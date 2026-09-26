using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tedd.FIC;

/// <summary>Fixed-width planar block codecs used by the fast tier.</summary>
internal static unsafe class BlkCodec
{
    public const int MUp = 9, MFlat = 10, MUpRun = 11, MFlatRun = 12;
    public const int MZero = 9, MZeroRun = 10;
    public const int RunMax = 17;

    private static readonly uint* PdM = InitMasks();
    private static readonly ulong* PdM64 = InitMasks64();

    private static ulong* InitMasks64()
    {
        var p = (ulong*)NativeMemory.AlignedAlloc(128, 64);
        for (int i = 0; i < 9; i++) p[i] = i == 0 ? 0 : 0x0101010101010101UL * ((1UL << i) - 1);
        for (int i = 9; i < 16; i++) p[i] = 0;
        return p;
    }

    private static uint* InitMasks()
    {
        var p = (uint*)NativeMemory.AlignedAlloc(64, 64);
        uint[] m = [0, 0x01010101, 0x03030303, 0x07070707, 0x0F0F0F0F, 0x1F1F1F1F, 0x3F3F3F3F, 0x7F7F7F7F, 0xFFFFFFFF];
        for (int i = 0; i < 9; i++) p[i] = m[i];
        return p;
    }

    /// <summary>Gets a safe destination capacity for a planar strip.</summary>
    public static long MaxBytes(int w, int h, int P) => (long)P * (w + 8) * (h + 4) * 9 / 8 + 256;

    /// <summary>Returns a 64-byte-aligned row stride with space for vector reads.</summary>
    public static long RowStride(int w)
    {
        long s = (w + 32 + 63) / 64;
        if ((s & 1) == 0) s++;
        return s * 64;
    }

    [ThreadStatic] internal static long* MarkBits;

    internal static bool OldStride = false;
    private static long StrideF0(int w) => OldStride ? (w + 31) & ~15 : RowStride(w);
    private static long StrideN(int w) => OldStride ? (w + 47) & ~15 : RowStride(w + 16);

    /// <summary>Encodes one N-FOR or F0-C strip, optionally recording row bit positions or stopping an F0-C trial.</summary>
    public static int Encode(StripCodec k, byte* src, long srcStride, int w, int h, int ch, int P, byte* dst, EncScratch? sc = null, long* marks = null, long* abortAt = null, bool simd = true)
    {
        _ = PdM; _ = PdM64;
        bool a = P == 4;
        var own = sc == null ? new EncScratch() : null;
        try
        {
            var s = (sc ?? own)!;
            bool v = Isa.Use(simd);
            return k == StripCodec.F0C
                ? (a ? EncF0<A4>(src, srcStride, w, h, ch, dst, s, abortAt, v) : EncF0<A3>(src, srcStride, w, h, ch, dst, s, abortAt, v))
                : v ? (a ? EncN<A4, KSimd>(src, srcStride, w, h, ch, dst, s, marks) : EncN<A3, KSimd>(src, srcStride, w, h, ch, dst, s, marks))
                    : (a ? EncN<A4, KScalar>(src, srcStride, w, h, ch, dst, s, marks) : EncN<A3, KScalar>(src, srcStride, w, h, ch, dst, s, marks));
        }
        finally { own?.Dispose(); }
    }

    /// <summary>Decodes one planar strip, rejecting malformed dimensions, lengths, or coding.</summary>
    public static bool Decode(StripCodec k, byte* s, long len, long avail, byte* dst, long dstStride, int w, int h, int ch, int P, bool simd = true)
    {
        _ = PdM;
        if (w <= 0 || h <= 0 || w > Ficq.MaxPlanarWidth || len < 1 || avail < len) return false;
        return Isa.Use(simd) ? Dec<KSimd>(k, s, len, avail, dst, dstStride, w, h, ch, P) : Dec<KScalar>(k, s, len, avail, dst, dstStride, w, h, ch, P);
    }

    private static bool Dec<TK>(StripCodec k, byte* s, long len, long avail, byte* dst, long dstStride, int w, int h, int ch, int P) where TK : struct, IKn
    {
        bool a = P == 4;
        if (k == StripCodec.F0C)
            return a ? (ch == 3 ? DecF0<A4, C3, TK>(s, len, avail, dst, dstStride, w, h) : DecF0<A4, C4, TK>(s, len, avail, dst, dstStride, w, h))
                     : (ch == 3 ? DecF0<A3, C3, TK>(s, len, avail, dst, dstStride, w, h) : DecF0<A3, C4, TK>(s, len, avail, dst, dstStride, w, h));
        return a ? (ch == 3 ? DecN<A4, C3, TK>(s, len, avail, dst, dstStride, w, h) : DecN<A4, C4, TK>(s, len, avail, dst, dstStride, w, h))
                 : (ch == 3 ? DecN<A3, C3, TK>(s, len, avail, dst, dstStride, w, h) : DecN<A3, C4, TK>(s, len, avail, dst, dstStride, w, h));
    }

    public interface IKn { static abstract bool Simd { get; } }
    public struct KSimd : IKn { public static bool Simd => true; }
    public struct KScalar : IKn { public static bool Simd => false; }

    /// <summary>Extracts equal-width low bits from packed byte lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong PextB(ulong x, int wd, int bytes)
    {
        x &= (bytes == 8 ? ulong.MaxValue : 0xFFFFFFFFUL) & (0x0101010101010101UL * ((1UL << wd) - 1));
        x = (x & 0x00FF00FF00FF00FFUL) | ((x & 0xFF00FF00FF00FF00UL) >> (8 - wd));
        x = (x & 0x0000FFFF0000FFFFUL) | ((x & 0xFFFF0000FFFF0000UL) >> (16 - 2 * wd));
        return (x & 0x00000000FFFFFFFFUL) | ((x & 0xFFFFFFFF00000000UL) >> (32 - 4 * wd));
    }

    /// <summary>Deposits four equal-width values into separate byte lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint PdepB4(ulong v, int wd)
    {
        ulong m = (1UL << wd) - 1;
        uint r = 0;
        for (int k = 0; k < 4; k++) r |= (uint)((v >> (k * wd)) & m) << (8 * k);
        return r;
    }

    public interface IAl { static abstract bool A { get; } }
    public struct A3 : IAl { public static bool A => false; }
    public struct A4 : IAl { public static bool A => true; }
    public interface ICh { static abstract bool Four { get; } }
    public struct C3 : ICh { public static bool Four => false; }
    public struct C4 : ICh { public static bool Four => true; }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Peek(byte* s, long pos) => *(ulong*)(s + (pos >> 3)) >> (int)(pos & 7);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UnZZ(int z) => (z >> 1) ^ -(z & 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ZZ8(int d) { int e = (sbyte)(byte)d; return (e << 1) ^ (e >> 31); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BL(int v) => 32 - BitOperations.LeadingZeroCount((uint)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SubB(uint a, uint b) => ((a | 0x80808080u) - (b & 0x7F7F7F7Fu)) ^ ((a ^ ~b) & 0x80808080u);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint UnZig(uint z) => ((z >> 1) & 0x7F7F7F7Fu) ^ ((z & 0x01010101u) * 0xFFu);

    private static bool EndOk(byte* s, long pos, long len)
    {
        long left = len * 8 - pos;
        if (left < 0 || left >= 8) return false;
        return left == 0 || (s[len - 1] >> (int)(8 - left)) == 0;
    }

    private struct Wr
    {
        public byte* O;
        public ulong Acc;
        public int N;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Put(ulong v, int n)
        {
            Acc |= v << N;
            N += n;
            *(ulong*)O = Acc;
            int bytes = N >> 3;
            O += bytes;
            Acc >>= bytes << 3;
            N &= 7;
        }

        public void Flush()
        {
            while (N > 0) { *O++ = (byte)Acc; Acc >>= 8; N -= 8; }
            N = 0;
        }
    }


    private static readonly Vector128<byte>[] DeMask = BuildDe();
    private static readonly Vector128<byte> ShR0 = Mask(0, 0), ShG0 = Mask(0, 1), ShB0 = Mask(0, 2);
    private static readonly Vector128<byte> ShR1 = Mask(1, 0), ShG1 = Mask(1, 1), ShB1 = Mask(1, 2);
    private static readonly Vector128<byte> ShR2 = Mask(2, 0), ShG2 = Mask(2, 1), ShB2 = Mask(2, 2);
    private static readonly Vector128<byte> De4 = Vector128.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15);

    private static Vector128<byte> Mask(int k, int ch)
    {
        var m = new byte[16];
        for (int i = 0; i < 16; i++) { int ob = k * 16 + i; m[i] = ob % 3 == ch ? (byte)(ob / 3) : (byte)0x80; }
        return Vector128.Create(m);
    }

    private static Vector128<byte>[] BuildDe()
    {
        var a = new Vector128<byte>[9];
        for (int k = 0; k < 3; k++)
            for (int j = 0; j < 3; j++)
            {
                var m = new byte[16];
                for (int i = 0; i < 16; i++) { int bidx = 3 * i + k - 16 * j; m[i] = bidx >= 0 && bidx < 16 ? (byte)bidx : (byte)0x80; }
                a[k * 3 + j] = Vector128.Create(m);
            }
        return a;
    }

    private static void PlaneRow(byte* src, byte* p0, byte* p1, byte* p2, byte* pa, int w, int ch, bool simd)
    {
        int x = 0;
        var m7f = Vector128.Create((byte)0x7F);
        if (!simd) { }
        else if (ch == 3)
        {
            var d = DeMask;
            Vector128<byte> r0 = d[0], r1 = d[1], r2 = d[2], g0 = d[3], g1 = d[4], g2 = d[5], q0 = d[6], q1 = d[7], q2 = d[8];
            for (; x + 16 <= w; x += 16)
            {
                byte* s = src + x * 3;
                var a = Sse2.LoadVector128(s);
                var b = Sse2.LoadVector128(s + 16);
                var c = Sse2.LoadVector128(s + 32);
                var r = Ssse3.Shuffle(a, r0) | Ssse3.Shuffle(b, r1) | Ssse3.Shuffle(c, r2);
                var g = Ssse3.Shuffle(a, g0) | Ssse3.Shuffle(b, g1) | Ssse3.Shuffle(c, g2);
                var bb = Ssse3.Shuffle(a, q0) | Ssse3.Shuffle(b, q1) | Ssse3.Shuffle(c, q2);
                var avg = (r & g) + (Sse2.ShiftRightLogical((r ^ g).AsUInt16(), 1).AsByte() & m7f);
                Sse2.Store(p0 + x, g);
                Sse2.Store(p1 + x, r - g);
                Sse2.Store(p2 + x, bb - avg);
            }
        }
        else
        {
            var de = De4;
            for (; x + 16 <= w; x += 16)
            {
                byte* s = src + x * 4;
                var t0 = Ssse3.Shuffle(Sse2.LoadVector128(s), de).AsUInt32();
                var t1 = Ssse3.Shuffle(Sse2.LoadVector128(s + 16), de).AsUInt32();
                var t2 = Ssse3.Shuffle(Sse2.LoadVector128(s + 32), de).AsUInt32();
                var t3 = Ssse3.Shuffle(Sse2.LoadVector128(s + 48), de).AsUInt32();
                var u0 = Sse2.UnpackLow(t0, t1).AsUInt64();
                var u1 = Sse2.UnpackHigh(t0, t1).AsUInt64();
                var u2 = Sse2.UnpackLow(t2, t3).AsUInt64();
                var u3 = Sse2.UnpackHigh(t2, t3).AsUInt64();
                var r = Sse2.UnpackLow(u0, u2).AsByte();
                var g = Sse2.UnpackHigh(u0, u2).AsByte();
                var bb = Sse2.UnpackLow(u1, u3).AsByte();
                var al = Sse2.UnpackHigh(u1, u3).AsByte();
                var avg = (r & g) + (Sse2.ShiftRightLogical((r ^ g).AsUInt16(), 1).AsByte() & m7f);
                Sse2.Store(p0 + x, g);
                Sse2.Store(p1 + x, r - g);
                Sse2.Store(p2 + x, bb - avg);
                if (pa != null) Sse2.Store(pa + x, al);
            }
        }
        for (; x < w; x++)
        {
            byte* s = src + x * ch;
            int r = s[0], g = s[1], b = s[2];
            p0[x] = (byte)g; p1[x] = (byte)(r - g); p2[x] = (byte)(b - ((r + g) >> 1));
            if (pa != null) pa[x] = s[3];
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int mn, int mx) MinMax(Vector128<byte> v)
    {
        var z = Vector128<byte>.Zero;
        var lo = Sse2.UnpackLow(v, z).AsUInt16();
        var hi = Sse2.UnpackHigh(v, z).AsUInt16();
        int mn = Sse41.MinHorizontal(Sse41.Min(lo, hi)).ToScalar();
        var ff = Vector128.Create((ushort)255);
        int mx = 255 - Sse41.MinHorizontal(Sse41.Min(ff - lo, ff - hi)).ToScalar();
        return (mn, mx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int c, int r) CircFull(Vector128<byte> v)
    {
        (int mn, int mx) = MinMax(v);
        (int mn2, int mx2) = MinMax(v ^ Vector128.Create((byte)0x80));
        int r1 = mx - mn, r2 = mx2 - mn2;
        int useFirst = (r1 - 128) >> 31;
        int c = (mn & useFirst) | ((mn2 ^ 0x80) & ~useFirst);
        int r2c = r2 < 128 ? r2 : 128;
        int r = (r1 & useFirst) | (r2c & ~useFirst);
        return (c, r);
    }

    private static (int c, int r) CircSlow(byte* b0, int cw, int chh, long stride)
    {
        int mn = 255, mx = 0, mn2 = 255, mx2 = 0;
        for (int r = 0; r < chh; r++)
            for (int x = 0; x < cw; x++)
            {
                int v = b0[r * stride + x];
                mn = Math.Min(mn, v); mx = Math.Max(mx, v);
                int v2 = v ^ 0x80;
                mn2 = Math.Min(mn2, v2); mx2 = Math.Max(mx2, v2);
            }
        if (mx - mn < 128) return (mn, mx - mn);
        if (mx2 - mn2 < 128) return (mn2 ^ 0x80, mx2 - mn2);
        return (0, 128);
    }

    private static int EncF0<TA>(byte* src, long srcStride, int W, int H, int ch, byte* dst, EncScratch sc, long* abortAt, bool simd) where TA : struct, IAl
    {
        int P = TA.A ? 4 : 3;
        int nbx = (W + 3) >> 2, nby = (H + 3) >> 2, nfull = W >> 2;
        long stride = StrideF0(W);
        long bandSz = P * 4 * stride;
        {
            {
                byte* band = sc.Get(EncScratch.Blk, 2 * bandSz + nbx + 256);
                byte* flags = band + 2 * bandSz + 64;
                int* st = stackalloc int[8]; // [0..3] left base (nb), [4..7] block 0's nb of the band above
                for (int i = 0; i < 8; i++) st[i] = 0;
                var w = new Wr { O = dst };
                long* marks = MarkBits;
                for (int by = 0; by < nby; by++)
                {
                    if (marks != null) marks[by] = (w.O - dst) * 8 + w.N;
                    if (abortAt != null && by >= 2 && (w.O - dst) * 8 + w.N > abortAt[by * 4] + 64) return -1;
                    int y0 = by * 4, chh = Math.Min(4, H - y0);
                    byte* B = band + (by & 1) * bandSz;
                    byte* Bp = band + ((by & 1) ^ 1) * bandSz;
                    for (int r = 0; r < chh; r++)
                        PlaneRow(src + (y0 + r) * srcStride, B + r * stride, B + (4 + r) * stride, B + (8 + r) * stride, TA.A ? B + (12 + r) * stride : null, W, ch, simd);
                    bool fullBand = chh == 4;
                    if (fullBand)
                    {
                        for (int bx = 0; bx < nfull; bx++)
                        {
                            int x0 = bx << 2;
                            bool up = by > 0, fl = bx > 0 || by > 0;
                            for (int p = 0; p < P && (up || fl); p++)
                            {
                                byte* b0 = B + p * 4 * stride + x0;
                                uint q0 = *(uint*)b0, q1 = *(uint*)(b0 + stride), q2 = *(uint*)(b0 + 2 * stride), q3 = *(uint*)(b0 + 3 * stride);
                                if (up)
                                {
                                    byte* u0 = Bp + p * 4 * stride + x0;
                                    up = q0 == *(uint*)u0 && q1 == *(uint*)(u0 + stride) && q2 == *(uint*)(u0 + 2 * stride) && q3 == *(uint*)(u0 + 3 * stride);
                                }
                                if (fl)
                                {
                                    int v = bx > 0 ? b0[-1] : Bp[(p * 4 + 3) * stride + x0];
                                    uint v4 = (uint)v * 0x01010101u;
                                    fl = q0 == v4 && q1 == v4 && q2 == v4 && q3 == v4;
                                }
                            }
                            flags[bx] = (byte)((up ? 1 : 0) | (fl ? 2 : 0));
                        }
                    }
                    for (int bx = 0; bx < nbx;)
                    {
                        int x0 = bx << 2, cw = Math.Min(4, W - x0);
                        bool full = cw == 4 && fullBand;
                        if (full && flags[bx] != 0)
                        {
                            int ru = 0, rf = 0;
                            while (bx + ru < nfull && ru < RunMax && (flags[bx + ru] & 1) != 0) ru++;
                            while (bx + rf < nfull && rf < RunMax && (flags[bx + rf] & 2) != 0) rf++;
                            bool flat = rf >= ru;
                            int n = flat ? rf : ru;
                            if (n >= 2) w.Put((ulong)(flat ? MFlatRun : MUpRun) | (ulong)(n - 2) << 4, 8);
                            else w.Put((ulong)(flat ? MFlat : MUp), 4);
                            int xl = (bx + n - 1) << 2;
                            for (int p = 0; p < P; p++)
                            {
                                st[p] = B[p * 4 * stride + xl];
                                if (bx == 0) st[4 + p] = B[p * 4 * stride];
                            }
                            bx += n;
                            continue;
                        }
                        for (int p = 0; p < P; p++)
                        {
                            byte* b0 = B + p * 4 * stride + x0;
                            int c, rng;
                            if (full && simd)
                            {
                                var v = Vector128.Create(*(uint*)b0, *(uint*)(b0 + stride), *(uint*)(b0 + 2 * stride), *(uint*)(b0 + 3 * stride)).AsByte();
                                (c, rng) = CircFull(v);
                            }
                            else if (full) (c, rng) = CircSlow(b0, 4, 4, stride); // the same range; c differs only at width 8, where it is unused
                            else (c, rng) = CircSlow(b0, cw, chh, stride);
                            int wd = rng >= 128 ? 8 : BL(rng);
                            int b = wd == 8 ? 0 : c;
                            if (wd < 8)
                            {
                                int pred = bx > 0 ? st[p] : by > 0 ? st[4 + p] : 0;
                                int z = ZZ8(b - pred), n = z + 1, l = BL(n);
                                ulong eg = (1UL << (l - 1)) | ((ulong)(uint)(n - (1 << (l - 1))) << l);
                                w.Put((ulong)wd | eg << 4, 4 + 2 * l - 1);
                            }
                            else w.Put((ulong)wd, 4);
                            if (full)
                            {
                                uint b4 = (uint)b * 0x01010101u;
                                uint m = PdM[wd];
                                int n4 = 4 * wd;
                                ulong p0, p1, p2, p3;
                                if (simd)
                                {
                                    p0 = Bmi2.ParallelBitExtract(SubB(*(uint*)b0, b4), m); p1 = Bmi2.ParallelBitExtract(SubB(*(uint*)(b0 + stride), b4), m);
                                    p2 = Bmi2.ParallelBitExtract(SubB(*(uint*)(b0 + 2 * stride), b4), m); p3 = Bmi2.ParallelBitExtract(SubB(*(uint*)(b0 + 3 * stride), b4), m);
                                }
                                else
                                {
                                    p0 = PextB(SubB(*(uint*)b0, b4), wd, 4); p1 = PextB(SubB(*(uint*)(b0 + stride), b4), wd, 4);
                                    p2 = PextB(SubB(*(uint*)(b0 + 2 * stride), b4), wd, 4); p3 = PextB(SubB(*(uint*)(b0 + 3 * stride), b4), wd, 4);
                                }
                                if (wd < 8) { w.Put(p0 | p1 << n4, 2 * n4); w.Put(p2 | p3 << n4, 2 * n4); }
                                else { w.Put(p0, 32); w.Put(p1, 32); w.Put(p2, 32); w.Put(p3, 32); }
                            }
                            else
                            {
                                for (int r = 0; r < chh; r++)
                                    for (int xx = 0; xx < cw; xx++)
                                        w.Put((ulong)((b0[r * stride + xx] - b) & 255), wd);
                            }
                            int nb = wd < 8 ? b : b0[0];
                            st[p] = nb;
                            if (bx == 0) st[4 + p] = nb;
                        }
                        bx++;
                    }
                }
                if (marks != null) marks[nby] = (w.O - dst) * 8 + w.N;
                w.Flush();
                return (int)(w.O - dst);
            }
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool PlaneFull<TK>(byte* s, ref long pos, int bx, bool hasUp, ref int lb, ref int up1, int wd, byte* rem, byte* bas, long stride) where TK : struct, IKn
    {
        ulong pk = Peek(s, pos) >> 4;
        int used = 4, b = 0;
        if (wd < 8)
        {
            int pred = bx > 0 ? lb : hasUp ? up1 : 0;
            int t = BitOperations.TrailingZeroCount(pk);
            if (t > 8) return false;
            int z = (int)((pk >> (t + 1)) & ((1UL << t) - 1)) + (1 << t) - 1;
            if (z > 255) return false;
            used += 2 * t + 1;
            b = (pred + UnZZ(z)) & 255;
        }
        else if (wd > 8) return false;
        pos += used;
        int step = 4 * wd;
        uint v0;
        if (TK.Simd)
        {
            uint m = PdM[wd];
            v0 = Bmi2.ParallelBitDeposit((uint)Peek(s, pos), m);
            *(uint*)rem = v0;
            *(uint*)(rem + stride) = Bmi2.ParallelBitDeposit((uint)Peek(s, pos + step), m);
            *(uint*)(rem + 2 * stride) = Bmi2.ParallelBitDeposit((uint)Peek(s, pos + 2 * step), m);
            *(uint*)(rem + 3 * stride) = Bmi2.ParallelBitDeposit((uint)Peek(s, pos + 3 * step), m);
        }
        else
        {
            v0 = PdepB4(Peek(s, pos), wd);
            *(uint*)rem = v0;
            *(uint*)(rem + stride) = PdepB4(Peek(s, pos + step), wd);
            *(uint*)(rem + 2 * stride) = PdepB4(Peek(s, pos + 2 * step), wd);
            *(uint*)(rem + 3 * stride) = PdepB4(Peek(s, pos + 3 * step), wd);
        }
        uint a4 = (uint)b * 0x01010101u;
        *(uint*)bas = a4; *(uint*)(bas + stride) = a4; *(uint*)(bas + 2 * stride) = a4; *(uint*)(bas + 3 * stride) = a4;
        pos += (long)wd << 4;
        int nb = wd < 8 ? b : (int)(v0 & 255);
        lb = nb;
        if (bx == 0) up1 = nb;
        return true;
    }

    private static bool PlaneSlow(byte* s, ref long pos, int bx, bool hasUp, ref int lb, ref int up1, byte* rem, byte* bas, long stride, int cw, int chh)
    {
        ulong pk = Peek(s, pos);
        int wd = (int)(pk & 15);
        if (wd > 8) return false;
        pk >>= 4;
        int used = 4, b = 0;
        if (wd < 8)
        {
            int pred = bx > 0 ? lb : hasUp ? up1 : 0;
            int t = BitOperations.TrailingZeroCount(pk);
            if (t > 8) return false;
            int z = (int)((pk >> (t + 1)) & ((1UL << t) - 1)) + (1 << t) - 1;
            if (z > 255) return false;
            used += 2 * t + 1;
            b = (pred + UnZZ(z)) & 255;
        }
        pos += used;
        ulong mm = (1UL << wd) - 1;
        int first = 0;
        for (int r = 0; r < chh; r++)
            for (int xx = 0; xx < cw; xx++)
            {
                int v = (int)(Peek(s, pos) & mm);
                pos += wd;
                rem[r * stride + xx] = (byte)v;
                bas[r * stride + xx] = (byte)b;
                if (r == 0 && xx == 0) first = v;
            }
        int nb = wd < 8 ? b : first;
        lb = nb;
        if (bx == 0) up1 = nb;
        return true;
    }

    public interface IAdd { static abstract bool On { get; } }
    public struct AddOn : IAdd { public static bool On => true; }

    private static void ColourRowT<TA, TC, TK>(byte* a0, byte* b0, long ps, byte* o, int w) where TA : struct, IAl where TC : struct, ICh where TK : struct, IKn
    {
        var m7f = Vector128.Create((byte)0x7F);
        var ff = Vector128.Create((byte)0xFF);
        byte* a1 = a0 + ps, a2 = a1 + ps, a3 = a2 + ps;
        byte* b1 = b0 + ps, b2 = b1 + ps, b3 = b2 + ps;
        int x = 0;
        for (; TK.Simd && x + 16 <= w; x += 16)
        {
            var v0 = Sse2.LoadVector128(a0 + x) + Sse2.LoadVector128(b0 + x);
            var v1 = Sse2.LoadVector128(a1 + x) + Sse2.LoadVector128(b1 + x);
            var v2 = Sse2.LoadVector128(a2 + x) + Sse2.LoadVector128(b2 + x);
            var gg = v0;
            var rr = v1 + gg;
            var avg = (rr & gg) + (Sse2.ShiftRightLogical((rr ^ gg).AsUInt16(), 1).AsByte() & m7f);
            var bb = v2 + avg;
            if (!TC.Four)
            {
                byte* q = o + x * 3;
                Sse2.Store(q, Ssse3.Shuffle(rr, ShR0) | Ssse3.Shuffle(gg, ShG0) | Ssse3.Shuffle(bb, ShB0));
                Sse2.Store(q + 16, Ssse3.Shuffle(rr, ShR1) | Ssse3.Shuffle(gg, ShG1) | Ssse3.Shuffle(bb, ShB1));
                Sse2.Store(q + 32, Ssse3.Shuffle(rr, ShR2) | Ssse3.Shuffle(gg, ShG2) | Ssse3.Shuffle(bb, ShB2));
            }
            else
            {
                var al = ff;
                if (TA.A) al = Sse2.LoadVector128(a3 + x) + Sse2.LoadVector128(b3 + x);
                var rg0 = Sse2.UnpackLow(rr, gg).AsUInt16();
                var rg1 = Sse2.UnpackHigh(rr, gg).AsUInt16();
                var ba0 = Sse2.UnpackLow(bb, al).AsUInt16();
                var ba1 = Sse2.UnpackHigh(bb, al).AsUInt16();
                byte* q = o + x * 4;
                Sse2.Store(q, Sse2.UnpackLow(rg0, ba0).AsByte());
                Sse2.Store(q + 16, Sse2.UnpackHigh(rg0, ba0).AsByte());
                Sse2.Store(q + 32, Sse2.UnpackLow(rg1, ba1).AsByte());
                Sse2.Store(q + 48, Sse2.UnpackHigh(rg1, ba1).AsByte());
            }
        }
        for (; x < w; x++)
        {
            int p0 = a0[x] + b0[x], p1 = a1[x] + b1[x], p2 = a2[x] + b2[x], p3 = TA.A ? a3[x] + b3[x] : 255;
            int gv = p0 & 255, rv = (p1 + gv) & 255, bv = (p2 + ((rv + gv) >> 1)) & 255;
            if (!TC.Four) { byte* q = o + x * 3; q[0] = (byte)rv; q[1] = (byte)gv; q[2] = (byte)bv; }
            else { byte* q = o + x * 4; q[0] = (byte)rv; q[1] = (byte)gv; q[2] = (byte)bv; q[3] = (byte)p3; }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Mode<TA>(byte* s, long pos, int m, int bx, int nfull, bool hasUp, byte* R, byte* Bs, byte* Rp, byte* Bp, long stride, int* st)
        where TA : struct, IAl
    {
        int P = TA.A ? 4 : 3;
        long ps = 4 * stride;
        if (m > MFlatRun) return -1;
        pos += 4;
        int n = 1;
        if (m >= MUpRun) { n = (int)(Peek(s, pos) & 15) + 2; pos += 4; }
        if (bx + n > nfull) return -1;
        long x0 = (long)bx << 2;
        int nbytes = 4 * n;
        if (m == MUp || m == MUpRun)
        {
            if (!hasUp) return -1;
            for (int p = 0; p < P; p++)
                for (int r = 0; r < 4; r++)
                {
                    long o = p * ps + r * stride + x0;
                    Unsafe.CopyBlockUnaligned(R + o, Rp + o, (uint)nbytes);
                    Unsafe.CopyBlockUnaligned(Bs + o, Bp + o, (uint)nbytes);
                }
        }
        else
        {
            if (bx == 0 && !hasUp) return -1;
            for (int p = 0; p < P; p++)
            {
                byte v = bx > 0 ? (byte)(R[p * ps + x0 - 1] + Bs[p * ps + x0 - 1]) : (byte)(Rp[p * ps + 3 * stride + x0] + Bp[p * ps + 3 * stride + x0]);
                for (int r = 0; r < 4; r++)
                {
                    long o = p * ps + r * stride + x0;
                    Unsafe.InitBlockUnaligned(R + o, 0, (uint)nbytes);
                    Unsafe.InitBlockUnaligned(Bs + o, v, (uint)nbytes);
                }
            }
        }
        long xl = x0 + nbytes - 4;
        for (int p = 0; p < P; p++)
        {
            st[p] = (byte)(R[p * ps + xl] + Bs[p * ps + xl]);
            if (bx == 0) st[4 + p] = (byte)(R[p * ps] + Bs[p * ps]);
        }
        st[8] = n;
        return pos;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long BandFull<TA, TK>(byte* s, long pos, int nfull, bool hasUp, byte* R, byte* Bs, byte* Rp, byte* Bp, long stride, int* st)
        where TA : struct, IAl where TK : struct, IKn
    {
        long ps = 4 * stride;
        int lb0 = st[0], lb1 = st[1], lb2 = st[2], lb3 = st[3], u0 = st[4], u1 = st[5], u2 = st[6], u3 = st[7];
        byte* R1 = R + ps, R2 = R1 + ps, R3 = R2 + ps;
        byte* B1 = Bs + ps, B2 = B1 + ps, B3 = B2 + ps;
        for (int bx = 0; bx < nfull;)
        {
            long x0 = (long)bx << 2;
            int wd0 = (int)(Peek(s, pos) & 15);
            if (wd0 > 8)
            {
                st[0] = lb0; st[1] = lb1; st[2] = lb2; st[3] = lb3; st[4] = u0; st[5] = u1; st[6] = u2; st[7] = u3;
                pos = Mode<TA>(s, pos, wd0, bx, nfull, hasUp, R, Bs, Rp, Bp, stride, st);
                if (pos < 0) return -1;
                lb0 = st[0]; lb1 = st[1]; lb2 = st[2]; lb3 = st[3]; u0 = st[4]; u1 = st[5]; u2 = st[6]; u3 = st[7];
                bx += st[8];
                continue;
            }
            if (!PlaneFull<TK>(s, ref pos, bx, hasUp, ref lb0, ref u0, wd0, R + x0, Bs + x0, stride)) return -1;
            if (!PlaneFull<TK>(s, ref pos, bx, hasUp, ref lb1, ref u1, (int)(Peek(s, pos) & 15), R1 + x0, B1 + x0, stride)) return -1;
            if (!PlaneFull<TK>(s, ref pos, bx, hasUp, ref lb2, ref u2, (int)(Peek(s, pos) & 15), R2 + x0, B2 + x0, stride)) return -1;
            if (TA.A && !PlaneFull<TK>(s, ref pos, bx, hasUp, ref lb3, ref u3, (int)(Peek(s, pos) & 15), R3 + x0, B3 + x0, stride)) return -1;
            bx++;
        }
        st[0] = lb0; st[1] = lb1; st[2] = lb2; st[3] = lb3; st[4] = u0; st[5] = u1; st[6] = u2; st[7] = u3;
        return pos;
    }

    private static bool DecF0<TA, TC, TK>(byte* s0, long len, long avail, byte* dst, long dstStride, int W, int H)
        where TA : struct, IAl where TC : struct, ICh where TK : struct, IKn
    {
        int P = TA.A ? 4 : 3;
        int nbx = (W + 3) >> 2, nby = (H + 3) >> 2, nfull = W >> 2;
        long stride = StrideF0(W);
        long bandSz = P * 4 * stride;           // one band of one kind (rem or bas): P planes x 4 rows
        long bandMax = (long)nbx * P * 19 + 64;  // most bits a band can take, in bytes (+ look-ahead)
        long tailBytes = Math.Max(0, len) + bandMax + 16 + 64;
        byte[] arr = ArrayPool<byte>.Shared.Rent((int)(4 * bandSz + 256 + tailBytes));
        try
        {
            fixed (byte* ap = arr)
            {
                byte* rem0 = R0.Align64(ap);
                byte* bas0 = rem0 + 2 * bandSz + 64;
                byte* tailBuf = R0.Align64(rem0 + 4 * bandSz + 128);
                byte* s = s0;
                long pos = 0;
                bool tail = false;
                int* st = stackalloc int[12];
                for (int i = 0; i < 12; i++) st[i] = 0;
                long ps = 4 * stride; // plane step inside a band
                for (int by = 0; by < nby; by++)
                {
                    if (!tail && (pos >> 3) + bandMax + 16 > avail)
                    {
                        Buffer.MemoryCopy(s0, tailBuf, len, len);
                        new Span<byte>(tailBuf + len, (int)(tailBytes - len)).Clear();
                        s = tailBuf;
                        tail = true;
                    }
                    if ((pos >> 3) > len) return false;
                    int y0 = by * 4, chh = Math.Min(4, H - y0);
                    long co = (by & 1) * bandSz, po = ((by & 1) ^ 1) * bandSz;
                    byte* R = rem0 + co, Bs = bas0 + co;
                    bool hasUp = by > 0;
                    int bx = 0;
                    if (chh == 4 && nfull > 0)
                    {
                        pos = BandFull<TA, TK>(s, pos, nfull, hasUp, R, Bs, rem0 + po, bas0 + po, stride, st);
                        if (pos < 0) return false;
                        bx = nfull;
                    }
                    if (bx < nbx)
                    {
                        int lb0 = st[0], lb1 = st[1], lb2 = st[2], lb3 = st[3], u0 = st[4], u1 = st[5], u2 = st[6], u3 = st[7];
                        for (; bx < nbx; bx++)
                        {
                            long x0 = bx << 2;
                            int cw = Math.Min(4, W - (int)x0);
                            if (!PlaneSlow(s, ref pos, bx, hasUp, ref lb0, ref u0, R + x0, Bs + x0, stride, cw, chh)) return false;
                            if (!PlaneSlow(s, ref pos, bx, hasUp, ref lb1, ref u1, R + ps + x0, Bs + ps + x0, stride, cw, chh)) return false;
                            if (!PlaneSlow(s, ref pos, bx, hasUp, ref lb2, ref u2, R + 2 * ps + x0, Bs + 2 * ps + x0, stride, cw, chh)) return false;
                            if (TA.A && !PlaneSlow(s, ref pos, bx, hasUp, ref lb3, ref u3, R + 3 * ps + x0, Bs + 3 * ps + x0, stride, cw, chh)) return false;
                        }
                        st[0] = lb0; st[1] = lb1; st[2] = lb2; st[3] = lb3; st[4] = u0; st[5] = u1; st[6] = u2; st[7] = u3;
                    }
                    for (int r = 0; r < chh; r++)
                        ColourRowT<TA, TC, TK>(R + r * stride, Bs + r * stride, ps, dst + (y0 + r) * dstStride, W);
                }
                return EndOk(s, pos, len);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(arr); }
    }


    private static void Residual(byte* v, byte* up, byte* z, int W, bool first, bool simd)
    {
        int x = 0;
        if (!first)
        {
            for (; x + 16 <= W; x += 16)
            {
                var e = Vector128.Load(v + x) - Vector128.Load(up + x);
                var neg = Vector128.LessThan(e.AsSByte(), Vector128<sbyte>.Zero).AsByte();
                ((e + e) ^ neg).Store(z + x);
            }
            for (; x < W; x++) z[x] = (byte)ZZ8(v[x] - up[x]);
        }
        else
        {
            z[0] = (byte)ZZ8(v[0]);
            for (x = 1; x < W; x++) z[x] = (byte)ZZ8(v[x] - v[x - 1]);
        }
    }

    private static int EncN<TA, TK>(byte* src, long srcStride, int W, int H, int ch, byte* dst, EncScratch sc, long* rowMarks) where TA : struct, IAl where TK : struct, IKn
    {
        int P = TA.A ? 4 : 3;
        int nbx = (W + 7) >> 3, nfull = W >> 3;
        long stride = StrideN(W);
        {
            {
                byte* buf = sc.Get(EncScratch.Blk, 3 * 4 * stride + nbx + 256);
                byte* pa = buf, pb = buf + 4 * stride, zz = buf + 8 * stride; // plane rows (current / previous), zigzag rows
                byte* flags = buf + 12 * stride + 64;
                var w = new Wr { O = dst };
                long* marks = rowMarks != null ? rowMarks : MarkBits;
                for (int y = 0; y < H; y++)
                {
                    if (marks != null) marks[y] = (w.O - dst) * 8 + w.N;
                    PlaneRow(src + y * srcStride, pa, pa + stride, pa + 2 * stride, TA.A ? pa + 3 * stride : null, W, ch, TK.Simd);
                    for (int p = 0; p < P; p++) Residual(pa + p * stride, pb + p * stride, zz + p * stride, W, y == 0, TK.Simd);
                    for (int bx = 0; bx < nfull; bx++)
                    {
                        ulong o = *(ulong*)(zz + bx * 8) | *(ulong*)(zz + stride + bx * 8) | *(ulong*)(zz + 2 * stride + bx * 8);
                        if (TA.A) o |= *(ulong*)(zz + 3 * stride + bx * 8);
                        flags[bx] = (byte)(o == 0 ? 1 : 0);
                    }
                    w.O = RowGroups<TA, TK>(zz, stride, flags, nfull, w.O, ref w.Acc, ref w.N);
                    if (nfull < nbx)
                    {
                        int x0 = nfull << 3, gw = W - x0;
                        for (int p = 0; p < P; p++)
                        {
                            byte* zp = zz + p * stride + x0;
                            int o = 0;
                            for (int i = 0; i < gw; i++) o |= zp[i];
                            int wd = BL(o);
                            w.Put((ulong)wd, 4);
                            for (int i = 0; i < gw; i++) w.Put(zp[i], wd);
                        }
                    }
                    byte* t = pa; pa = pb; pb = t;
                }
                if (marks != null) marks[H] = (w.O - dst) * 8 + w.N;
                w.Flush();
                return (int)(w.O - dst);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put(ref byte* o, ref ulong acc, ref int n, ulong v, int len)
    {
        ulong a = acc | v << n;
        int t = n + len;
        *(ulong*)o = a;
        int bytes = t >> 3;
        o += bytes;
        acc = a >> (bytes << 3);
        n = t & 7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Group<TK>(ref byte* o, ref ulong acc, ref int n, ulong q, ulong* m64) where TK : struct, IKn
    {
        ulong t = q | q >> 32; t |= t >> 16; t |= t >> 8;
        int wd = 32 - BitOperations.LeadingZeroCount((uint)t & 255);
        ulong bits = TK.Simd ? Bmi2.X64.ParallelBitExtract(q, m64[wd]) : PextB(q, wd, 8);
        if (wd <= 6) Put(ref o, ref acc, ref n, (ulong)wd | bits << 4, 4 + 8 * wd);
        else
        {
            int h4 = 4 * wd;
            Put(ref o, ref acc, ref n, (ulong)wd | (bits & ((1UL << h4) - 1)) << 4, 4 + h4);
            Put(ref o, ref acc, ref n, bits >> h4, h4);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static byte* RowGroups<TA, TK>(byte* zz, long stride, byte* flags, int nfull, byte* o, ref ulong accRef, ref int nRef) where TA : struct, IAl where TK : struct, IKn
    {
        ulong acc = accRef;
        int n = nRef;
        byte* z1 = zz + stride, z2 = z1 + stride, z3 = z2 + stride;
        ulong* m64 = PdM64;
        for (int bx = 0; bx < nfull;)
        {
            if (flags[bx] != 0)
            {
                int k = 1;
                while (bx + k < nfull && k < RunMax && flags[bx + k] != 0) k++;
                if (k >= 2) Put(ref o, ref acc, ref n, MZeroRun | (ulong)(k - 2) << 4, 8);
                else Put(ref o, ref acc, ref n, MZero, 4);
                bx += k;
                continue;
            }
            long x0 = (long)bx << 3;
            Group<TK>(ref o, ref acc, ref n, *(ulong*)(zz + x0), m64);
            Group<TK>(ref o, ref acc, ref n, *(ulong*)(z1 + x0), m64);
            Group<TK>(ref o, ref acc, ref n, *(ulong*)(z2 + x0), m64);
            if (TA.A) Group<TK>(ref o, ref acc, ref n, *(ulong*)(z3 + x0), m64);
            bx++;
        }
        accRef = acc;
        nRef = n;
        return o;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GroupPlane<TK>(byte* s, ref long pos, int wd, byte* d) where TK : struct, IKn
    {
        if ((uint)wd > 8) return false;
        pos += 4;
        uint z0, z1;
        if (TK.Simd)
        {
            uint m = PdM[wd];
            z0 = Bmi2.ParallelBitDeposit((uint)Peek(s, pos), m);
            z1 = Bmi2.ParallelBitDeposit((uint)Peek(s, pos + 4 * wd), m);
        }
        else
        {
            z0 = PdepB4(Peek(s, pos), wd);
            z1 = PdepB4(Peek(s, pos + 4 * wd), wd);
        }
        *(uint*)d = UnZig(z0);
        *(uint*)(d + 4) = UnZig(z1);
        pos += 8 * wd;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RowFull<TA, TK>(byte* s, long pos, int nfull, byte* e, long stride) where TA : struct, IAl where TK : struct, IKn
    {
        byte* e1 = e + stride, e2 = e1 + stride, e3 = e2 + stride;
        for (int bx = 0; bx < nfull;)
        {
            long x0 = (long)bx << 3;
            ulong pk = Peek(s, pos);
            int wd = (int)(pk & 15);
            if (wd > 8)
            {
                if (wd > MZeroRun) return -1;
                int n = 1;
                pos += 4;
                if (wd == MZeroRun) { n = (int)((pk >> 4) & 15) + 2; pos += 4; }
                if (bx + n > nfull) return -1;
                uint nb = (uint)(8 * n);
                Unsafe.InitBlockUnaligned(e + x0, 0, nb);
                Unsafe.InitBlockUnaligned(e1 + x0, 0, nb);
                Unsafe.InitBlockUnaligned(e2 + x0, 0, nb);
                if (TA.A) Unsafe.InitBlockUnaligned(e3 + x0, 0, nb);
                bx += n;
                continue;
            }
            if (!GroupPlane<TK>(s, ref pos, wd, e + x0)) return -1;
            if (!GroupPlane<TK>(s, ref pos, (int)(Peek(s, pos) & 15), e1 + x0)) return -1;
            if (!GroupPlane<TK>(s, ref pos, (int)(Peek(s, pos) & 15), e2 + x0)) return -1;
            if (TA.A && !GroupPlane<TK>(s, ref pos, (int)(Peek(s, pos) & 15), e3 + x0)) return -1;
            bx++;
        }
        return pos;
    }

    private static void AddColourRow<TA, TC, TK>(byte* v0, byte* e0, long ps, byte* o, int w) where TA : struct, IAl where TC : struct, ICh where TK : struct, IKn
    {
        var m7f = Vector128.Create((byte)0x7F);
        var ff = Vector128.Create((byte)0xFF);
        byte* v1 = v0 + ps, v2 = v1 + ps, v3 = v2 + ps;
        byte* e1 = e0 + ps, e2 = e1 + ps, e3 = e2 + ps;
        int x = 0;
        for (; TK.Simd && x + 16 <= w; x += 16)
        {
            var a0 = Sse2.LoadVector128(v0 + x) + Sse2.LoadVector128(e0 + x);
            var a1 = Sse2.LoadVector128(v1 + x) + Sse2.LoadVector128(e1 + x);
            var a2 = Sse2.LoadVector128(v2 + x) + Sse2.LoadVector128(e2 + x);
            Sse2.Store(v0 + x, a0); Sse2.Store(v1 + x, a1); Sse2.Store(v2 + x, a2);
            var gg = a0;
            var rr = a1 + gg;
            var avg = (rr & gg) + (Sse2.ShiftRightLogical((rr ^ gg).AsUInt16(), 1).AsByte() & m7f);
            var bb = a2 + avg;
            if (!TC.Four)
            {
                byte* q = o + x * 3;
                Sse2.Store(q, Ssse3.Shuffle(rr, ShR0) | Ssse3.Shuffle(gg, ShG0) | Ssse3.Shuffle(bb, ShB0));
                Sse2.Store(q + 16, Ssse3.Shuffle(rr, ShR1) | Ssse3.Shuffle(gg, ShG1) | Ssse3.Shuffle(bb, ShB1));
                Sse2.Store(q + 32, Ssse3.Shuffle(rr, ShR2) | Ssse3.Shuffle(gg, ShG2) | Ssse3.Shuffle(bb, ShB2));
            }
            else
            {
                var al = ff;
                if (TA.A) { al = Sse2.LoadVector128(v3 + x) + Sse2.LoadVector128(e3 + x); Sse2.Store(v3 + x, al); }
                var rg0 = Sse2.UnpackLow(rr, gg).AsUInt16();
                var rg1 = Sse2.UnpackHigh(rr, gg).AsUInt16();
                var ba0 = Sse2.UnpackLow(bb, al).AsUInt16();
                var ba1 = Sse2.UnpackHigh(bb, al).AsUInt16();
                byte* q = o + x * 4;
                Sse2.Store(q, Sse2.UnpackLow(rg0, ba0).AsByte());
                Sse2.Store(q + 16, Sse2.UnpackHigh(rg0, ba0).AsByte());
                Sse2.Store(q + 32, Sse2.UnpackLow(rg1, ba1).AsByte());
                Sse2.Store(q + 48, Sse2.UnpackHigh(rg1, ba1).AsByte());
            }
        }
        for (; x < w; x++)
        {
            int p0 = (byte)(v0[x] + e0[x]), p1 = (byte)(v1[x] + e1[x]), p2 = (byte)(v2[x] + e2[x]);
            v0[x] = (byte)p0; v1[x] = (byte)p1; v2[x] = (byte)p2;
            int p3 = 255;
            if (TA.A) { p3 = (byte)(v3[x] + e3[x]); v3[x] = (byte)p3; }
            int gv = p0, rv = (p1 + gv) & 255, bv = (p2 + ((rv + gv) >> 1)) & 255;
            if (!TC.Four) { byte* q = o + x * 3; q[0] = (byte)rv; q[1] = (byte)gv; q[2] = (byte)bv; }
            else { byte* q = o + x * 4; q[0] = (byte)rv; q[1] = (byte)gv; q[2] = (byte)bv; q[3] = (byte)p3; }
        }
    }

    private static bool DecN<TA, TC, TK>(byte* s0, long len, long avail, byte* dst, long dstStride, int W, int H)
        where TA : struct, IAl where TC : struct, ICh where TK : struct, IKn
    {
        int P = TA.A ? 4 : 3;
        int nbx = (W + 7) >> 3, nfull = W >> 3;
        long stride = StrideN(W);
        long rowMax = (long)nbx * P * 9 + 64;
        long tailBytes = len + rowMax + 16 + 64;
        byte[] arr = ArrayPool<byte>.Shared.Rent((int)(2 * 4 * stride + 256 + tailBytes));
        try
        {
            fixed (byte* ap = arr)
            {
                byte* buf = R0.Align64(ap);
                byte* v = buf, e = buf + 4 * stride;  // plane rows (in place), residual rows
                byte* tailBuf = R0.Align64(buf + 8 * stride + 64);
                byte* s = s0;
                long pos = 0;
                bool tail = false;
                for (int y = 0; y < H; y++)
                {
                    if (!tail && (pos >> 3) + rowMax + 16 > avail)
                    {
                        Buffer.MemoryCopy(s0, tailBuf, len, len);
                        new Span<byte>(tailBuf + len, (int)(tailBytes - len)).Clear();
                        s = tailBuf;
                        tail = true;
                    }
                    if ((pos >> 3) > len) return false;
                    if (nfull > 0)
                    {
                        pos = RowFull<TA, TK>(s, pos, nfull, e, stride);
                        if (pos < 0) return false;
                    }
                    for (int bx = nfull; bx < nbx; bx++)
                    {
                        int x0 = bx << 3, gw = W - x0;
                        for (int p = 0; p < P; p++)
                        {
                            int wd = (int)(Peek(s, pos) & 15);
                            if (wd > 8) return false;
                            pos += 4;
                            ulong mm = (1UL << wd) - 1;
                            byte* d = e + p * stride + x0;
                            for (int i = 0; i < gw; i++) { d[i] = (byte)UnZZ((int)(Peek(s, pos) & mm)); pos += wd; }
                        }
                    }
                    if (y == 0)
                    {
                        for (int p = 0; p < P; p++)
                        {
                            byte* vp = v + p * stride, ep = e + p * stride;
                            byte a = 0;
                            for (int x = 0; x < W; x++) { a = (byte)(a + ep[x]); vp[x] = a; ep[x] = 0; }
                        }
                    }
                    AddColourRow<TA, TC, TK>(v, e, stride, dst + y * dstStride, W);
                }
                return EndOk(s, pos, len);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(arr); }
    }
}
