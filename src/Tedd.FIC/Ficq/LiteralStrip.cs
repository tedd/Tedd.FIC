using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tedd.FIC;

/// <summary>Literal, cache-index, delta, and run op codec used as each strip's size fallback.</summary>
internal static unsafe class LiteralStrip
{
    public const int MaxRun = 62;

    /// <summary>Gets a safe output-buffer capacity for a given pixel count.</summary>
    public static long MaxEncodedSize(long px) => 5 * px + 16;

    /// <summary>Gets the minimum bytes needed to represent a pixel count with bounded runs.</summary>
    public static long MinPayload(long px) => (px + MaxRun - 1) / MaxRun;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Load(byte* s, nint p, int ch, nint last) =>
        ch == 4 ? *(uint*)(s + p * 4)
        : p < last ? *(uint*)(s + p * 3) | 0xFF000000u
        : *(ushort*)(s + p * 3) | (uint)s[p * 3 + 2] << 16 | 0xFF000000u;

    /// <summary>Encodes one strip with fresh color-cache and previous-pixel state.</summary>
    public static int Encode(byte* src, int w, int rows, int ch, byte* dst)
    {
        uint* ix = stackalloc uint[64];
        new Span<uint>(ix, 64).Clear();
        nint count = (nint)w * rows, last = count - 1;
        uint prev = 0xFF000000u;
        nint o = 0;
        int run = 0;
        for (nint p = 0; p < count; p++)
        {
            uint px = Load(src, p, ch, last);
            if (px == prev)
            {
                run++;
                if (run == MaxRun || p == last) { dst[o++] = (byte)(0xC0 | (run - 1)); run = 0; }
                continue;
            }
            if (run != 0) { dst[o++] = (byte)(0xC0 | (run - 1)); run = 0; }
            int h = LiteralOps.Hash(px);
            if (ix[h] == px) dst[o++] = (byte)h;
            else
            {
                ix[h] = px;
                if (((px ^ prev) >> 24) != 0) { *(ulong*)(dst + o) = 0xFFUL | (ulong)px << 8; o += 5; }
                else
                {
                    uint dr = (uint)(sbyte)(byte)(px - prev) + 2;
                    uint dg = (uint)(sbyte)(byte)((px >> 8) - (prev >> 8)) + 2;
                    uint db = (uint)(sbyte)(byte)((px >> 16) - (prev >> 16)) + 2;
                    if ((dr | dg | db) < 4) dst[o++] = (byte)(0x40 | dr << 4 | dg << 2 | db);
                    else
                    {
                        uint lg = dg + 30, lr = dr - dg + 8, lb = db - dg + 8;
                        if ((lg < 64) & ((lr | lb) < 16)) { *(ushort*)(dst + o) = (ushort)(0x80 | lg | (lr << 4 | lb) << 8); o += 2; }
                        else { *(uint*)(dst + o) = 0xFEu | (px & 0x00FFFFFFu) << 8; o += 4; }
                    }
                }
            }
            prev = px;
        }
        return (int)o;
    }

    /// <summary>Gets the cost of encoding a changed pixel as a literal or delta op.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LiteralCost(uint px, uint prev)
    {
        if (((px ^ prev) >> 24) != 0) return 5;
        int dr = (sbyte)(byte)(px - prev), dg = (sbyte)(byte)((px >> 8) - (prev >> 8)), db = (sbyte)(byte)((px >> 16) - (prev >> 16));
        if (((uint)(dr + 2) | (uint)(dg + 2) | (uint)(db + 2)) < 4u) return 1;
        if ((uint)(dg + 32) < 64u && ((uint)(dr - dg + 8) | (uint)(db - dg + 8)) < 16u) return 2;
        return 4;
    }

    private struct SizerState
    {
        public uint Prev;
        public long RunLen, Cost;
    }

    /// <summary>Counts exact encoded bytes without materializing the strip.</summary>
    public static long Size(byte* src, int w, int rows, int ch, bool simd) => SizeBelow(src, w, rows, ch, long.MaxValue, simd);

    /// <summary>Counts exact bytes while they can beat limit; may return a lower bound once they cannot.</summary>
    public static long SizeBelow(byte* src, int w, int rows, int ch, long limit, bool simd)
    {
        long n = (long)w * rows;
        long minimum = MinPayload(n);
        if (minimum >= limit) return minimum;
        bool v = Isa.Use(simd);
        long chunk = Math.Max(4096, ((n + 1023) / 1024 + 7) & ~7L);
        int nc = (int)((n + chunk - 1) / chunk);
        long* lb = stackalloc long[nc + 1];
        lb[nc] = 0;
        if (limit != long.MaxValue)
        {
            long total = v ? LowerBoundsAvx2(src, n, ch, chunk, lb) : LowerBoundsScalar(src, n, ch, chunk, lb);
            if (total >= limit) return total;
            for (int k = nc - 1; k >= 0; k--) lb[k] += lb[k + 1];
        }
        else new Span<long>(lb, nc).Clear();
        uint* ix = stackalloc uint[64];
        new Span<uint>(ix, 64).Clear();
        var st = new SizerState { Prev = 0xFF000000u };
        long p = 0;
        while (p < n && Load(src, (nint)p, ch, (nint)n - 1) == st.Prev) p++;
        st.RunLen = p;
        for (int k = 0; k < nc; k++)
        {
            long p1 = Math.Min(n, (k + 1) * chunk);
            if (p < p1)
            {
                if (v) ExactAvx2(src, ref p, p1, n, ch, ix, ref st);
                ExactScalar(src, ref p, p1, n, ch, ix, ref st);
            }
            if (st.Cost + lb[k + 1] >= limit) return st.Cost + lb[k + 1];
        }
        return st.Cost + (st.RunLen + MaxRun - 1) / MaxRun;
    }

    /// <summary>Counts exact encoded bytes using the scalar reference path.</summary>
    public static long SizeScalar(byte* src, int w, int rows, int ch)
    {
        uint* ix = stackalloc uint[64];
        new Span<uint>(ix, 64).Clear();
        long n = (long)w * rows, p = 0;
        var st = new SizerState { Prev = 0xFF000000u };
        ExactScalar(src, ref p, n, n, ch, ix, ref st);
        return st.Cost + (st.RunLen + MaxRun - 1) / MaxRun;
    }

    private static void ExactScalar(byte* src, ref long pRef, long p1, long n, int ch, uint* ix, ref SizerState st)
    {
        nint last = (nint)n - 1;
        uint prev = st.Prev;
        long runLen = st.RunLen, cost = st.Cost;
        for (long p = pRef; p < p1; p++)
        {
            uint px = Load(src, (nint)p, ch, last);
            if (px == prev) { runLen++; continue; }
            cost += (runLen + MaxRun - 1) / MaxRun;
            runLen = 0;
            int h = LiteralOps.Hash(px);
            if (ix[h] == px) cost++;
            else { ix[h] = px; cost += LiteralCost(px, prev); }
            prev = px;
        }
        pRef = Math.Max(pRef, p1);
        st.Prev = prev; st.RunLen = runLen; st.Cost = cost;
    }

    private static long LowerBoundsScalar(byte* src, long n, int ch, long chunk, long* lb)
    {
        nint last = (nint)n - 1;
        uint prev = 0xFF000000u;
        bool inRun = false;
        long total = 0;
        for (long c0 = 0, k = 0; c0 < n; c0 += chunk, k++)
        {
            long c1 = Math.Min(n, c0 + chunk), b = 0;
            for (long p = c0; p < c1; p++)
            {
                uint px = Load(src, (nint)p, ch, last);
                if (px != prev) { b++; inRun = false; }
                else if (!inRun) { b++; inRun = true; }
                prev = px;
            }
            lb[k] = b;
            total += b;
        }
        return total;
    }

    private static readonly Vector128<byte> Rgb3to4 = Vector128.Create((byte)0, 1, 2, 128, 3, 4, 5, 128, 6, 7, 8, 128, 9, 10, 11, 128);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Load8(byte* src, long p, int ch, Vector128<byte> sh, Vector256<uint> alphaRgb) =>
        ch == 4
            ? Vector256.Load((uint*)(src + p * 4))
            : Vector256.Create(Ssse3.Shuffle(Vector128.Load(src + p * 3), sh), Ssse3.Shuffle(Vector128.Load(src + p * 3 + 12), sh)).AsUInt32() | alphaRgb;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long LowerBoundsAvx2(byte* src, long n, int ch, long chunk, long* lb)
    {
        var sh = Rgb3to4;
        var alphaRgb = Vector256.Create(ch == 3 ? 0xFF000000u : 0u);
        var rot = Vector256.Create(7, 0, 1, 2, 3, 4, 5, 6);
        var all7 = Vector256.Create(7);
        var carry = Vector256.Create(0xFF000000u);
        long vend = ch == 4 ? n - 8 : n - 10;
        nint last = (nint)n - 1;
        uint inRun = 0; // 1 if the pixel before the current group is part of a run
        long total = 0;
        for (long c0 = 0, k = 0; c0 < n; c0 += chunk, k++)
        {
            long c1 = Math.Min(n, c0 + chunk), b = 0, p = c0;
            for (; p + 8 <= c1 && p <= vend; p += 8)
            {
                var cur = Load8(src, p, ch, sh, alphaRgb);
                var prv = Avx2.Blend(Avx2.PermuteVar8x32(cur.AsInt32(), rot), carry.AsInt32(), 0b1).AsUInt32();
                carry = Avx2.PermuteVar8x32(cur.AsInt32(), all7).AsUInt32();
                uint eqm = Vector256.Equals(cur, prv).ExtractMostSignificantBits();
                b += 8 - BitOperations.PopCount(eqm) + BitOperations.PopCount(eqm & ~((eqm << 1) | inRun));
                inRun = eqm >> 7;
            }
            if (p < c1)
            {
                uint prev = p == 0 ? 0xFF000000u : Load(src, (nint)p - 1, ch, last);
                bool run = inRun != 0;
                for (; p < c1; p++)
                {
                    uint px = Load(src, (nint)p, ch, last);
                    if (px != prev) { b++; run = false; }
                    else if (!run) { b++; run = true; }
                    prev = px;
                }
                inRun = run ? 1u : 0u;
                carry = Vector256.Create(prev);
            }
            lb[k] = b;
            total += b;
        }
        return total;
    }

    private static readonly Vector256<int>[] ShiftIdx = BuildShift(false), ShiftInvalid = BuildShift(true);

    private static Vector256<int>[] BuildShift(bool invalid)
    {
        var a = new Vector256<int>[8];
        for (int d = 0; d < 8; d++)
        {
            var e = new int[8];
            for (int j = 0; j < 8; j++) e[j] = invalid ? (j < d ? -1 : 0) : Math.Max(0, j - d);
            a[d] = Vector256.Create(e);
        }
        return a;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ExactAvx2(byte* src, ref long pRef, long p1, long n, int ch, uint* ix, ref SizerState st)
    {
        long vend = Math.Min(p1 - 8, ch == 4 ? n - 8 : n - 10);
        long p = pRef;
        if (p > vend) return;
        var sh = Rgb3to4;
        var alphaRgb = Vector256.Create(ch == 3 ? 0xFF000000u : 0u);
        var rot = Vector256.Create(7, 0, 1, 2, 3, 4, 5, 6);
        var all7 = Vector256.Create(7);
        var carry = Vector256.Create(st.Prev);
        var bias = Vector256.Create(0x00020202u).AsByte();
        var diffMask = Vector256.Create(0x00FCFCFCu);
        var alphaMask = Vector256.Create(0xFF000000u);
        var gShuf = Vector256.Create((byte)1, 128, 1, 128, 5, 128, 5, 128, 9, 128, 9, 128, 13, 128, 13, 128,
                                     1, 128, 1, 128, 5, 128, 5, 128, 9, 128, 9, 128, 13, 128, 13, 128);
        var lumaBias = Vector256.Create(0x00082008u).AsByte();
        var lumaMask = Vector256.Create(0x00F0C0F0u);
        var weights = Vector256.Create(0x0B070503u).AsSByte();
        var ones16 = Vector256.Create((short)1);
        var four = Vector256.Create(4);
        var five = Vector256.Create(5);
        var one = Vector256.Create(1);
        var h63 = Vector256.Create(63);
        var acc = Vector256<int>.Zero;
        long runLen = st.RunLen, cost = 0;
        for (; p <= vend; p += 8)
        {
            var cur = Load8(src, p, ch, sh, alphaRgb);
            var prv = Avx2.Blend(Avx2.PermuteVar8x32(cur.AsInt32(), rot), carry.AsInt32(), 0b1).AsUInt32();
            carry = Avx2.PermuteVar8x32(cur.AsInt32(), all7).AsUInt32();
            var eq = Vector256.Equals(cur, prv);
            var d = cur.AsByte() - prv.AsByte();
            var isDiff = Vector256.Equals((d + bias).AsUInt32() & diffMask, Vector256<uint>.Zero).AsInt32();
            var isLuma = Vector256.Equals(((d - Avx2.Shuffle(d, gShuf)) + lumaBias).AsUInt32() & lumaMask, Vector256<uint>.Zero).AsInt32();
            var c = four + isLuma + isLuma + isDiff;
            if (ch == 4) c = Vector256.ConditionalSelect(Vector256.Equals(d.AsUInt32() & alphaMask, Vector256<uint>.Zero).AsInt32(), c, five);
            c = Vector256.AndNot(c, eq.AsInt32()); // run lanes cost nothing here
            var hsh = Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(cur.AsByte(), weights), ones16) & h63;
            var old = Avx2.GatherVector256((int*)ix, hsh, 4).AsUInt32();
            for (int k = 7; k >= 1; k--)
            {
                var sameSlot = Vector256.Equals(hsh, Avx2.PermuteVar8x32(hsh, ShiftIdx[k]) | ShiftInvalid[k]).AsUInt32();
                old = Vector256.ConditionalSelect(sameSlot, Avx2.PermuteVar8x32(cur.AsInt32(), ShiftIdx[k]).AsUInt32(), old);
            }
            var hit = Vector256.Equals(old, cur).AsInt32();
            acc += Vector256.ConditionalSelect(hit, Vector256.Min(c, one), c);
            var e16 = Avx2.PackUnsignedSaturate(hsh, hsh).AsUInt64();
            ulong eLo = e16.GetElement(0), eHi = e16.GetElement(2);
            var cu = cur.AsUInt64();
            ulong q0 = cu.GetElement(0), q1 = cu.GetElement(1), q2 = cu.GetElement(2), q3 = cu.GetElement(3);
            ix[eLo & 63] = (uint)q0; ix[(eLo >> 16) & 63] = (uint)(q0 >> 32);
            ix[(eLo >> 32) & 63] = (uint)q1; ix[eLo >> 48] = (uint)(q1 >> 32);
            ix[eHi & 63] = (uint)q2; ix[(eHi >> 16) & 63] = (uint)(q2 >> 32);
            ix[(eHi >> 32) & 63] = (uint)q3; ix[eHi >> 48] = (uint)(q3 >> 32);
            uint eqm = eq.ExtractMostSignificantBits();
            uint ne = ~eqm & 0xFFu;
            if (ne == 0) { runLen += 8; continue; }
            int t0 = BitOperations.TrailingZeroCount(ne);
            cost += (runLen + t0 + MaxRun - 1) / MaxRun;
            int top = BitOperations.LeadingZeroCount(ne << 24);
            uint mid = eqm & (0xFFu >> top) & ~((2u << t0) - 1);
            cost += BitOperations.PopCount(mid & ~(mid << 1));
            runLen = top;
        }
        pRef = p;
        st.RunLen = runLen;
        st.Cost += cost + Vector256.Sum(acc);
        st.Prev = Load(src, (nint)p - 1, ch, (nint)n - 1);
    }

    /// <summary>Decodes a strip and requires the payload to end exactly at the padding boundary.</summary>
    public static bool Decode(ReadOnlySpan<byte> data, int s0, Span<byte> dst, int w, int h, int c)
    {
        if (w <= 0 || h <= 0 || (c != 3 && c != 4) || s0 < 0 || data.Length < s0 + Ficq.Padding || dst.Length < (long)w * h * c) return false;
        return c == 3 ? Dec<Rgb>(data, s0, dst, w, h) : Dec<Rgba>(data, s0, dst, w, h);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Dec<TPx>(ReadOnlySpan<byte> data, int s0, Span<byte> dstSpan, int w, int h) where TPx : struct, IPx
    {
        int C = TPx.Ch;
        ref byte src = ref MemoryMarshal.GetReference(data);
        ref byte dst = ref MemoryMarshal.GetReference(dstSpan);
        ref uint d1 = ref MemoryMarshal.GetArrayDataReference(LiteralOps.DeltaFirst);
        ref uint d2 = ref MemoryMarshal.GetArrayDataReference(LiteralOps.DeltaLuma2);
        uint* ix = stackalloc uint[64];
        new Span<uint>(ix, 64).Clear();
        const uint AlphaRgb = 0xFF000000u;
        uint px = AlphaRgb;
        nint s = s0, sEnd = data.Length - Ficq.Padding;
        nint o = 0, oEnd = (nint)w * h * C;
        nint oFast = oEnd - (MaxRun * C + 40);
        while (o < oFast)
        {
            if (s >= sEnd) return false;
            uint tag = Unsafe.Add(ref src, s);
            if (tag < 0x80)
            {
                if (tag < 0x40)
                {
                    px = ix[tag];
                    if (C == 3) px |= AlphaRgb; // RGB: a value read from the index has alpha 255
                    s++;
                }
                else { px = LiteralOps.Swar(px, Unsafe.Add(ref d1, (nint)tag)); s++; }
            }
            else if (tag < 0xC0)
            {
                px = LiteralOps.Swar(px, LiteralOps.Swar(Unsafe.Add(ref d1, (nint)tag), Unsafe.Add(ref d2, (nint)Unsafe.Add(ref src, s + 1))));
                s += 2;
            }
            else if (tag < 0xFE)
            {
                nint n = (nint)(tag & 63) + 1;
                ix[LiteralOps.Hash(px)] = px;
                for (nint k = 0; k < n * C; k += C) Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o + k), px);
                o += n * C;
                s++;
                continue;
            }
            else if (tag == 0xFE)
            {
                px = (px & 0xFF000000u) | (Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, s + 1)) & 0x00FFFFFFu);
                s += 4;
            }
            else
            {
                if (C == 3) return false; // no RGBA op in an RGB image
                px = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, s + 1));
                s += 5;
            }
            ix[LiteralOps.Hash(px)] = px;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), px);
            o += C;
        }
        while (o < oEnd)
        {
            if (s >= sEnd) return false;
            uint tag = data[(int)s];
            nint n = 1;
            if (tag < 0x40) { px = ix[tag]; if (C == 3) px |= AlphaRgb; s++; }
            else if (tag < 0x80) { px = LiteralOps.Swar(px, LiteralOps.DeltaFirst[tag]); s++; }
            else if (tag < 0xC0)
            {
                if (s + 1 >= sEnd) return false;
                px = LiteralOps.Swar(px, LiteralOps.Swar(LiteralOps.DeltaFirst[tag], LiteralOps.DeltaLuma2[data[(int)s + 1]]));
                s += 2;
            }
            else if (tag < 0xFE) { n = (nint)(tag & 63) + 1; s++; }
            else if (tag == 0xFE)
            {
                if (s + 3 >= sEnd) return false;
                px = (px & 0xFF000000u) | data[(int)s + 1] | (uint)data[(int)s + 2] << 8 | (uint)data[(int)s + 3] << 16;
                s += 4;
            }
            else
            {
                if (C == 3 || s + 4 >= sEnd) return false;
                px = data[(int)s + 1] | (uint)data[(int)s + 2] << 8 | (uint)data[(int)s + 3] << 16 | (uint)data[(int)s + 4] << 24;
                s += 5;
            }
            if (n * C > oEnd - o) return false;
            ix[LiteralOps.Hash(px)] = px;
            for (nint k = 0; k < n; k++)
            {
                int b = (int)(o + k * C);
                dstSpan[b] = (byte)px; dstSpan[b + 1] = (byte)(px >> 8); dstSpan[b + 2] = (byte)(px >> 16);
                if (C == 4) dstSpan[b + 3] = (byte)(px >> 24);
            }
            o += n * C;
        }
        return s == sEnd;
    }
}
