using System.Runtime.InteropServices;

namespace Tedd.FIC;

/// <summary>Per-worker aligned native buffers, retained on the same thread only within bounded cache limits.</summary>
internal sealed unsafe class EncScratch : IDisposable
{
    public const int CandA = 0, CandB = 1, R0 = 2, QpPixels = 3, QpPred = 4, QpMasks = 5, Blk = 6, Count = 7;

    public static long RetainBytes = 8L << 20;
    public static long RetainTotal = 16L << 20;
    private static long _retained;

    public static long Retained => Volatile.Read(ref _retained);
    private long _held;

    [ThreadStatic] private static EncScratch? _cached;

    private readonly byte*[] _p = new byte*[Count];
    private readonly long[] _n = new long[Count];
    private long _total;

    /// <summary>Takes the calling thread's cached scratch, or allocates a new instance.</summary>
    public static EncScratch Rent()
    {
        var s = _cached;
        _cached = null;
        if (s == null) return new EncScratch();
        Interlocked.Add(ref _retained, -s._held);
        s._held = 0;
        return s;
    }

    /// <summary>Returns scratch to the calling thread's bounded cache or frees it.</summary>
    public static void Return(EncScratch s)
    {
        if (s._total <= RetainBytes && _cached == null)
        {
            if (Interlocked.Add(ref _retained, s._total) <= RetainTotal) { s._held = s._total; _cached = s; return; }
            Interlocked.Add(ref _retained, -s._total);
        }
        s.Dispose();
    }

    internal static bool Poison;
    private static int _poisonSeed;

    /// <summary>Gets at least the requested capacity in a 64-byte-aligned slot; contents are unspecified.</summary>
    public byte* Get(int slot, long bytes)
    {
        if (Poison)
        {
            byte* q = GetRaw(slot, bytes);
            var rnd = new Random(Interlocked.Increment(ref _poisonSeed));
            rnd.NextBytes(new Span<byte>(q, (int)Math.Min(bytes, int.MaxValue)));
            return q;
        }
        return GetRaw(slot, bytes);
    }

    private byte* GetRaw(int slot, long bytes)
    {
        if (bytes > _n[slot])
        {
            if (_p[slot] != null) NativeMemory.AlignedFree(_p[slot]);
            _total -= _n[slot];
            _p[slot] = null;
            _n[slot] = 0;
            _p[slot] = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(bytes, 64), 64);
            _n[slot] = bytes;
            _total += bytes;
        }
        return _p[slot];
    }

    /// <summary>Frees the native buffers owned by this instance.</summary>
    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~EncScratch()
    {
        if (_held != 0) Interlocked.Add(ref _retained, -_held);
        Free();
    }

    private void Free()
    {
        for (int i = 0; i < Count; i++)
        {
            if (_p[i] != null) NativeMemory.AlignedFree(_p[i]);
            _p[i] = null;
            _n[i] = 0;
        }
        _total = 0;
    }

    /// <summary>Runs each strip action with worker-local scratch and returns all scratch before completion.</summary>
    public static void ForStrips(int n, int threads, Action<int, EncScratch> body)
    {
        if (threads <= 1 || n == 1)
        {
            var s = Rent();
            try { for (int i = 0; i < n; i++) body(i, s); }
            finally { Return(s); }
            return;
        }
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = threads },
            Rent,
            (i, _, s) => { body(i, s); return s; },
            Return);
    }
}

/// <summary>Owns an exact-sized native copy of one encoded strip.</summary>
internal unsafe struct NativePayload : IDisposable
{
    public byte* Ptr;
    public int Len;

    /// <summary>Copies a strip payload into owned native memory.</summary>
    public static NativePayload Copy(byte* src, int len)
    {
        var p = new NativePayload { Ptr = (byte*)NativeMemory.Alloc((nuint)Math.Max(len, 1)), Len = len };
        Buffer.MemoryCopy(src, p.Ptr, len, len);
        return p;
    }

    public readonly ReadOnlySpan<byte> Span => new(Ptr, Len);

    /// <summary>Releases the payload and clears its pointer and length.</summary>
    public void Dispose()
    {
        if (Ptr != null) NativeMemory.Free(Ptr);
        Ptr = null;
        Len = 0;
    }
}
