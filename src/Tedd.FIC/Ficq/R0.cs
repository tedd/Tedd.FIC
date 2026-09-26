using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tedd.FIC;

/// <summary>Shared MED prediction, Rice modeling, and row transforms for the R0 strip codec.</summary>
internal static unsafe class R0
{
    public const int L = 16;
    public const int MaxK = 8;
    public const int Pad = 32;
    public const int AMax = 255;

    public static readonly short[] Th =
    [
        6, 13, 30, 70, 157, 256, 256, 256,
        7, 18, 35, 86, 213, 256, 256, 256,
        7, 17, 35, 82, 209, 256, 256, 256,
        8, 16, 32, 64, 128, 256, 256, 256,
    ];

    /// <summary>Gets the Rice bit cost of a zigzag residual, including an escape for large quotients.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RiceBits(int k, int z)
    {
        int q = z >> k;
        return q < L ? q + 1 + k : L + 1 + k + 8;
    }

    /// <summary>Computes activity from neighboring residual rows with AVX2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Act(byte* zn, byte* znn, ushort* act, int w)
    {
        var cap = Vector256.Create((ushort)AMax);
        for (int x = 0; x < w; x += 16)
        {
            var n = Avx2.ConvertToVector256Int16(zn + x).AsUInt16();
            var nw = Avx2.ConvertToVector256Int16(zn + x - 1).AsUInt16();
            var ne = Avx2.ConvertToVector256Int16(zn + x + 1).AsUInt16();
            var nn = Avx2.ConvertToVector256Int16(znn + x).AsUInt16();
            Avx2.Min((n << 1) + nw + ne + nn, cap).Store(act + x);
        }
    }

    /// <summary>Maps activity to per-pixel Rice parameters for a color plane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void KRow(int p, ushort* act, byte* k, int w)
    {
        fixed (short* th = Th)
        {
            short* t = th + p * 8;
            var t0 = Vector256.Create((short)(t[0] - 1)); var t1 = Vector256.Create((short)(t[1] - 1));
            var t2 = Vector256.Create((short)(t[2] - 1)); var t3 = Vector256.Create((short)(t[3] - 1));
            var t4 = Vector256.Create((short)(t[4] - 1));
            for (int x = 0; x < w; x += 16)
            {
                var a = Vector256.Load((short*)act + x);
                var kv = Vector256<short>.Zero - Avx2.CompareGreaterThan(a, t0) - Avx2.CompareGreaterThan(a, t1) - Avx2.CompareGreaterThan(a, t2) -
                         Avx2.CompareGreaterThan(a, t3) - Avx2.CompareGreaterThan(a, t4);
                var pk = Avx2.PackUnsignedSaturate(kv, kv);
                Avx2.Permute4x64(pk.AsUInt64(), 0b1000).GetLower().AsByte().Store(k + x);
            }
        }
    }

    /// <summary>Portable activity calculation for a residual row.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ActS(byte* zn, byte* znn, ushort* act, int w)
    {
        var cap = Vector128.Create((ushort)AMax);
        int x = 0;
        for (; x + 8 <= w; x += 8)
        {
            var n = Vector128.WidenLower(Vector128.Load(zn + x));
            var nw = Vector128.WidenLower(Vector128.Load(zn + x - 1));
            var ne = Vector128.WidenLower(Vector128.Load(zn + x + 1));
            var nn = Vector128.WidenLower(Vector128.Load(znn + x));
            Vector128.Min((n << 1) + nw + ne + nn, cap).Store(act + x);
        }
        for (; x < w; x++)
        {
            int a = 2 * zn[x] + zn[x - 1] + zn[x + 1] + znn[x] - AMax;
            act[x] = (ushort)(AMax + (a & (a >> 31))); // min(a, 255), branch-free
        }
    }

    /// <summary>Portable activity-to-Rice-parameter mapping.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void KRowS(int p, ushort* act, byte* k, int w)
    {
        int t0 = Th[p * 8], t1 = Th[p * 8 + 1], t2 = Th[p * 8 + 2], t3 = Th[p * 8 + 3], t4 = Th[p * 8 + 4];
        var v0 = Vector128.Create((ushort)t0); var v1 = Vector128.Create((ushort)t1); var v2 = Vector128.Create((ushort)t2);
        var v3 = Vector128.Create((ushort)t3); var v4 = Vector128.Create((ushort)t4);
        int x = 0;
        for (; x + 8 <= w; x += 8)
        {
            var a = Vector128.Load(act + x);
            var kv = Vector128<ushort>.Zero - Vector128.GreaterThanOrEqual(a, v0) - Vector128.GreaterThanOrEqual(a, v1) - Vector128.GreaterThanOrEqual(a, v2) -
                     Vector128.GreaterThanOrEqual(a, v3) - Vector128.GreaterThanOrEqual(a, v4);
            *(ulong*)(k + x) = Vector128.Narrow(kv, kv).AsUInt64().ToScalar();
        }
        for (; x < w; x++)
        {
            int a = act[x];
            k[x] = (byte)((a >= t0 ? 1 : 0) + (a >= t1 ? 1 : 0) + (a >= t2 ? 1 : 0) + (a >= t3 ? 1 : 0) + (a >= t4 ? 1 : 0));
        }
    }

    /// <summary>Predicts one channel from west, north, and northwest values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Med(int W, int N, int NW)
    {
        int d = W - N, m = d & (d >> 31);
        int lo = N + m, hi = W - m;
        int p = W + N - NW;
        int a = p - lo; p = lo + (a & ~(a >> 31));   // max(p, lo)
        int b = p - hi; return hi + (b & (b >> 31)); // min(p, hi)
    }

    /// <summary>Applies MED independently to four packed byte channels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Med4(uint w, uint n, uint nw) =>
        (uint)Med((int)(w & 255), (int)(n & 255), (int)(nw & 255)) |
        (uint)Med((int)(w >> 8 & 255), (int)(n >> 8 & 255), (int)(nw >> 8 & 255)) << 8 |
        (uint)Med((int)(w >> 16 & 255), (int)(n >> 16 & 255), (int)(nw >> 16 & 255)) << 16 |
        (uint)Med((int)(w >> 24), (int)(n >> 24), (int)(nw >> 24)) << 24;

    /// <summary>Produces planar zigzag residual rows through the portable path.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ZRowsS(int w, uint* cur, uint* prv, byte* z0, byte* z1, byte* z2, byte* z3)
    {
        int x = 0;
        if (w >= 3)
        {
            ZRowsOne(0, cur, prv, z0, z1, z2, z3);
            for (x = 1; x + 2 <= w; x += 2)
            {
                var V = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(cur + x)).AsByte()).AsInt16();
                var W = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(cur + x - 1)).AsByte()).AsInt16();
                var N = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(prv + x)).AsByte()).AsInt16();
                var NW = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(prv + x - 1)).AsByte()).AsInt16();
                var pred = Vector128.Min(Vector128.Max(W + N - NW, Vector128.Min(W, N)), Vector128.Max(W, N));
                var d = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(V - pred, 8), 8);
                var zz = (d << 1) ^ Vector128.ShiftRightArithmetic(d, 15);
                ulong zv = Vector128.Narrow(zz.AsUInt16(), zz.AsUInt16()).AsUInt64().ToScalar();
                z0[x] = (byte)zv; z1[x] = (byte)(zv >> 8); z2[x] = (byte)(zv >> 16); z3[x] = (byte)(zv >> 24);
                z0[x + 1] = (byte)(zv >> 32); z1[x + 1] = (byte)(zv >> 40); z2[x + 1] = (byte)(zv >> 48); z3[x + 1] = (byte)(zv >> 56);
            }
        }
        for (; x < w; x++) ZRowsOne(x, cur, prv, z0, z1, z2, z3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ZRowsOne(int x, uint* cur, uint* prv, byte* z0, byte* z1, byte* z2, byte* z3)
    {
        {
            uint v = cur[x], n = prv[x], wv = x > 0 ? cur[x - 1] : n, nw = x > 0 ? prv[x - 1] : n;
            uint pred = Med4(wv, n, nw);
            static byte Z(uint v, uint p, int b) { int d = (sbyte)(byte)((v >> b) - (p >> b)); return (byte)((d << 1) ^ (d >> 31)); }
            z0[x] = Z(v, pred, 0); z1[x] = Z(v, pred, 8); z2[x] = Z(v, pred, 16); z3[x] = Z(v, pred, 24);
        }
    }

    /// <summary>Replicates edge residuals into the row padding read by the activity kernels.</summary>
    public static void PadRow(byte* z, int w)
    {
        z[-1] = z[-2] = z[0];
        z[w] = z[w + 1] = z[w - 1];
    }

    private static readonly Vector256<byte> PlaneSplit = Vector256.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15,
                                                                          0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15);
    private static readonly Vector256<int> PlaneGather = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);

    /// <summary>Produces planar zigzag residual rows eight pixels at a time with AVX2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ZRows8(int w, uint* cur, uint* prv, byte* z0, byte* z1, byte* z2, byte* z3)
    {
        {
            int d0 = 0;
            uint v = cur[0], n = prv[0];
            for (int b = 0; b < 32; b += 8)
            {
                int d = (sbyte)(byte)((v >> b) - (n >> b));
                d0 |= ((d << 1) ^ (d >> 31)) << b;
            }
            z0[0] = (byte)d0; z1[0] = (byte)(d0 >> 8); z2[0] = (byte)(d0 >> 16); z3[0] = (byte)(d0 >> 24);
        }
        var split = PlaneSplit;
        var gather = PlaneGather;
        for (int x = 1; x < w; x += 8)
        {
            var V = Vector256.Load((byte*)(cur + x));
            var W = Vector256.Load((byte*)(cur + x - 1));
            var N = Vector256.Load((byte*)(prv + x));
            var NW = Vector256.Load((byte*)(prv + x - 1));
            var g = Avx2.SubtractSaturate(Avx2.AddSaturate(W, Avx2.SubtractSaturate(N, NW)), Avx2.SubtractSaturate(NW, N));
            var pred = Avx2.Min(Avx2.Max(g, Avx2.Min(W, N)), Avx2.Max(W, N));
            var d = V - pred;
            var zz = (d + d) ^ Avx2.CompareGreaterThan(Vector256<sbyte>.Zero, d.AsSByte()).AsByte();
            var t = Avx2.PermuteVar8x32(Avx2.Shuffle(zz, split).AsInt32(), gather).AsUInt64();
            *(ulong*)(z0 + x) = t.GetElement(0);
            *(ulong*)(z1 + x) = t.GetElement(1);
            *(ulong*)(z2 + x) = t.GetElement(2);
            *(ulong*)(z3 + x) = t.GetElement(3);
        }
    }

    /// <summary>Produces planar zigzag residual rows with the SSE implementation.</summary>
    public static void ZRows(int w, uint* cur, uint* prv, byte* z0, byte* z1, byte* z2, byte* z3)
    {
        var sel = Vector128.Create((byte)0, 4, 1, 5, 2, 6, 3, 7, 8, 8, 8, 8, 8, 8, 8, 8);
        {
            var N = Sse41.ConvertToVector128Int16((byte*)prv);
            var V = Sse41.ConvertToVector128Int16((byte*)cur);
            var d = Sse2.ShiftRightArithmetic(Sse2.ShiftLeftLogical(V - N, 8), 8);
            var zz = (d << 1) ^ Sse2.ShiftRightArithmetic(d, 15);
            uint zv = Sse2.PackUnsignedSaturate(zz, zz).AsUInt32().ToScalar();
            z0[0] = (byte)zv; z1[0] = (byte)(zv >> 8); z2[0] = (byte)(zv >> 16); z3[0] = (byte)(zv >> 24);
        }
        for (int x = 1; x < w; x += 2)
        {
            var N = Sse41.ConvertToVector128Int16((byte*)(prv + x));
            var W = Sse41.ConvertToVector128Int16((byte*)(cur + x - 1));
            var NW = Sse41.ConvertToVector128Int16((byte*)(prv + x - 1));
            var V = Sse41.ConvertToVector128Int16((byte*)(cur + x));
            var pred = Vector128.Min(Vector128.Max(W - NW + N, Vector128.Min(W, N)), Vector128.Max(W, N));
            var d = Sse2.ShiftRightArithmetic(Sse2.ShiftLeftLogical(V - pred, 8), 8);
            var zz = (d << 1) ^ Sse2.ShiftRightArithmetic(d, 15);
            ulong zv = Ssse3.Shuffle(Sse2.PackUnsignedSaturate(zz, zz), sel).AsUInt64().ToScalar();
            *(ushort*)(z0 + x) = (ushort)zv; *(ushort*)(z1 + x) = (ushort)(zv >> 16);
            *(ushort*)(z2 + x) = (ushort)(zv >> 32); *(ushort*)(z3 + x) = (ushort)(zv >> 48);
        }
    }

    /// <summary>Writes an unsigned LEB128 length and returns its byte count.</summary>
    public static int PutVarint(byte* p, long v)
    {
        int n = 0;
        while (v >= 0x80) { p[n++] = (byte)(v | 0x80); v >>= 7; }
        p[n++] = (byte)v;
        return n;
    }

    /// <summary>Reads a minimal unsigned LEB128 length, or -1 for malformed input.</summary>
    public static long GetVarint(byte* p, byte* end, ref int pos)
    {
        long v = 0;
        for (int sh = 0; sh <= 28; sh += 7)
        {
            if (p + pos >= end) return -1;
            int b = p[pos++];
            v |= (long)(b & 0x7F) << sh;
            if (b < 0x80) return b == 0 && sh > 0 ? -1 : v;
        }
        return -1;
    }

    /// <summary>Gets a padded scratch stride for residual rows and vector reads.</summary>
    public static int RowStride(int w) => Math.Max(w, 64) + 2 * Pad + 64;

    /// <summary>Rounds a native pointer up to a 64-byte boundary.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* Align64(byte* p) => (byte*)(((nint)p + 63) & ~(nint)63);
}

/// <summary>Encodes planar MED residuals with adaptive Rice parameters and optional zero-group masks.</summary>
internal sealed unsafe class R0Encoder
{
    private const int Pad = R0.Pad;
    private readonly int[] _hist = new int[4 * 256];

    [ThreadStatic] private static R0Encoder? _local;
    public static R0Encoder Local => _local ??= new R0Encoder();

    public bool Masks = true;

    /// <summary>Gets a safe output capacity for one R0 strip.</summary>
    public static long MaxPayload(int w, int rows, int P) => 3 + 15 + (long)w * rows * P * (25 + 8 + 8 + 1) / 8 + (long)rows * P + 64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> ToPlanes8(Vector256<uint> v, Vector256<uint> amask)
    {
        var b80 = Vector256.Create((byte)0x80);
        var one = Vector256.Create((byte)1);
        var vs = (v << 8).AsByte();
        var vb = v.AsByte();
        var v1 = vs - vb + b80;
        var av = Avx2.Average(vs, vb) - ((vs ^ vb) & one);
        var v2 = vb - (av.AsUInt32() << 8).AsByte() + b80;
        return ((v >> 8) & Vector256.Create(0xFFu)) | (v1.AsUInt32() & Vector256.Create(0xFF00u)) |
               (v2.AsUInt32() & Vector256.Create(0xFF0000u)) | (v & amask);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Planes4(byte* s, uint* cur, int w, uint amask, bool simd)
    {
        var am = Vector256.Create(amask);
        int x = 0;
        for (; simd && x + 8 <= w; x += 8) ToPlanes8(Vector256.Load((uint*)(s + x * 4)), am).Store(cur + x);
        for (; x < w; x++)
        {
            uint v = *(uint*)(s + x * 4);
            int r = (int)(v & 255), g = (int)(v >> 8 & 255), b = (int)(v >> 16 & 255);
            cur[x] = (uint)g | ((uint)(r - g + 128) & 255) << 8 | ((uint)(b - ((r + g) >> 1) + 128) & 255) << 16 | (v & amask);
        }
    }

    private static readonly Vector128<byte> Rgb3to4 = Vector128.Create((byte)0, 1, 2, 128, 3, 4, 5, 128, 6, 7, 8, 128, 9, 10, 11, 128);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Planes3(byte* s, uint* cur, int w, bool simd)
    {
        var sh = Rgb3to4;
        int x = 0;
        for (; simd && x + 10 <= w; x += 8)
        {
            var lo = Ssse3.Shuffle(Vector128.Load(s + x * 3), sh);
            var hi = Ssse3.Shuffle(Vector128.Load(s + x * 3 + 12), sh);
            ToPlanes8(Vector256.Create(lo, hi).AsUInt32(), Vector256<uint>.Zero).Store(cur + x);
        }
        for (; x < w; x++)
        {
            int r = s[x * 3], g = s[x * 3 + 1], b = s[x * 3 + 2];
            cur[x] = (uint)g | ((uint)(r - g + 128) & 255) << 8 | ((uint)(b - ((r + g) >> 1) + 128) & 255) << 16;
        }
    }

    private struct Bits
    {
        public ulong Qa, Ra;
        public int Qn, Rn, En;
        public byte* Qp, Rp, Ep;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Append(ref byte* p, ref ulong acc, ref int nb, ulong v, int n)
    {
        ulong lo = acc | (v << nb);
        ulong hi = nb == 0 ? 0 : v >> (64 - nb);
        *(ulong*)p = lo;
        *(ulong*)(p + 8) = hi;
        int total = nb + n, bytes = total >> 3;
        p += bytes;
        acc = bytes == 8 ? hi : (lo >> (bytes * 8)) | ((hi << 1) << (63 - bytes * 8));
        nb = total & 7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put(ref byte* p, ref ulong acc, ref int nb, ulong v, int n)
    {
        ulong a = acc | (v << nb);
        int t = nb + n;
        *(ulong*)p = a;
        int bytes = t >> 3;
        p += bytes;
        acc = a >> (bytes << 3);
        nb = t & 7;
    }

    private static readonly Vector128<byte> MaskLut = Vector128.Create((byte)0, 1, 3, 7, 15, 31, 63, 127, 255, 255, 255, 255, 255, 255, 255, 255);

    /// <summary>Writes Rice quotients, remainders, and escaped values for one plane row.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Emit(byte* z, byte* kp, int w, ref Bits st, bool simd)
    {
        ulong qa = st.Qa, ra = st.Ra;
        int qn = st.Qn, rn = st.Rn, en = st.En;
        byte* qp = st.Qp, rp = st.Rp, ep = st.Ep;
        var maskLut = MaskLut;
        var fifteen = Vector256.Create(15u);
        int x = 0;
        for (; simd && x + 8 <= w; x += 8)
        {
            ulong k8 = *(ulong*)(kp + x);
            var zv = Avx2.ConvertToVector256Int32(z + x).AsUInt32();
            var kv = Avx2.ConvertToVector256Int32(kp + x).AsUInt32();
            var qv = Avx2.ShiftRightLogicalVariable(zv, kv);
            if (!Vector256.LessThanOrEqualAll(qv, fifteen))
            {
                for (int j = 0; j < 8; j++)
                {
                    int zz = z[x + j], k = kp[x + j];
                    int q = zz >> k;
                    if (q >= R0.L) { ep[en++] = (byte)q; q = R0.L; }
                    Append(ref qp, ref qa, ref qn, 1ul << q, q + 1);
                    Append(ref rp, ref ra, ref rn, (ulong)(uint)(zz & ((1 << k) - 1)), k);
                }
                continue;
            }
            ulong kmask = Ssse3.Shuffle(maskLut, Vector128.CreateScalarUnsafe(k8).AsByte()).AsUInt64().ToScalar();
            ulong z8 = *(ulong*)(z + x);
            int nr = BitOperations.PopCount(kmask);
            ulong rb = Bmi2.X64.ParallelBitExtract(z8, kmask);
            if (nr <= 56) Put(ref rp, ref ra, ref rn, rb, nr);
            else Append(ref rp, ref ra, ref rn, rb, nr);
            var code = Avx2.ShiftLeftLogicalVariable(Vector256<uint>.One, qv);
            var lens = (code << 1) - Vector256<uint>.One;
            var cp = Avx2.PackUnsignedSaturate(code.AsInt32(), lens.AsInt32()).AsUInt64();
            ulong mLo = cp.GetElement(1), mHi = cp.GetElement(3);
            ulong bLo = Bmi2.X64.ParallelBitExtract(cp.GetElement(0), mLo), bHi = Bmi2.X64.ParallelBitExtract(cp.GetElement(2), mHi);
            int nLo = BitOperations.PopCount(mLo), nHi = BitOperations.PopCount(mHi);
            if (nLo + nHi <= 56) Put(ref qp, ref qa, ref qn, bLo | bHi << nLo, nLo + nHi);
            else if (nLo + nHi <= 64) Append(ref qp, ref qa, ref qn, bLo | bHi << nLo, nLo + nHi);
            else
            {
                Append(ref qp, ref qa, ref qn, bLo, nLo);
                Append(ref qp, ref qa, ref qn, bHi, nHi);
            }
        }
        for (; x < w; x++)
        {
            int zz = z[x], k = kp[x];
            int q = zz >> k;
            if (q >= R0.L) { ep[en++] = (byte)q; q = R0.L; }
            Put(ref qp, ref qa, ref qn, 1ul << q, q + 1);
            Put(ref rp, ref ra, ref rn, (ulong)(uint)(zz & ((1 << k) - 1)), k);
        }
        st.Qa = qa; st.Ra = ra; st.Qn = qn; st.Rn = rn; st.En = en; st.Qp = qp; st.Rp = rp;
    }

    /// <summary>Encodes a strip and returns the number of payload bytes written.</summary>
    public int Encode(byte* src, int w, int rows, int ch, int P, byte* o, EncScratch? sc = null, bool simd = true)
    {
        long n = (long)w * rows;
        int cap = Math.Max(w, 64);
        int rs = R0.RowStride(w);
        int gw = ((w + 7) >> 3) / 64 + 1;
        long rowBytes = 12L * rs + rs + rs + 2L * rs + 8L * (cap + 16) + rs + rs + 8L * (cap / 512 + 4) + 12 * 64;
        long mmax = n * 4 / 8 + (n / Math.Max(w, 1) + 1) * 4 / 8 + 64;
        long qmax = n * 4 * 25 / 8 + 64, rmax = n * 4 + 64, emax = n * 4 + 64;
        long total = rowBytes + mmax + qmax + rmax + emax + 6 * 64;
        var own = sc == null ? new EncScratch() : null;
        try
        {
            {
                byte* b = R0.Align64((sc ?? own)!.Get(EncScratch.R0, total + 64));
                byte* zb = b; b = R0.Align64(b + 12L * rs);
                byte* kp = b; b = R0.Align64(b + rs);
                byte* zero0 = b; b = R0.Align64(b + rs);
                new Span<byte>(zero0, rs).Clear();
                ushort* act = (ushort*)b; b = R0.Align64(b + 2L * rs);
                uint* plb = (uint*)b; b = R0.Align64(b + 8L * (cap + 16));
                byte* zc = b; b = R0.Align64(b + rs);
                byte* kc = b; b = R0.Align64(b + rs);
                ulong* gbits = (ulong*)b; b = R0.Align64(b + 8L * (cap / 512 + 4));
                byte* mo = b; b = R0.Align64(b + mmax);
                byte* qo = b; b = R0.Align64(b + qmax);
                byte* ro = b; b = R0.Align64(b + rmax);
                byte* eo = b;
                return EncodeCore(src, w, rows, ch, P, o, rs, cap, gw, zb, kp, zero0, act, plb, zc, kc, gbits, mo, qo, ro, eo, Isa.Use(simd));
            }
        }
        finally { own?.Dispose(); }
    }

    /// <summary>Transforms rows, chooses Rice parameters, and emits the mask, quotient, remainder, and escape streams.</summary>
    private int EncodeCore(byte* src, int w, int rows, int ch, int P, byte* o, int rs, int cap, int gw, byte* zb, byte* kp, byte* zero0,
        ushort* act, uint* plb, byte* zc, byte* kc, ulong* gbits, byte* mo, byte* qo, byte* ro, byte* eo, bool simd)
    {
        int qlen, rlen, le, mlen;
        bool plain0 = false;
        bool masks = Masks;
        int* ks = stackalloc int[4];
        ulong ma = 0; int mn = 0;
        fixed (int* hist = _hist)
        {
            byte* zero = zero0 + Pad;
            byte* mp = mo;
            uint* prv = plb, cur = plb + cap + 16;
            new Span<uint>(prv, w + 16).Clear();
            var st = new Bits { Qp = qo, Rp = ro, Ep = eo };
            int slot = 0;
            for (int y = 0; y < rows; y++)
            {
                byte* srow = src + (long)y * w * ch;
                if (ch == 4) Planes4(srow, cur, w, P == 4 ? 0xFF000000u : 0, simd); else Planes3(srow, cur, w, simd);
                byte* z0 = zb + (0 * 3 + slot) * rs + Pad, z1 = zb + (1 * 3 + slot) * rs + Pad;
                byte* z2 = zb + (2 * 3 + slot) * rs + Pad, z3 = zb + (3 * 3 + slot) * rs + Pad;
                if (simd) R0.ZRows8(w, cur, prv, z0, z1, z2, z3);
                else R0.ZRowsS(w, cur, prv, z0, z1, z2, z3);
                for (int p = 0; p < P; p++) R0.PadRow(zb + (p * 3 + slot) * rs + Pad, w);
                if (y == 0)
                {
                    long plain = 0, model = 0;
                    new Span<int>(hist, 4 * 256).Clear();
                    for (int p = 0; p < P; p++)
                    {
                        byte* z = zb + (p * 3 + slot) * rs + Pad;
                        int* hp = hist + p * 256;
                        for (int x = 0; x < w; x++) hp[z[x]]++;
                        long best = long.MaxValue;
                        for (int k = 0; k <= R0.MaxK; k++)
                        {
                            long c = 4;
                            for (int v = 0; v < 256; v++) if (hp[v] != 0) c += (long)hp[v] * R0.RiceBits(k, v);
                            if (c < best) { best = c; ks[p] = k; }
                        }
                        plain += best;
                        int k0 = 0;
                        while (k0 < 8 && R0.Th[p * 8 + k0] <= 0) k0++;
                        for (int v = 0; v < 256; v++) if (hp[v] != 0) model += (long)hp[v] * R0.RiceBits(k0, v);
                    }
                    plain0 = plain < model;
                }
                for (int p = 0; p < P; p++)
                {
                    byte* z = zb + (p * 3 + slot) * rs + Pad;
                    if (y == 0 && plain0) new Span<byte>(kp, w + 8).Fill((byte)ks[p]);
                    else
                    {
                        byte* zn = y == 0 ? zero : zb + (p * 3 + (slot + 2) % 3) * rs + Pad;
                        byte* znn = y <= 1 ? zn : zb + (p * 3 + (slot + 1) % 3) * rs + Pad;
                        if (simd) { R0.Act(zn, znn, act, w); R0.KRow(p, act, kp, w); }
                        else { R0.ActS(zn, znn, act, w); R0.KRowS(p, act, kp, w); }
                    }
                    if (!masks) { Emit(z, kp, w, ref st, simd); continue; }
                    int G = (w + 7) >> 3, tail = w & 7, Gf = w >> 3;
                    long saved;
                    for (int i = 0; i < gw; i++) gbits[i] = 0;
                    {
                        var sacc = Vector256<ulong>.Zero;
                        int g = 0;
                        for (; simd && g + 4 <= Gf; g += 4)
                        {
                            var eq = Avx2.CompareEqual(Vector256.Load((ulong*)(z + 8 * g)), Vector256<ulong>.Zero);
                            int b4 = Avx.MoveMask(eq.AsDouble());
                            var sad = Avx2.SumAbsoluteDifferences(Vector256.Load(kp + 8 * g), Vector256<byte>.Zero).AsUInt64() & eq;
                            sacc += sad + (Vector256.Create(8ul) & eq);
                            gbits[g >> 6] |= (ulong)b4 << (g & 63);
                        }
                        saved = (long)Vector256.Sum(sacc);
                        for (; g < G; g++)
                        {
                            ulong lane = g == Gf ? (1ul << (8 * tail)) - 1 : ulong.MaxValue;
                            if ((*(ulong*)(z + 8 * g) & lane) != 0) continue;
                            ulong kk = *(ulong*)(kp + 8 * g) & lane;
                            saved += (g == Gf ? tail : 8) + (long)((kk * 0x0101010101010101UL) >> 56);
                            gbits[g >> 6] |= 1ul << (g & 63);
                        }
                    }
                    bool masked = saved > G;
                    MPut(ref mp, ref ma, ref mn, masked ? 1u : 0u, 1);
                    if (!masked) { Emit(z, kp, w, ref st, simd); continue; }
                    int j = 0;
                    for (int g = 0; g < G; g++)
                    {
                        *(ulong*)(zc + 8 * j) = *(ulong*)(z + 8 * g);
                        *(ulong*)(kc + 8 * j) = *(ulong*)(kp + 8 * g);
                        j += 1 - (int)(gbits[g >> 6] >> (g & 63) & 1);
                    }
                    for (int g = 0; g < G; g += 32) MPut(ref mp, ref ma, ref mn, (uint)(gbits[g >> 6] >> (g & 63)), Math.Min(32, G - g));
                    int ncomp = 8 * j;
                    if (tail != 0 && (gbits[Gf >> 6] >> (Gf & 63) & 1) == 0) ncomp -= 8 - tail;
                    Emit(zc, kc, ncomp, ref st, simd);
                }
                { uint* t = prv; prv = cur; cur = t; }
                slot = (slot + 1) % 3;
            }
            if (st.Qn > 0) { *st.Qp = (byte)st.Qa; st.Qp++; }
            if (st.Rn > 0) { *st.Rp = (byte)st.Ra; st.Rp++; }
            qlen = (int)(st.Qp - qo);
            rlen = (int)(st.Rp - ro);
            le = st.En;
            while (mn > 0) { *mp++ = (byte)ma; ma >>= 8; mn = Math.Max(0, mn - 8); }
            mlen = (int)(mp - mo);
        }
        int pos = 0;
        ulong x0 = plain0 ? 1ul : 0;
        int xbits = 1;
        if (plain0) for (int p = 0; p < P; p++) { x0 |= (ulong)ks[p] << xbits; xbits += 4; }
        if (masks) x0 |= 1ul << xbits;
        xbits++;
        for (int i = 0; i < (xbits + 7) / 8; i++) o[pos++] = (byte)(x0 >> (8 * i));
        pos += R0.PutVarint(o + pos, le);
        pos += R0.PutVarint(o + pos, rlen);
        if (masks) pos += R0.PutVarint(o + pos, mlen);
        Buffer.MemoryCopy(eo, o + pos, le, le); pos += le;
        Buffer.MemoryCopy(ro, o + pos, rlen, rlen); pos += rlen;
        Buffer.MemoryCopy(mo, o + pos, mlen, mlen); pos += mlen;
        Buffer.MemoryCopy(qo, o + pos, qlen, qlen); pos += qlen;
        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MPut(ref byte* p, ref ulong acc, ref int nb, uint v, int n)
    {
        acc |= (ulong)v << nb;
        nb += n;
        if (nb >= 32) { *(uint*)p = (uint)acc; p += 4; acc >>= 32; nb -= 32; }
    }
}

/// <summary>Validates R0 substreams and reconstructs planar MED-coded strips.</summary>
internal sealed unsafe class R0Decoder
{
    private const int L = R0.L;
    private const int Pad = R0.Pad;

    [ThreadStatic] private static R0Decoder? _local;
    public static R0Decoder Local => _local ??= new R0Decoder();

    private byte* _rbp, _qp, _ep, _qbp, _mp;
    private long _roff, _rbits, _qpos, _qdone, _qlenB, _mpos, _mbits, _mbytes, _mavail, _nsymDec;
    private ulong _qcarry;
    private int _qi, _qn, _ei, _elen;
    private bool _bad, _simd;
    private Vector128<byte> _zbad;

    private static readonly Vector128<byte> MaskLut = Vector128.Create((byte)0, 1, 3, 7, 15, 31, 63, 127, 255, 255, 255, 255, 255, 255, 255, 255);
    private static readonly Vector128<byte> Pow2Lut = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly Vector128<byte> QLimLut = Vector128.Create((byte)255, 127, 63, 31, 15, 7, 3, 1, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly Vector128<byte> Rgb12 = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 128, 128, 128, 128);
    private static readonly ulong[] LutQ = new ulong[256];
    private static readonly byte[] LutC = new byte[256], LutT = new byte[256];

    static R0Decoder()
    {
        for (int v = 0; v < 256; v++)
        {
            ulong q = 0; int c = 0, z = 0;
            for (int b = 0; b < 8; b++)
            {
                if ((v >> b & 1) != 0) { q |= (ulong)z << (8 * c); c++; z = 0; }
                else z++;
            }
            LutQ[v] = q; LutC[v] = (byte)c; LutT[v] = (byte)z;
        }
    }

    /// <summary>Validates an R0 strip, decodes its substreams, and reconstructs the pixels.</summary>
    public bool Decode(byte* data, int len, long avail, int w, int rows, int ch, int P, byte* dst, bool simd = true)
    {
        if (len < 3 || w <= 0 || rows <= 0 || w > Ficq.MaxPlanarWidth || avail < len) return false;
        byte* end = data + len;
        bool plain0 = (data[0] & 1) != 0;
        int xbits = plain0 ? 1 + 4 * P : 1;
        ulong xv = 0;
        int xbytes = (xbits + 1 + 7) / 8;
        if (xbytes > len) return false;
        for (int i = 0; i < xbytes; i++) xv |= (ulong)data[i] << (8 * i);
        bool masks = (xv >> xbits & 1) != 0;
        if ((xv >> (xbits + 1)) != 0) return false; // unused bits must be 0
        int pos = xbytes;
        long le = R0.GetVarint(data, end, ref pos);
        long lr = R0.GetVarint(data, end, ref pos);
        long lm = masks ? R0.GetVarint(data, end, ref pos) : 0;
        if (le < 0 || lr < 0 || lm < 0 || pos + le + lr + lm > len) return false;
        long lq = len - pos - le - lr - lm;
        long nsym = (long)w * rows * P;
        if ((!masks && lq * 8 < nsym) || le > nsym || lm > ((long)rows * P * (1 + ((w + 7) >> 3)) + 7) / 8) return false;
        byte* ep = data + pos;
        byte* rp = ep + le;
        byte* mp = rp + lr;
        byte* qp = mp + lm;
        _mp = mp; _mpos = 0; _mbits = lm * 8; _mbytes = lm; _mavail = avail - (mp - data);
        int cap = Math.Max(w, 64);
        int rs = R0.RowStride(w);
        long rowBytes = rs + 12L * rs + rs + 2L * rs + 12L * (cap + 16) + 8L * (cap + 16) + (8L * cap + 512) + 4L * (cap / 256 + 4) + 10 * 64;
        long need = (rp - data) + lr + (long)P * w + 64;
        long rcopy = need > avail ? lr + (long)P * w + 64 : 0;
        byte[] arr = ArrayPool<byte>.Shared.Rent((int)(rowBytes + rcopy + 64));
        try
        {
            fixed (byte* basePtr = arr)
            {
                byte* b = R0.Align64(basePtr);
                var s = new Scr();
                s.K = b; b = R0.Align64(b + rs);
                s.Z = b; b = R0.Align64(b + 12L * rs);
                s.Zero = b; b = R0.Align64(b + rs);
                s.Act = (ushort*)b; b = R0.Align64(b + 2L * rs);
                s.Pl = (uint*)b; b = R0.Align64(b + 12L * (cap + 16));
                s.Eb = (uint*)b; b = R0.Align64(b + 8L * (cap + 16));
                s.Qb = b; b = R0.Align64(b + 8L * cap + 512);
                s.Gm = (uint*)b; b = R0.Align64(b + 4L * (cap / 256 + 4));
                s.Cap = cap; s.Rs = rs;
                new Span<byte>(s.Zero, rs).Clear();
                new Span<uint>(s.Pl, cap + 16).Clear();
                if (rcopy > 0)
                {
                    byte* rb = b;
                    Buffer.MemoryCopy(rp, rb, lr, lr);
                    new Span<byte>(rb + lr, (int)(rcopy - lr)).Clear();
                    rp = rb;
                }
                _simd = Isa.Use(simd);
                return Run(xv, plain0, masks, ep, (int)le, rp, lr, qp, lq, w, rows, ch, P, dst, ref s);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(arr);
            _rbp = _qp = _ep = _qbp = _mp = null;
        }
    }

    private struct Scr
    {
        public byte* K, Z, Zero, Qb;
        public ushort* Act;
        public uint* Pl, Eb, Gm;
        public int Cap, Rs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint MBits(int n)
    {
        if (_mpos + n > _mbits) { _bad = true; return 0; }
        long bo = _mpos >> 3;
        int sh = (int)(_mpos & 7);
        ulong v;
        if (bo + 8 <= _mavail) v = *(ulong*)(_mp + bo) >> sh;
        else
        {
            v = 0;
            for (int i = 0; i < 8 && bo + i < _mbytes; i++) v |= (ulong)_mp[bo + i] << (8 * i);
            v >>= sh;
        }
        _mpos += n;
        return (uint)(v & ((1ul << n) - 1));
    }

    private void PlaneRow(bool masks, byte* kp, byte* zo, int w, uint* gm)
    {
        if (!masks || MBits(1) == 0)
        {
            if (_simd) Seg(kp, 0, w, zo);
            else SegS(kp, 0, w, zo);
            _nsymDec += w;
            return;
        }
        int G = (w + 7) >> 3;
        for (int g = 0; g < G; g += 32) gm[g >> 5] = MBits(Math.Min(32, G - g));
        if (_simd) SegMasked(kp, w, zo, gm);
        else SegMaskedS(kp, w, zo, gm);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SegS(byte* k, int x0, int x1, byte* z)
    {
        byte* qb = _qbp, rb = _rbp;
        long roff = _roff;
        int qi = _qi;
        uint bad = 0;
        for (int x = x0; x < x1; x++)
        {
            int kk = k[x];
            uint q = qb[qi++];
            uint r = (*(uint*)(rb + (roff >> 3)) >> (int)(roff & 7)) & ((1u << kk) - 1);
            roff += kk;
            if (q >= L) q = Escape(q);
            uint v = (q << kk) | r;
            bad |= v >> 8;
            z[x] = (byte)v;
        }
        _roff = roff; _qi = qi;
        if (bad != 0) _bad = true;
    }

    private void SegMaskedS(byte* k, int w, byte* z, uint* gm)
    {
        int G = (w + 7) >> 3;
        long coded = 0;
        for (int g = 0; g < G; g++)
        {
            int x = g << 3, n = Math.Min(8, w - x);
            if ((gm[g >> 5] >> (g & 31) & 1) != 0) { for (int j = 0; j < n; j++) z[x + j] = 0; continue; }
            SegS(k, x, x + n, z);
            coded += n;
        }
        _nsymDec += coded;
    }

    /// <summary>Consumes each plane row and checks that all coded substreams end at their declared lengths.</summary>
    private bool Run(ulong xv, bool plain0, bool masks, byte* ep, int le, byte* rp, long lr, byte* qp, long lq, int w, int rows, int ch, int P, byte* dst, ref Scr sc)
    {
        byte* zb = sc.Z, kp = sc.K, zero0 = sc.Zero;
        ushort* act = sc.Act;
        uint* plb = sc.Pl, eb = sc.Eb, gm = sc.Gm;
        int cap = sc.Cap, rs = sc.Rs;
        _nsymDec = 0;
        _rbp = rp; _roff = 0; _rbits = lr * 8;
        _ep = ep; _ei = 0; _elen = le;
        _qp = qp; _qpos = 0; _qlenB = lq; _qcarry = 0; _qdone = 0; _qbp = sc.Qb; _qi = 0; _qn = 0;
        _bad = false;
        _zbad = Vector128<byte>.Zero;
        byte* zero = zero0 + Pad;
        uint* prv = plb, ra = plb + cap + 16, rb = plb + 2 * (cap + 16);
        uint* ebA = eb, ebB = eb + cap + 16;
        int pendY = -1;
        long stride = (long)w * ch;
        int slot = 0;
        for (int y = 0; y < rows; y++)
        {
            byte* zc0 = zb + (0 * 3 + slot) * rs + Pad, zc1 = zb + (1 * 3 + slot) * rs + Pad;
            if (y == 0 && plain0)
                for (int p = 0; p < P; p++)
                {
                    int kk = (int)(xv >> (1 + 4 * p)) & 15;
                    if (kk > R0.MaxK) return false;
                    new Span<byte>(kp, w + 8).Fill((byte)kk);
                    QEnsure(w);
                    PlaneRow(masks, kp, zb + (p * 3 + slot) * rs + Pad, w, gm);
                }
            else
            {
                QEnsure(P * w);
                for (int p = 0; p < P; p++)
                {
                    byte* zn = y == 0 ? zero : zb + (p * 3 + (slot + 2) % 3) * rs + Pad;
                    byte* znn = y <= 1 ? zn : zb + (p * 3 + (slot + 1) % 3) * rs + Pad;
                    if (_simd) { R0.Act(zn, znn, act, w); R0.KRow(p, act, kp, w); }
                    else { R0.ActS(zn, znn, act, w); R0.KRowS(p, act, kp, w); }
                    PlaneRow(masks, kp, zb + (p * 3 + slot) * rs + Pad, w, gm);
                }
            }
            if (_bad || _qi > _qn || _roff > _rbits || !_zbad.Equals(Vector128<byte>.Zero)) return false;
            byte* orow = dst + y * stride;
            for (int p = 0; p < P; p++) R0.PadRow(zb + (p * 3 + slot) * rs + Pad, w);
            PackE(zc0, zc1, zb + (2 * 3 + slot) * rs + Pad, P == 4 ? zb + (3 * 3 + slot) * rs + Pad : zero, pendY < 0 ? ebA : ebB, w, _simd);
            if (pendY < 0) pendY = y;
            else
            {
                if (_simd) Recon8x2(w, prv, ra, rb, ebA, ebB);
                else { ReconS(w, prv, ra, ebA); ReconS(w, ra, rb, ebB); }
                Colour(w, ch, P, ra, dst + pendY * stride, _simd);
                Colour(w, ch, P, rb, orow, _simd);
                uint* t = prv; prv = rb; rb = t;
                pendY = -1;
            }
            slot = (slot + 1) % 3;
        }
        if (pendY >= 0)
        {
            if (_simd) Recon8(w, prv, ra, ebA);
            else ReconS(w, prv, ra, ebA);
            Colour(w, ch, P, ra, dst + pendY * stride, _simd);
        }
        if (_bad || _ei != _elen || _qdone != _nsymDec || _qi != _qn) return false;
        if (_qpos != _qlenB || _qcarry > 7) return false;
        if (_rbits - _roff >= 8 || (_rbits > _roff && (_rbp[_roff >> 3] >> (int)(_roff & 7)) != 0)) return false;
        if (_mbits - _mpos >= 8 || (_mbits > _mpos && (_mp[_mpos >> 3] >> (int)(_mpos & 7)) != 0)) return false;
        return true;
    }

    private void QEnsure(int need)
    {
        if (_qn - _qi >= need) return;
        byte* qb = _qbp;
        if (_qi > 0)
        {
            int left = _qn - _qi;
            if (left > 0) Buffer.MemoryCopy(qb + _qi, qb, left, left);
            _qn = left; _qi = 0;
        }
        fixed (ulong* lq = LutQ)
        fixed (byte* lc = LutC, lt = LutT)
        {
            byte* src = _qp;
            long bp = _qpos, blen = _qlenB;
            ulong carry = _qcarry;
            int qn = _qn;
            long done = _qdone;
            while (qn < need && bp < blen)
            {
                int v = src[bp++];
                if (v == 0) { carry = Math.Min(carry + 8, 247); continue; }
                *(ulong*)(qb + qn) = lq[v] + carry;
                int c = lc[v];
                qn += c;
                done += c;
                carry = lt[v];
            }
            _qpos = bp; _qcarry = carry; _qdone = done; _qn = qn;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecOne(int k)
    {
        uint q = _qbp[_qi++];
        uint r = (*(uint*)(_rbp + (_roff >> 3)) >> (int)(_roff & 7)) & ((1u << k) - 1);
        _roff += k;
        if (q >= L) q = Escape(q);
        uint z = (q << k) | r;
        if (z > 255) { _bad = true; return 0; }
        return (int)z;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint Escape(uint q)
    {
        if (q > L || _ei >= _elen) { _bad = true; return 0; }
        uint e = _ep[_ei++];
        if (e < L) { _bad = true; return 0; }
        return e;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SegMasked(byte* k, int w, byte* z, uint* gm)
    {
        byte* qb = _qbp;
        byte* rb = _rbp;
        long roff = _roff;
        int qi = _qi;
        long coded = 0;
        var maskLut = MaskLut;
        var pow2 = Pow2Lut;
        var qlim = QLimLut;
        var zbad = _zbad;
        int G = w >> 3;
        for (int g = 0; g < G; g++)
        {
            int x = g << 3;
            ulong keep = (ulong)((long)(gm[g >> 5] >> (g & 31) & 1) - 1); // 0 if masked, all ones if coded
            ulong kk = *(ulong*)(k + x);
            var kv = Vector128.CreateScalarUnsafe(kk).AsByte();
            ulong mask = Ssse3.Shuffle(maskLut, kv).AsUInt64().ToScalar() & keep;
            long bo = roff >> 3;
            int sh = (int)(roff & 7);
            ulong bits = (*(ulong*)(rb + bo) >> sh) | ((ulong)rb[bo + 8] << (63 - sh) << 1);
            ulong r8 = Bmi2.X64.ParallelBitDeposit(bits, mask);
            ulong q8 = *(ulong*)(qb + qi) & keep;
            if (((q8 | (q8 + 0x7070707070707070UL)) & 0x8080808080808080UL) != 0)
            {
                _roff = roff; _qi = qi;
                for (int j = 0; j < 8; j++) z[x + j] = (byte)DecOne(k[x + j]);
                roff = _roff; qi = _qi;
                coded += 8;
                continue;
            }
            roff += BitOperations.PopCount(mask);
            qi += (int)(keep & 8);
            coded += (long)(keep & 8);
            var q8v = Vector128.CreateScalar(q8).AsByte();
            zbad |= Sse2.SubtractSaturate(q8v, Ssse3.Shuffle(qlim, kv));
            var q16 = Sse41.ConvertToVector128Int16(q8v);
            var p16 = Sse41.ConvertToVector128Int16(Ssse3.Shuffle(pow2, kv));
            var prod = Sse2.MultiplyLow(q16, p16);
            *(ulong*)(z + x) = Sse2.PackUnsignedSaturate(prod, prod).AsUInt64().ToScalar() | r8;
        }
        _zbad = zbad;
        _roff = roff; _qi = qi;
        int tail = w & 7;
        if (tail != 0)
        {
            int x = G << 3;
            if ((gm[G >> 5] >> (G & 31) & 1) != 0) *(ulong*)(z + x) = 0;
            else { for (int j = 0; j < tail; j++) z[x + j] = (byte)DecOne(k[x + j]); coded += tail; }
        }
        _nsymDec += coded;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Seg(byte* k, int x0, int x1, byte* z)
    {
        int x = x0;
        byte* qb = _qbp;
        byte* rb = _rbp;
        long roff = _roff;
        int qi = _qi;
        var maskLut = MaskLut;
        var pow2 = Pow2Lut;
        var qlim = QLimLut;
        var zbad = _zbad;
        for (; x + 8 <= x1; x += 8)
        {
            ulong kk = *(ulong*)(k + x);
            var kv = Vector128.CreateScalarUnsafe(kk).AsByte();
            ulong mask = Ssse3.Shuffle(maskLut, kv).AsUInt64().ToScalar();
            long bo = roff >> 3;
            int sh = (int)(roff & 7);
            ulong bits = (*(ulong*)(rb + bo) >> sh) | ((ulong)rb[bo + 8] << (63 - sh) << 1);
            ulong r8 = Bmi2.X64.ParallelBitDeposit(bits, mask);
            ulong q8 = *(ulong*)(qb + qi);
            if (((q8 | (q8 + 0x7070707070707070UL)) & 0x8080808080808080UL) != 0)
            {
                _roff = roff; _qi = qi;
                for (int j = 0; j < 8; j++) z[x + j] = (byte)DecOne(k[x + j]);
                roff = _roff; qi = _qi;
                continue;
            }
            roff += BitOperations.PopCount(mask);
            qi += 8;
            var q8v = Vector128.CreateScalar(q8).AsByte();
            zbad |= Sse2.SubtractSaturate(q8v, Ssse3.Shuffle(qlim, kv));
            var q16 = Sse41.ConvertToVector128Int16(q8v);
            var p16 = Sse41.ConvertToVector128Int16(Ssse3.Shuffle(pow2, kv));
            var prod = Sse2.MultiplyLow(q16, p16);
            *(ulong*)(z + x) = Sse2.PackUnsignedSaturate(prod, prod).AsUInt64().ToScalar() | r8;
        }
        _zbad = zbad;
        _roff = roff; _qi = qi;
        for (; x < x1; x++) z[x] = (byte)DecOne(k[x]);
    }

    private static void PackE(byte* z0, byte* z1, byte* z2, byte* z3, uint* eb, int w, bool simd)
    {
        if (!simd)
        {
            for (int x = 0; x < w; x++)
            {
                uint zz = z0[x] | (uint)z1[x] << 8 | (uint)z2[x] << 16 | (uint)z3[x] << 24;
                eb[x] = ((zz >> 1) & 0x7F7F7F7Fu) ^ ((zz & 0x01010101u) * 0xFFu);
            }
            return;
        }
        var one = Vector128.Create((byte)1);
        var m7f = Vector128.Create((byte)0x7F);
        for (int x = 0; x < w; x += 16)
        {
            var a = Vector128.Load(z0 + x);
            var b = Vector128.Load(z1 + x);
            var c = Vector128.Load(z2 + x);
            var d = Vector128.Load(z3 + x);
            a = ((a.AsUInt16() >> 1).AsByte() & m7f) ^ Vector128.Equals(a & one, one);
            b = ((b.AsUInt16() >> 1).AsByte() & m7f) ^ Vector128.Equals(b & one, one);
            c = ((c.AsUInt16() >> 1).AsByte() & m7f) ^ Vector128.Equals(c & one, one);
            d = ((d.AsUInt16() >> 1).AsByte() & m7f) ^ Vector128.Equals(d & one, one);
            var ab0 = Sse2.UnpackLow(a, b).AsUInt16();
            var ab1 = Sse2.UnpackHigh(a, b).AsUInt16();
            var cd0 = Sse2.UnpackLow(c, d).AsUInt16();
            var cd1 = Sse2.UnpackHigh(c, d).AsUInt16();
            Sse2.UnpackLow(ab0, cd0).AsUInt32().Store(eb + x);
            Sse2.UnpackHigh(ab0, cd0).AsUInt32().Store(eb + x + 4);
            Sse2.UnpackLow(ab1, cd1).AsUInt32().Store(eb + x + 8);
            Sse2.UnpackHigh(ab1, cd1).AsUInt32().Store(eb + x + 12);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReconS(int w, uint* prv, uint* cur, uint* eb)
    {
        static Vector128<short> L(uint v) => Vector128.WidenLower(Vector128.CreateScalarUnsafe(v).AsByte()).AsInt16();
        var m255 = Vector128.Create((short)255);
        var W = L(prv[0]);
        var NW = W;
        for (int x = 0; x < w; x++)
        {
            var N = L(prv[x]);
            var e = Vector128.WidenLower(Vector128.CreateScalarUnsafe(eb[x]).AsSByte());
            var t = Vector128.Min(Vector128.Max(W + N - NW, Vector128.Min(W, N)), Vector128.Max(W, N)) + e;
            W = t & m255;
            NW = N;
            cur[x] = Vector128.Narrow(W.AsUInt16(), W.AsUInt16()).AsUInt32().ToScalar();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Recon8(int w, uint* prv, uint* cur, uint* eb)
    {
        var m255 = Vector128.Create((short)255);
        var W = Sse41.ConvertToVector128Int16((byte*)prv);
        var NW = W;
        for (int x = 0; x < w; x++)
        {
            var N = Sse41.ConvertToVector128Int16((byte*)(prv + x));
            var e = Sse41.ConvertToVector128Int16((sbyte*)(eb + x));
            var dn = N - NW;
            var mx = Vector128.Max(W, N);
            var mn = Vector128.Min(W, N);
            var t = Vector128.Min(Vector128.Max(W + dn, mn), mx) + e;
            W = t & m255;
            NW = N;
            cur[x] = Sse2.PackUnsignedSaturate(W, W).AsUInt32().ToScalar();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Recon8x2(int w, uint* prv, uint* ra, uint* rb, uint* ea, uint* eb)
    {
        var m255 = Vector128.Create((short)255);
        var n0 = Sse41.ConvertToVector128Int16((byte*)prv);
        var top0 = (n0 + Sse41.ConvertToVector128Int16((sbyte*)ea)) & m255;
        ra[0] = Sse2.PackUnsignedSaturate(top0, top0).AsUInt32().ToScalar();
        var R = Sse2.UnpackLow(top0.AsInt64(), top0.AsInt64()).AsInt16();
        var Np = Sse2.UnpackLow(n0.AsInt64(), top0.AsInt64()).AsInt16();
        for (int j = 1; j <= w; j++)
        {
            var ld = Sse41.ConvertToVector128Int16((byte*)(prv + j));
            var N = Sse2.UnpackLow(ld.AsInt64(), R.AsInt64()).AsInt16();
            ulong ev = ea[j] | (ulong)eb[j - 1] << 32;
            var e = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(ev).AsSByte());
            var t = Vector128.Min(Vector128.Max(R - Np + N, Vector128.Min(R, N)), Vector128.Max(R, N)) + e;
            R = t & m255;
            Np = N;
            ulong pk = Sse2.PackUnsignedSaturate(R, R).AsUInt64().ToScalar();
            ra[j] = (uint)pk;
            rb[j - 1] = (uint)(pk >> 32);
        }
    }

    private static void Colour(int w, int ch, int P, uint* v, byte* o, bool simd)
    {
        var b80 = Vector256.Create((byte)0x80);
        var one = Vector256.Create((byte)1);
        var mff = Vector256.Create(0xFFu);
        var m16 = Vector256.Create(0xFF0000u);
        var aMask = P == 4 ? Vector256.Create(0xFF000000u) : Vector256<uint>.Zero;
        var aOr = P == 4 ? Vector256<uint>.Zero : Vector256.Create(0xFF000000u);
        var rgb12 = Rgb12;
        int x = 0;
        int lim = !simd ? 0 : ch == 4 ? w - 7 : w - 9;
        for (; x < lim; x += 8)
        {
            var vv = Vector256.Load(v + x);
            var gs1 = (vv << 8).AsByte();
            var rr = vv.AsByte() + gs1 + b80;
            var av = Avx2.Average(rr, gs1) - ((rr ^ gs1) & one);
            var bb = vv.AsByte() + (av.AsUInt32() << 8).AsByte() + b80;
            var outv = ((rr.AsUInt32() >> 8) & mff) | ((vv & mff) << 8) | (bb.AsUInt32() & m16) | (vv & aMask) | aOr;
            if (ch == 4) outv.Store((uint*)(o + x * 4));
            else
            {
                Ssse3.Shuffle(outv.GetLower().AsByte(), rgb12).Store(o + x * 3);
                Ssse3.Shuffle(outv.GetUpper().AsByte(), rgb12).Store(o + x * 3 + 12);
            }
        }
        for (; x < w; x++)
        {
            uint p = v[x];
            int g = (int)(p & 255), r = (int)((p >> 8) + g + 128) & 255;
            int b = (int)((p >> 16) + 128 + ((r + g) >> 1)) & 255;
            if (ch == 4) *(uint*)(o + x * 4) = (uint)(r | g << 8 | b << 16) | (P == 4 ? p & 0xFF000000u : 0xFF000000u);
            else { o[x * 3] = (byte)r; o[x * 3 + 1] = (byte)g; o[x * 3 + 2] = (byte)b; }
        }
    }
}
