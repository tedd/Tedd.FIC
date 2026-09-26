using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tedd.FIC;

internal static class Crc32C
{
    private const uint Poly = 0x82F63B78; // reflected 0x1EDC6F41
    private static readonly uint[] X2n = BuildX2n();

    private static uint[] BuildX2n()
    {
        var t = new uint[32];
        uint p = 1u << 30; // x^1
        t[0] = p;
        for (int k = 1; k < 32; k++) t[k] = p = MultModP(p, p);
        return t;
    }

    private static uint MultModP(uint a, uint b)
    {
        uint m = 1u << 31, p = 0;
        while (m != 0)
        {
            if ((a & m) != 0)
            {
                p ^= b;
                if ((a & (m - 1)) == 0) break;
            }
            m >>= 1;
            b = (b & 1) != 0 ? (b >> 1) ^ Poly : b >> 1;
        }
        return p;
    }

    private static uint X2nModP(long n, int k)
    {
        uint p = 1u << 31; // x^0
        while (n != 0)
        {
            if ((n & 1) != 0) p = MultModP(X2n[k & 31], p);
            n >>= 1;
            k++;
        }
        return p;
    }

    public static uint Combine(uint crcA, uint crcB, long lengthB) =>
        lengthB == 0 ? crcA : MultModP(ShiftFor(lengthB), crcA) ^ crcB;

    [ThreadStatic] private static long[]? _cacheLen;
    [ThreadStatic] private static uint[]? _cacheVal;
    [ThreadStatic] private static int _cacheNext;

    private static uint ShiftFor(long len)
    {
        var lens = _cacheLen ??= new long[4];
        var vals = _cacheVal ??= new uint[4];
        for (int i = 0; i < 4; i++) if (lens[i] == len) return vals[i];
        uint v = X2nModP(len, 3);
        int k = _cacheNext;
        _cacheNext = (k + 1) & 3;
        lens[k] = len;
        vals[k] = v;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        int i = 0;
        ref byte p = ref MemoryMarshal.GetReference(data);
        for (; i + 8 <= data.Length; i += 8)
            c = BitOperations.Crc32C(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i)));
        for (; i < data.Length; i++)
            c = BitOperations.Crc32C(c, Unsafe.Add(ref p, i));
        return ~c;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 * 1024) return Append(0, data);
        int part = data.Length / 3 & ~7;
        uint a = 0xFFFFFFFF, b = 0xFFFFFFFF, c = 0xFFFFFFFF;
        ref byte p0 = ref MemoryMarshal.GetReference(data);
        ref byte p1 = ref Unsafe.Add(ref p0, part);
        ref byte p2 = ref Unsafe.Add(ref p0, 2 * part);
        int i0 = 0;
        for (; i0 + 16 <= part; i0 += 16)
        {
            a = BitOperations.Crc32C(a, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p0, i0)));
            b = BitOperations.Crc32C(b, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p1, i0)));
            c = BitOperations.Crc32C(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p2, i0)));
            a = BitOperations.Crc32C(a, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p0, i0 + 8)));
            b = BitOperations.Crc32C(b, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p1, i0 + 8)));
            c = BitOperations.Crc32C(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p2, i0 + 8)));
        }
        for (; i0 < part; i0 += 8)
        {
            a = BitOperations.Crc32C(a, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p0, i0)));
            b = BitOperations.Crc32C(b, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p1, i0)));
            c = BitOperations.Crc32C(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p2, i0)));
        }
        int j = 3 * part;
        for (; j + 8 <= data.Length; j += 8) c = BitOperations.Crc32C(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p0, j)));
        for (; j < data.Length; j++) c = BitOperations.Crc32C(c, Unsafe.Add(ref p0, j));
        return Combine(Combine(~a, ~b, part), ~c, data.Length - 2L * part);
    }

    public static unsafe uint Compute(ReadOnlySpan<byte> data, int threads)
    {
        const int MinChunk = 1 << 20;
        int chunks = (int)Math.Min(threads, data.Length / MinChunk);
        if (chunks <= 1) return Compute(data);
        long size = data.Length / chunks;
        var crcs = new uint[chunks];
        fixed (byte* p = data)
        {
            nint addr = (nint)p;
            int length = data.Length;
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
            {
                long start = k * size, end = k == chunks - 1 ? length : start + size;
                crcs[k] = Compute(new ReadOnlySpan<byte>((byte*)addr + start, (int)(end - start)));
            });
        }
        uint crc = crcs[0];
        for (int k = 1; k < chunks; k++)
            crc = Combine(crc, crcs[k], k == chunks - 1 ? data.Length - k * size : size);
        return crc;
    }
}
