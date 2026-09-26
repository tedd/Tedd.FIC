using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tedd.FIC;

internal interface IBase { static abstract bool Med { get; } }
internal struct BaseW : IBase { public static bool Med => false; }
internal struct BaseM : IBase { public static bool Med => true; }

internal enum QpOp : byte { Invalid, Index, Diff, Luma, W3, Run, RunLong, ZRun, ZRunLong, Copy, CopyLong, L2, Alpha, Rgb, Rgba }

/// <summary>Opcode layout and decode tables for the two byte-mode strip variants.</summary>
internal sealed class QpLayout
{
    public const int W3Start = 192, GroupStart = 200, L2Start = 246, AlphaStart = 250;
    public const uint Valid = 1u << 21, Tool = 1u << 30;

    public readonly string Name;
    public readonly bool Med;
    public readonly int RunShort, ZRunShort;
    public readonly bool ZRunLast;
    public readonly int[] Dy, Dx, CopyShort;
    public readonly int RunStart, RunLongCode, ZRunStart = -1, ZRunLongCode = -1;
    public readonly int[] CopyStart, CopyLongCode;
    public readonly QpOp[] Op = new QpOp[256];
    public readonly int[] Val = new int[256];
    public readonly int[] Cls = new int[256];
    public readonly uint[] T = new uint[768];

    public static readonly QpLayout GW = new("GW", med: false, runShort: 18, zShort: 0, zLast: false, [(1, 0, 26)]);

    public static readonly QpLayout GM = new("GM", med: true, runShort: 5, zShort: 3, zLast: true,
        [(1, 0, 6), (1, 1, 3), (1, -1, 3), (2, 0, 3), (0, -2, 2), (0, -3, 2), (0, -4, 3), (1, -2, 3), (1, 2, 2)]);

    private QpLayout(string name, bool med, int runShort, int zShort, bool zLast, (int Dy, int Dx, int Short)[] copies)
    {
        Name = name; Med = med; RunShort = runShort; ZRunShort = zShort; ZRunLast = zLast;
        Dy = copies.Select(c => c.Dy).ToArray();
        Dx = copies.Select(c => c.Dx).ToArray();
        CopyShort = copies.Select(c => c.Short).ToArray();
        for (int i = 0; i < 64; i++) { Op[i] = QpOp.Index; Val[i] = i; }
        for (int i = 64; i < 128; i++) Op[i] = QpOp.Diff;
        for (int i = 128; i < 192; i++) Op[i] = QpOp.Luma;
        for (int i = W3Start; i < GroupStart; i++) Op[i] = QpOp.W3;
        int c = GroupStart;
        RunStart = c;
        for (int l = 1; l <= runShort; l++) { Op[c] = QpOp.Run; Val[c] = l; T[512 + c] = Valid | (uint)l; c++; }
        RunLongCode = c; Op[c] = QpOp.RunLong; T[512 + c] = Valid | (uint)runShort << 22; c++;
        if (zShort > 0)
        {
            ZRunStart = c;
            for (int l = 1; l <= zShort; l++) { Op[c] = QpOp.ZRun; Val[c] = l; T[512 + c] = Valid | 1u << 16 | (uint)l; c++; }
            ZRunLongCode = c; Op[c] = QpOp.ZRunLong; T[512 + c] = Valid | 1u << 16 | (uint)zShort << 22; c++;
        }
        CopyStart = new int[copies.Length];
        CopyLongCode = new int[copies.Length];
        for (int j = 0; j < copies.Length; j++)
        {
            CopyStart[j] = c;
            for (int l = 1; l <= copies[j].Short; l++) { Op[c] = QpOp.Copy; Val[c] = l; Cls[c] = j; T[512 + c] = Valid | (uint)(2 + j) << 16 | (uint)l; c++; }
            CopyLongCode[j] = c; Op[c] = QpOp.CopyLong; Cls[c] = j; T[512 + c] = Valid | (uint)(2 + j) << 16 | (uint)copies[j].Short << 22; c++;
        }
        if (c != L2Start) throw new InvalidOperationException($"{name}: the run/copy group must end at {L2Start}, not {c}");
        for (int i = 0; i < 4; i++) { Op[L2Start + i] = QpOp.L2; Val[L2Start + i] = i; T[512 + L2Start + i] = Tool | (uint)i << 22; }
        for (int i = 0; i < 4; i++) { Op[AlphaStart + i] = QpOp.Alpha; Val[AlphaStart + i] = i; }
        Op[254] = QpOp.Rgb;
        Op[255] = QpOp.Rgba;
        Array.Copy(LiteralOps.DeltaFirst, 0, T, 0, 256);
        Array.Copy(LiteralOps.DeltaLuma2, 0, T, 256, 256);
    }

    /// <summary>Gets the longest one-byte run or copy length for an opcode class.</summary>
    public int ShortOf(int cls) => cls == 0 ? RunShort : cls == 1 ? ZRunShort : CopyShort[cls - 2];
}

/// <summary>Byte-mode strip codec with a previous-pixel or MED predictor.</summary>
internal static class QpCodec
{
    internal static int AbortFirst = 4;
    internal static double AbortSlack = 1.25;

    internal static int EncoderRunCap = Ficq.MaxRun;

    /// <summary>Gets a safe destination capacity for a byte-mode strip.</summary>
    public static long MaxEncodedSize(int w, int h) => (long)w * h * 6 + 64;

    public const int L2Off = 128;

    /// <summary>Maps a packed color to its second-level cache slot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int L2Slot(uint v) => (int)((v * 0x9E3779B1u) >> 22);

    /// <summary>Computes the packed-channel MED predictor using SIMD instructions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Med(uint w, uint n, uint nw)
    {
        var W = Vector128.CreateScalarUnsafe(w).AsByte();
        var N = Vector128.CreateScalarUnsafe(n).AsByte();
        var NW = Vector128.CreateScalarUnsafe(nw).AsByte();
        var g = Sse2.SubtractSaturate(Sse2.AddSaturate(W, Sse2.SubtractSaturate(N, NW)), Sse2.SubtractSaturate(NW, N));
        var r = Sse2.Min(Sse2.Max(g, Sse2.Min(W, N)), Sse2.Max(W, N));
        return r.AsUInt32().ToScalar();
    }

    /// <summary>Scalar equivalent of the packed-channel MED predictor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint MedS(uint w, uint n, uint nw)
    {
        uint r = 0;
        for (int b = 0; b < 32; b += 8)
        {
            int W = (int)(w >> b) & 255, N = (int)(n >> b) & 255, NW = (int)(nw >> b) & 255;
            int lo = Math.Min(W, N), hi = Math.Max(W, N);
            r |= (uint)Math.Clamp(W + N - NW, lo, hi) << b;
        }
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Med<TK>(uint w, uint n, uint nw) where TK : struct, BlkCodec.IKn => TK.Simd ? Med(w, n, nw) : MedS(w, n, nw);

    /// <summary>Expands a W3 opcode's three channel deltas into packed bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint W3Delta(uint v)
    {
        uint dg = (v >> 12) - 64, drdg = ((v >> 6) & 63) - 32, dbdg = (v & 63) - 32;
        return ((dg + drdg) & 255) | (dg & 255) << 8 | ((dg + dbdg) & 255) << 16;
    }

    /// <summary>Decodes a byte-mode strip and checks that every payload byte was consumed.</summary>
    public static bool Decode(QpLayout lay, ReadOnlySpan<byte> data, int s0, Span<byte> dst, int w, int h, int c, bool simd = true)
    {
        if (w <= 0 || h <= 0 || (c != 3 && c != 4) || s0 < 0 || data.Length < s0 + Ficq.Padding || dst.Length < (long)w * h * c) return false;
        nint end;
        bool ok = Isa.Use(simd) ? Dec3<BlkCodec.KSimd>(lay, data, s0, dst, w, h, c, out end) : Dec3<BlkCodec.KScalar>(lay, data, s0, dst, w, h, c, out end);
        return ok && end == data.Length - Ficq.Padding;
    }

    private static bool Dec3<TK>(QpLayout lay, ReadOnlySpan<byte> data, int s0, Span<byte> dst, int w, int h, int c, out nint end) where TK : struct, BlkCodec.IKn =>
        lay.Med
            ? (c == 3 ? Dec<Rgb, BaseM, TK>.Run(lay, data, s0, dst, w, h, out end) : Dec<Rgba, BaseM, TK>.Run(lay, data, s0, dst, w, h, out end))
            : (c == 3 ? Dec<Rgb, BaseW, TK>.Run(lay, data, s0, dst, w, h, out end) : Dec<Rgba, BaseW, TK>.Run(lay, data, s0, dst, w, h, out end));

    /// <summary>Encodes one strip with the requested opcode layout, optionally stopping above a size limit.</summary>
    public static int Encode(QpLayout lay, ReadOnlySpan<byte> pixels, int w, int h, int c, Span<byte> dst, long limit = long.MaxValue, EncScratch? sc = null, bool simd = true)
    {
        if (w <= 0 || h <= 0 || (c != 3 && c != 4)) throw new ArgumentException("shape");
        if (pixels.Length < (long)w * h * c) throw new ArgumentException("pixels");
        if (dst.Length < MaxEncodedSize(w, h)) throw new ArgumentException("dst");
        bool v = Isa.Use(simd);
        if (sc == null)
        {
            using var tmp = new EncScratch();
            return lay.Med ? Enc<BaseM>.Run(lay, pixels, w, h, c, dst, limit, tmp, v) : Enc<BaseW>.Run(lay, pixels, w, h, c, dst, limit, tmp, v);
        }
        return lay.Med ? Enc<BaseM>.Run(lay, pixels, w, h, c, dst, limit, sc, v) : Enc<BaseW>.Run(lay, pixels, w, h, c, dst, limit, sc, v);
    }


    internal static class Dec<TPx, TB, TK> where TPx : struct, IPx where TB : struct, IBase where TK : struct, BlkCodec.IKn
    {
        private const uint AlphaRgb = 0xFF000000u;

        private ref struct Ctx
        {
            public ReadOnlySpan<byte> Data;
            public Span<byte> Dst;
            public Span<uint> Ix;
            public QpLayout Lay;
            public int W, H;
        }

        private struct SlowState { public nint S, O; public uint Px; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Slow(ref Ctx c, ref SlowState st)
        {
            nint s = st.S, o = st.O;
            uint px = st.Px;
            bool ok = SlowImpl(c.Lay, c.Data, c.Dst, c.W, c.H, c.Ix, ref s, ref o, ref px);
            st.S = s; st.O = o; st.Px = px;
            return ok;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Run(QpLayout lay, ReadOnlySpan<byte> data, int s0, Span<byte> dstSpan, int w, int h, out nint sOut)
        {
            sOut = -1;
            Span<uint> ixSpan = stackalloc uint[L2Off + 1024];
            ixSpan.Clear();
            for (int j = 0; j < lay.Dy.Length; j++)
            {
                long d = (long)lay.Dy[j] * w - lay.Dx[j];
                ixSpan[64 + 2 + j] = d >= 1 ? (uint)(d * TPx.Ch) : uint.MaxValue;
            }
            var ctx = new Ctx { Data = data, Dst = dstSpan, Ix = ixSpan, Lay = lay, W = w, H = h };
            ref byte src = ref MemoryMarshal.GetReference(data);
            ref byte dst = ref MemoryMarshal.GetReference(dstSpan);
            ref uint T = ref MemoryMarshal.GetArrayDataReference(lay.T);
            ref uint ix = ref MemoryMarshal.GetReference(ixSpan);
            nint stride = (nint)w * TPx.Ch;

            uint px = 0xFF000000u;
            nint s = s0;
            nint sEnd = data.Length - Ficq.Padding;
            nint o = 0;
            nint oEnd = (nint)w * h * TPx.Ch;
            nint oFast = oEnd - 64;
            nint head = TB.Med ? stride + TPx.Ch : 0; // the MED base needs p >= w + 1
            var alphaV = Vector128.CreateScalarUnsafe(TPx.Ch == 3 ? AlphaRgb : 0u).AsByte();
            if (TB.Med && oEnd >= stride + 64 && !HeadW(ref src, ref dst, ref T, ref ix, stride, sEnd, ref s, ref o, ref px)) return false;
            while (o < oEnd)
            {
                if (o >= head)
                {
                    var pv = Vector128.CreateScalarUnsafe(px).AsByte();
                    while (o < oFast)
                    {
                        if (s >= sEnd) return false;
                        uint tag = Unsafe.Add(ref src, s);
                        if (tag < 0x80)
                        {
                            if (tag < 0x40)
                            {
                                px = Unsafe.Add(ref ix, (nint)tag);
                                if (px == 0)
                                {
                                    if (TPx.Ch == 3) { px = AlphaRgb; Unsafe.Add(ref ix, LiteralOps.Hash(AlphaRgb)) = AlphaRgb; }
                                    else ix = 0;
                                }
                                s++;
                                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), px);
                                o += TPx.Ch;
                                if (TB.Med) pv = Vector128.CreateScalarUnsafe(px).AsByte();
                                continue;
                            }
                            if (TB.Med) { pv = MedAdd(ref dst, o, stride, pv, Unsafe.Add(ref T, (nint)tag), alphaV); px = pv.AsUInt32().ToScalar(); }
                            else px = LiteralOps.Swar(px, Unsafe.Add(ref T, (nint)tag));
                            s++;
                        }
                        else if (tag < 0xC0)
                        {
                            uint d = LiteralOps.Swar(Unsafe.Add(ref T, (nint)tag), Unsafe.Add(ref T, 256 + (nint)Unsafe.Add(ref src, s + 1)));
                            if (TB.Med) { pv = MedAdd(ref dst, o, stride, pv, d, alphaV); px = pv.AsUInt32().ToScalar(); }
                            else px = LiteralOps.Swar(px, d);
                            s += 2;
                        }
                        else if (tag < 0xC8)
                        {
                            uint v = (tag - 0xC0) << 16 | (uint)Unsafe.Add(ref src, s + 1) << 8 | Unsafe.Add(ref src, s + 2);
                            if (TB.Med) { pv = MedAdd(ref dst, o, stride, pv, W3Delta(v), alphaV); px = pv.AsUInt32().ToScalar(); }
                            else px = LiteralOps.Swar(px, W3Delta(v));
                            s += 3;
                            Unsafe.Add(ref ix, L2Off + (nint)L2Slot(px)) = px;
                        }
                        else if (tag < 250u)
                        {
                            uint e = Unsafe.Add(ref T, 512 + (nint)tag);
                            nint L = (nint)(e & 0xFFFF);
                            nint sn = s + 1;
                            if (L == 0)
                            {
                                if ((e & QpLayout.Valid) == 0)
                                {
                                    if ((e & QpLayout.Tool) == 0) return false;
                                    px = Unsafe.Add(ref ix, L2Off + (nint)(((e >> 22) & 3) << 8 | Unsafe.Add(ref src, s + 1)));
                                    if (TPx.Ch == 3) px |= AlphaRgb;
                                    s += 2;
                                    if (TB.Med) pv = Vector128.CreateScalarUnsafe(px).AsByte();
                                    goto Literal;
                                }
                                nint v = Unsafe.Add(ref src, sn++);
                                if (v >= 0x80)
                                {
                                    nint b1 = Unsafe.Add(ref src, sn++);
                                    if ((nuint)(b1 - 1) >= 0x7F) return false; // at most 2 LEB128 bytes, minimal (a second byte of 1..127)
                                    v = (v & 0x7F) | b1 << 7;
                                }
                                L = (nint)((e >> 22) & 255) + 1 + v;
                                if (L > Ficq.MaxRun) return false;
                            }
                            nint bytes = L * TPx.Ch;
                            if (bytes > oFast + 32 - o) break; // reaches the strip end: careful step
                            uint cls = (e >> 16) & 15;
                            ref byte q = ref Unsafe.Add(ref dst, o);
                            if (cls == 0)
                            {
                                var fv = Vector256.Create(px).AsByte();
                                if (TPx.Ch == 4)
                                {
                                    for (nint k = 0; k < bytes; k += 32) Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, k), fv);
                                }
                                else if (!TK.Simd)
                                {
                                    for (nint k = 0; k < bytes; k += 3) Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, k), px);
                                }
                                else
                                {
                                    var pat = Avx2.Shuffle(fv, Vector256.Create((byte)0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1));
                                    for (nint k = 0; k < bytes; k += 30) Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, k), pat);
                                }
                            }
                            else if (TB.Med && cls == 1)
                            {
                                for (nint k = 0; k < bytes; k += TPx.Ch)
                                {
                                    px = Med<TK>(px, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref q, k - stride)), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref q, k - stride - TPx.Ch)));
                                    if (TPx.Ch == 3) px |= AlphaRgb;
                                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, k), px);
                                }
                            }
                            else
                            {
                                nint D = (nint)Unsafe.Add(ref ix, 64 + (nint)cls);
                                if (D > o) return false; // source before the strip start (or no such class)
                                if (D >= 32)
                                {
                                    Unsafe.WriteUnaligned(ref q, Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref q, -D)));
                                    for (nint k = 32; k < bytes; k += 32)
                                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, k), Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref q, k - D)));
                                }
                                else if (D >= 4)
                                {
                                    for (nint k = 0; k < bytes; k += TPx.Ch)
                                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, k), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref q, k - D)));
                                }
                                else break; // under 4 bytes (tiny widths): careful step
                                px = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref q, bytes - TPx.Ch - D)); // last pixel, read at its source
                                if (TPx.Ch == 3) px |= AlphaRgb;
                            }
                            s = sn;
                            o += bytes;
                            Unsafe.Add(ref ix, LiteralOps.Hash(px)) = px;
                            if (TB.Med) pv = Vector128.CreateScalarUnsafe(px).AsByte();
                            continue;
                        }
                        else if (tag == 0xFE)
                        {
                            px = (Base(ref dst, o, stride, px) & 0xFF000000u) | (Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, s + 1)) & 0x00FFFFFFu);
                            s += 4;
                            if (TB.Med) pv = Vector128.CreateScalarUnsafe(px).AsByte();
                            Unsafe.Add(ref ix, L2Off + (nint)L2Slot(px)) = px;
                        }
                        else if (tag == 0xFF)
                        {
                            px = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, s + 1));
                            if (TPx.Ch == 3) px |= AlphaRgb; // RGB images: alpha is always 255
                            s += 5;
                            if (TB.Med) pv = Vector128.CreateScalarUnsafe(px).AsByte();
                            Unsafe.Add(ref ix, L2Off + (nint)L2Slot(px)) = px;
                        }
                        else
                        {
                            if (TPx.Ch == 3) return false; // no alpha prefix in RGB images
                            nint sA = s;
                            uint a;
                            if (tag == 250) a = 0;
                            else if (tag == 251) a = 255;
                            else if (tag == 252)
                            {
                                if (o < stride) return false;
                                a = Unsafe.Add(ref dst, o - stride + 3);
                            }
                            else { a = Unsafe.Add(ref src, s + 1); s++; }
                            s++;
                            if (s >= sEnd) return false; // the colour op after the prefix must start inside the payload
                            uint b = (Base(ref dst, o, stride, px) & 0x00FFFFFFu) | a << 24;
                            uint t2 = Unsafe.Add(ref src, s);
                            if (t2 < 0x80)
                            {
                                if (t2 < 0x40) return false;
                                px = LiteralOps.Swar(b, Unsafe.Add(ref T, (nint)t2));
                                s++;
                            }
                            else if (t2 < 0xC0)
                            {
                                uint d = LiteralOps.Swar(Unsafe.Add(ref T, (nint)t2), Unsafe.Add(ref T, 256 + (nint)Unsafe.Add(ref src, s + 1)));
                                px = LiteralOps.Swar(b, d);
                                s += 2;
                            }
                            else if (t2 < 0xC8)
                            {
                                uint v = (t2 - 0xC0) << 16 | (uint)Unsafe.Add(ref src, s + 1) << 8 | Unsafe.Add(ref src, s + 2);
                                px = LiteralOps.Swar(b, W3Delta(v));
                                s += 3;
                            }
                            else if (t2 == 0xFE)
                            {
                                px = (b & 0xFF000000u) | (Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, s + 1)) & 0x00FFFFFFu);
                                s += 4;
                            }
                            else return false;
                            if (TB.Med) pv = Vector128.CreateScalarUnsafe(px).AsByte();
                            if (s - sA >= 3) Unsafe.Add(ref ix, L2Off + (nint)L2Slot(px)) = px;
                        }
                    Literal:
                        Unsafe.Add(ref ix, LiteralOps.Hash(px)) = px;
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), px);
                        o += TPx.Ch;
                    }
                    if (o >= oEnd) break;
                }
                var st = new SlowState { S = s, O = o, Px = px };
                if (!Slow(ref ctx, ref st)) return false;
                s = st.S; o = st.O; px = st.Px;
            }
            sOut = s;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HeadW(ref byte src, ref byte dst, ref uint T, ref uint ix, nint stride, nint sEnd, ref nint sRef, ref nint oRef, ref uint pxRef)
        {
            nint s = sRef, o = oRef;
            uint px = pxRef;
            int C = TPx.Ch;
            while (o < stride)
            {
                if (s >= sEnd) return false;
                uint tag = Unsafe.Add(ref src, s);
                if (tag < 0x40)
                {
                    uint v = Unsafe.Add(ref ix, (nint)tag);
                    if (v == 0) break; // unset slot: careful path
                    px = v;
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), px);
                    o += C; s++;
                    continue;
                }
                if (tag < 0x80) { px = LiteralOps.Swar(px, Unsafe.Add(ref T, (nint)tag)); s++; }
                else if (tag < 0xC0)
                {
                    uint d = LiteralOps.Swar(Unsafe.Add(ref T, (nint)tag), Unsafe.Add(ref T, 256 + (nint)Unsafe.Add(ref src, s + 1)));
                    px = LiteralOps.Swar(px, d);
                    s += 2;
                }
                else if (tag < 0xC8)
                {
                    uint v = (tag - 0xC0) << 16 | (uint)Unsafe.Add(ref src, s + 1) << 8 | Unsafe.Add(ref src, s + 2);
                    px = LiteralOps.Swar(px, W3Delta(v));
                    s += 3;
                    Unsafe.Add(ref ix, L2Off + (nint)L2Slot(px)) = px;
                }
                else if (tag < 250u)
                {
                    uint e = Unsafe.Add(ref T, 512 + (nint)tag);
                    nint L = (nint)(e & 0xFFFF);
                    nint sn = s + 1;
                    if (L == 0)
                    {
                        if ((e & QpLayout.Valid) == 0)
                        {
                            if ((e & QpLayout.Tool) == 0) return false;
                            px = Unsafe.Add(ref ix, L2Off + (nint)(((e >> 22) & 3) << 8 | Unsafe.Add(ref src, s + 1)));
                            if (C == 3) px |= AlphaRgb;
                            s += 2;
                            goto Literal;
                        }
                        nint v = Unsafe.Add(ref src, sn++);
                        if (v >= 0x80)
                        {
                            nint b1 = Unsafe.Add(ref src, sn++);
                            if ((nuint)(b1 - 1) >= 0x7F) return false; // minimal: a second byte of 1..127
                            v = (v & 0x7F) | b1 << 7;
                        }
                        L = (nint)((e >> 22) & 255) + 1 + v;
                        if (L > Ficq.MaxRun) return false;
                    }
                    nint bytes = L * C;
                    if (o + bytes > stride) break; // leaves row 0: careful path
                    uint cls = (e >> 16) & 15;
                    if (cls <= 1)
                    {
                        for (nint k = 0; k < bytes; k += C) Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o + k), px);
                    }
                    else
                    {
                        nint D = (nint)Unsafe.Add(ref ix, 64 + (nint)cls);
                        if (D > o || D < 1) return false;
                        for (nint k = 0; k < bytes; k++) Unsafe.Add(ref dst, o + k) = Unsafe.Add(ref dst, o + k - D);
                        px = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, o + bytes - C));
                        if (C == 3) px |= AlphaRgb;
                    }
                    s = sn;
                    o += bytes;
                    Unsafe.Add(ref ix, LiteralOps.Hash(px)) = px;
                    continue;
                }
                else if (tag == 0xFE)
                {
                    px = (px & 0xFF000000u) | (Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, s + 1)) & 0x00FFFFFFu);
                    s += 4;
                    Unsafe.Add(ref ix, L2Off + (nint)L2Slot(px)) = px;
                }
                else if (tag == 0xFF)
                {
                    px = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, s + 1));
                    if (C == 3) px |= AlphaRgb;
                    s += 5;
                    Unsafe.Add(ref ix, L2Off + (nint)L2Slot(px)) = px;
                }
                else break; // ALPHA prefix: careful path
            Literal:
                Unsafe.Add(ref ix, LiteralOps.Hash(px)) = px;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), px);
                o += C;
            }
            sRef = s; oRef = o; pxRef = px;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> MedAdd(ref byte dst, nint o, nint stride, Vector128<byte> W, uint delta, Vector128<byte> alphaV)
        {
            if (!TK.Simd)
            {
                uint m0 = MedS(W.AsUInt32().ToScalar(), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, o - stride)),
                    Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, o - stride - TPx.Ch)));
                uint r0 = LiteralOps.Swar(m0, delta);
                return Vector128.CreateScalarUnsafe(TPx.Ch == 3 ? r0 | AlphaRgb : r0).AsByte();
            }
            var N = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, o - stride))).AsByte();
            var NW = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, o - stride - TPx.Ch))).AsByte();
            var g = Sse2.SubtractSaturate(Sse2.AddSaturate(W, Sse2.SubtractSaturate(N, NW)), Sse2.SubtractSaturate(NW, N));
            var m = Sse2.Min(Sse2.Max(g, Sse2.Min(W, N)), Sse2.Max(W, N));
            var r = Sse2.Add(m, Vector128.CreateScalarUnsafe(delta).AsByte());
            return TPx.Ch == 3 ? Sse2.Or(r, alphaV) : r;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Base(ref byte dst, nint o, nint stride, uint px)
        {
            if (!TB.Med) return px;
            uint m = Med<TK>(px, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, o - stride)), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, o - stride - TPx.Ch)));
            return TPx.Ch == 3 ? m | AlphaRgb : m;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Get(Span<byte> dst, long q) => TPx.Ch == 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(dst.Slice((int)(q * 4), 4))
            : dst[(int)(q * 3)] | (uint)dst[(int)(q * 3 + 1)] << 8 | (uint)dst[(int)(q * 3 + 2)] << 16 | 0xFF000000u;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Set(Span<byte> dst, long q, uint v)
        {
            int i = (int)(q * TPx.Ch);
            dst[i] = (byte)v; dst[i + 1] = (byte)(v >> 8); dst[i + 2] = (byte)(v >> 16);
            if (TPx.Ch == 4) dst[i + 3] = (byte)(v >> 24);
        }

        private static uint BaseAt(Span<byte> dst, long q, int w, uint px) =>
            TB.Med && q >= w + 1 ? (Med<TK>(Get(dst, q - 1), Get(dst, q - w), Get(dst, q - w - 1)) | (TPx.Ch == 3 ? 0xFF000000u : 0)) : px;

        private static bool SlowImpl(QpLayout lay, ReadOnlySpan<byte> data, Span<byte> dst, int w, int h, Span<uint> ix, ref nint s, ref nint o, ref uint px)
        {
            int C = TPx.Ch;
            long nPix = (long)w * h;
            long p = o / C;
            int sEnd = data.Length - Ficq.Padding;
            if (s >= sEnd) return false;
            uint tag = data[(int)s];
            var op = lay.Op[tag];
            int si = (int)s + 1;
            bool lit = false; // a literal op (L2 fill if 3+ bytes)
            switch (op)
            {
                case QpOp.Index:
                    px = ix[(int)tag] | (C == 3 ? 0xFF000000u : 0u);
                    Set(dst, p, px);
                    p++;
                    break;
                case QpOp.L2:
                    px = ix[L2Off + (lay.Val[tag] << 8 | data[si++])] | (C == 3 ? 0xFF000000u : 0u);
                    Set(dst, p, px);
                    p++;
                    break;
                case QpOp.Diff:
                case QpOp.Luma:
                case QpOp.W3:
                case QpOp.Rgb:
                case QpOp.Alpha:
                {
                    uint b = BaseAt(dst, p, w, px);
                    var k = op;
                    uint t = tag;
                    if (k == QpOp.Alpha)
                    {
                        if (C == 3) return false;
                        uint a;
                        int av = lay.Val[tag];
                        if (av == 0) a = 0;
                        else if (av == 1) a = 255;
                        else if (av == 2) { if (p < w) return false; a = Get(dst, p - w) >> 24; }
                        else a = data[si++];
                        b = (b & 0x00FFFFFFu) | a << 24;
                        if (si >= sEnd) return false;
                        t = data[si++];
                        k = lay.Op[t];
                        if (k != QpOp.Diff && k != QpOp.Luma && k != QpOp.W3 && k != QpOp.Rgb) return false;
                    }
                    switch (k)
                    {
                        case QpOp.Diff: px = LiteralOps.Swar(b, LiteralOps.DeltaFirst[t]); break;
                        case QpOp.Luma: px = LiteralOps.Swar(b, LiteralOps.Swar(LiteralOps.DeltaFirst[t], LiteralOps.DeltaLuma2[data[si]])); si++; break;
                        case QpOp.W3:
                            px = LiteralOps.Swar(b, W3Delta((t - 0xC0) << 16 | (uint)data[si] << 8 | data[si + 1]));
                            si += 2;
                            break;
                        default:
                            px = (b & 0xFF000000u) | data[si] | (uint)data[si + 1] << 8 | (uint)data[si + 2] << 16;
                            si += 3;
                            break;
                    }
                    if (C == 3) px |= 0xFF000000u;
                    Set(dst, p, px);
                    p++;
                    lit = true;
                    break;
                }
                case QpOp.Rgba:
                    px = BinaryPrimitives.ReadUInt32LittleEndian(data[si..]) | (C == 3 ? 0xFF000000u : 0u);
                    si += 4;
                    Set(dst, p, px);
                    p++;
                    lit = true;
                    break;
                case QpOp.Run:
                case QpOp.RunLong:
                case QpOp.ZRun:
                case QpOp.ZRunLong:
                case QpOp.Copy:
                case QpOp.CopyLong:
                {
                    long L;
                    int cls = op is QpOp.Run or QpOp.RunLong ? 0 : op is QpOp.ZRun or QpOp.ZRunLong ? 1 : 2 + lay.Cls[tag];
                    if (op is QpOp.Run or QpOp.ZRun or QpOp.Copy) L = lay.Val[tag];
                    else
                    {
                        long v = data[si++];
                        if (v >= 0x80)
                        {
                            int b1 = data[si++];
                            if ((nuint)(b1 - 1) >= 0x7F) return false; // minimal: a second byte of 1..127
                            v = (v & 0x7F) | (long)b1 << 7;
                        }
                        L = lay.ShortOf(cls) + 1 + v;
                    }
                    if (L <= 0 || L > nPix - p || L > Ficq.MaxRun) return false;
                    if (cls == 0)
                    {
                        Set(dst, p, px);
                        int b0 = (int)(p * C), total = (int)(L * C), done = C;
                        while (done < total) { int n = Math.Min(done, total - done); dst.Slice(b0, n).CopyTo(dst.Slice(b0 + done, n)); done += n; }
                        p += L;
                    }
                    else if (cls == 1)
                        for (long i = 0; i < L; i++) { px = BaseAt(dst, p, w, px); Set(dst, p++, px); }
                    else
                    {
                        long d = (long)lay.Dy[cls - 2] * w - lay.Dx[cls - 2];
                        if (d < 1 || p < d) return false;
                        int D = (int)(d * C), b0 = (int)(p * C), total = (int)(L * C);
                        for (int k = 0; k < total; k += D) { int n = Math.Min(D, total - k); dst.Slice(b0 + k - D, n).CopyTo(dst.Slice(b0 + k, n)); }
                        p += L;
                        px = Get(dst, p - 1);
                    }
                    break;
                }
                default:
                    return false;
            }
            ix[LiteralOps.Hash(px)] = px;
            if (lit && si - (int)s >= 3) ix[L2Off + L2Slot(px)] = px;
            s = si;
            o = (nint)(p * C);
            return true;
        }
    }


    internal static class Enc<TB> where TB : struct, IBase
    {
        public static unsafe int Run(QpLayout lay, ReadOnlySpan<byte> pixels, int w, int h, int c, Span<byte> dstSpan, long limit, EncScratch sc, bool simd)
        {
            int n = w * h;
            int nc = lay.Dy.Length;
            int classes = 2 + nc;
            int words = (n + 63) >> 6;
            int mw = classes + 1;
            var u = new Span<uint>(sc.Get(EncScratch.QpPixels, 4L * (n + 64)), n + 64);
            var pred = TB.Med ? new Span<uint>(sc.Get(EncScratch.QpPred, 4L * (n + 64)), n + 64) : Span<uint>.Empty;
            var masks = new Span<ulong>(sc.Get(EncScratch.QpMasks, 8L * (mw * (words + 2) + 64)), mw * (words + 2) + 64);
            {
                ref uint U = ref MemoryMarshal.GetReference(u);
                ref byte src = ref MemoryMarshal.GetReference(pixels);
                if (c == 4) Unsafe.CopyBlockUnaligned(ref Unsafe.As<uint, byte>(ref U), ref src, (uint)(n * 4));
                else
                {
                    int i = 0;
                    var shuf = Vector256.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80, 0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
                    var perm = Vector256.Create(0, 1, 2, 0, 3, 4, 5, 0);
                    var am = Vector256.Create(0xFF000000u).AsByte();
                    for (; simd && i + 11 <= n; i += 8)
                    {
                        var raw = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref src, i * 3));
                        var spread = Avx2.PermuteVar8x32(raw.AsInt32(), perm).AsByte();
                        var pxv = Avx2.Or(Avx2.Shuffle(spread, shuf), am);
                        Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref U, i)), pxv);
                    }
                    for (; i < n; i++)
                        Unsafe.Add(ref U, i) = Unsafe.Add(ref src, i * 3) | (uint)Unsafe.Add(ref src, i * 3 + 1) << 8 | (uint)Unsafe.Add(ref src, i * 3 + 2) << 16 | 0xFF000000u;
                }
                for (int i = n; i < n + 16; i++) Unsafe.Add(ref U, i) = 0; // padding

                masks[..(mw * (words + 2))].Clear();
                Span<int> dist = stackalloc int[16];
                for (int j = 0; j < nc; j++) { long d = (long)lay.Dy[j] * w - lay.Dx[j]; dist[j] = d >= 1 && d <= n ? (int)d : 0; }
                bool hasZ = lay.ZRunShort > 0;
                BuildMasks(u, pred, masks, words, mw, n, w, dist[..nc], hasZ, simd);

                int cap = Math.Min(EncoderRunCap, 16383 + 1); // LEB128 of at most 2 bytes
                Span<int> cmax = stackalloc int[16], cstart = stackalloc int[16], cshort = stackalloc int[16], clong = stackalloc int[16], order = stackalloc int[16];
                cmax[0] = Math.Min(cap, lay.RunShort + 16384); cstart[0] = lay.RunStart; cshort[0] = lay.RunShort; clong[0] = lay.RunLongCode;
                cmax[1] = Math.Min(cap, lay.ZRunShort + 16384); cstart[1] = lay.ZRunStart; cshort[1] = lay.ZRunShort; clong[1] = lay.ZRunLongCode;
                for (int j = 0; j < nc; j++)
                {
                    cmax[2 + j] = Math.Min(cap, lay.CopyShort[j] + 16384); cstart[2 + j] = lay.CopyStart[j]; cshort[2 + j] = lay.CopyShort[j]; clong[2 + j] = lay.CopyLongCode[j];
                }
                int no = 0;
                order[no++] = 0;
                if (hasZ && !lay.ZRunLast) order[no++] = 1;
                for (int j = 0; j < nc; j++) if (dist[j] > 0) order[no++] = 2 + j;
                if (hasZ && lay.ZRunLast) order[no++] = 1;
                var pc = new PCtx { Cmax = cmax, Cstart = cstart, Cshort = cshort, Clong = clong, Order = order[..no], Mw = mw, Limit = limit };
                nint o = Parse2(ref pc, ref U, ref (TB.Med ? ref MemoryMarshal.GetReference(pred) : ref U), ref MemoryMarshal.GetReference(masks), n, w, ref MemoryMarshal.GetReference(dstSpan));
                return o < 0 ? -1 : (int)o;
            }
        }

        private ref struct PCtx
        {
            public Span<int> Cmax, Cstart, Cshort, Clong, Order;
            public int Mw;
            public long Limit;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static nint Parse2(ref PCtx pc, ref uint U, ref uint P, ref ulong M, int n, int w, ref byte dst)
        {
            int mw = pc.Mw;
            Span<uint> ixSpan = stackalloc uint[64 + 1024];
            ixSpan.Clear();
            ref uint ix = ref MemoryMarshal.GetReference(ixSpan);
            ref uint L2 = ref Unsafe.Add(ref ix, 64);
            uint prev = 0xFF000000u;
            nint o = 0;
            int k = 0;
            int curWord = -1;
            ulong anyBits = 0;
            long limit = pc.Limit;
            int kCheck = limit == long.MaxValue ? int.MaxValue : n / AbortFirst;
            long slackQ = (long)(AbortSlack * 1024);
            while (k < n)
            {
                int wi = k >> 6;
                if (wi != curWord)
                {
                    curWord = wi; anyBits = Unsafe.Add(ref M, wi * mw + mw - 1);
                    if (k >= kCheck)
                    {
                        if ((long)o * n * 1024 > limit * slackQ * (long)k) return -1;
                        kCheck = k + (n >> 3);
                    }
                }
                if (((anyBits >> (k & 63)) & 1) == 0)
                {
                    uint px = Unsafe.Add(ref U, k);
                    int hs = LiteralOps.Hash(px);
                    ref uint slot = ref Unsafe.Add(ref ix, hs);
                    if (slot == px)
                    {
                        Unsafe.Add(ref dst, o++) = (byte)hs;
                        prev = px;
                        k++;
                        continue;
                    }
                    slot = px;
                    uint b = TB.Med ? Unsafe.Add(ref P, k) : prev;
                    if (((px ^ b) >> 24) == 0)
                    {
                        if (ColourCost(px, b) > 2)
                        {
                            int sl = L2Slot(px);
                            ref uint l2 = ref Unsafe.Add(ref L2, sl);
                            if (l2 == px)
                            {
                                Unsafe.Add(ref dst, o) = (byte)(QpLayout.L2Start + (sl >> 8));
                                Unsafe.Add(ref dst, o + 1) = (byte)sl;
                                o += 2;
                            }
                            else { o = Colour(ref dst, o, px, b); l2 = px; }
                        }
                        else o = Colour(ref dst, o, px, b);
                    }
                    else
                    {
                        uint a = px >> 24;
                        bool done = false;
                        int acode, extra = 0, cost = 5;
                        if (a == 0) acode = 0;
                        else if (a == 255) acode = 1;
                        else if (k >= w && (Unsafe.Add(ref U, k - w) >> 24) == a) acode = 2;
                        else { acode = 3; extra = 1; }
                        uint b2 = (b & 0x00FFFFFFu) | a << 24;
                        int c2 = 1 + extra + ColourCost(px, b2);
                        if (c2 < 5) cost = c2; else acode = -1;
                        if (cost > 2)
                        {
                            int sl = L2Slot(px);
                            ref uint l2 = ref Unsafe.Add(ref L2, sl);
                            if (l2 == px)
                            {
                                Unsafe.Add(ref dst, o) = (byte)(QpLayout.L2Start + (sl >> 8));
                                Unsafe.Add(ref dst, o + 1) = (byte)sl;
                                o += 2;
                                done = true;
                            }
                            else l2 = px; // cost >= 3: the literal below enters the L2
                        }
                        if (!done && acode >= 0)
                        {
                            Unsafe.Add(ref dst, o++) = (byte)(QpLayout.AlphaStart + acode);
                            if (extra == 1) Unsafe.Add(ref dst, o++) = (byte)a;
                            o = Colour(ref dst, o, px, b2);
                            done = true;
                        }
                        if (!done)
                        {
                            Unsafe.Add(ref dst, o) = 0xFF;
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o + 1), px);
                            o += 5;
                        }
                    }
                    prev = px;
                    k++;
                    continue;
                }
                var order = pc.Order;
                int sh = k & 63;
                ref ulong Mk = ref Unsafe.Add(ref M, (k >> 6) * mw);
                int bestKey = 0;
                for (int oi = 0; oi < order.Length; oi++)
                {
                    int Lw = BitOperations.TrailingZeroCount(~(Unsafe.Add(ref Mk, order[oi]) >> sh));
                    bestKey = Math.Max(bestKey, Lw << 4 | (15 - oi));
                }
                int bestL = bestKey >> 4, bestC;
                if (bestL < 64 - sh) bestC = order[15 - (bestKey & 15)];
                else
                {
                    bestL = 0; bestC = 0;
                    for (int oi = 0; oi < order.Length; oi++)
                    {
                        int cls = order[oi];
                        int t = k + bestL; // a class can only beat bestL if its bit at k + bestL is set
                        if (t >= n || ((Unsafe.Add(ref M, (t >> 6) * mw + cls) >> (t & 63)) & 1) == 0) continue;
                        int L = RunLen(ref Mk, cls, sh, pc.Cmax[cls], mw);
                        if (L > bestL) { bestL = L; bestC = cls; }
                    }
                }
                bestL = Math.Min(bestL, pc.Cmax[bestC]);
                int sc = pc.Cshort[bestC];
                if (bestL <= sc) Unsafe.Add(ref dst, o++) = (byte)(pc.Cstart[bestC] + bestL - 1);
                else
                {
                    Unsafe.Add(ref dst, o++) = (byte)pc.Clong[bestC];
                    int v = bestL - sc - 1;
                    while (v >= 0x80) { Unsafe.Add(ref dst, o++) = (byte)(v | 0x80); v >>= 7; }
                    Unsafe.Add(ref dst, o++) = (byte)v;
                }
                k += bestL;
                prev = Unsafe.Add(ref U, k - 1);
                Unsafe.Add(ref ix, LiteralOps.Hash(prev)) = prev;
            }
            return o;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RunLen(ref ulong Mk, int cls, int sh, int max, int mw)
        {
            ulong x = Unsafe.Add(ref Mk, cls) >> sh;
            int L = BitOperations.TrailingZeroCount(~x);
            if (L > 64 - sh) L = 64 - sh;
            if (L < 64 - sh || L >= max) return Math.Min(L, max);
            nint idx = cls;
            while (true)
            {
                idx += mw;
                ulong y = Unsafe.Add(ref Mk, idx);
                if (y == ulong.MaxValue) { L += 64; if (L >= max) return max; continue; }
                L += BitOperations.TrailingZeroCount(~y);
                return Math.Min(L, max);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nint Colour(ref byte dst, nint o, uint px, uint b)
        {
            uint dr = (uint)(sbyte)(byte)(px - b) + 2;
            uint dg = (uint)(sbyte)(byte)((px >> 8) - (b >> 8)) + 2;
            uint db = (uint)(sbyte)(byte)((px >> 16) - (b >> 16)) + 2;
            if ((dr | dg | db) < 4)
            {
                Unsafe.Add(ref dst, o) = (byte)(0x40 | dr << 4 | dg << 2 | db);
                return o + 1;
            }
            uint lg = dg + 30, lr = dr - dg + 8, lb = db - dg + 8;
            if ((lg < 64) & ((lr | lb) < 16))
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), (ushort)(0x80 | lg | (lr << 4 | lb) << 8));
                return o + 2;
            }
            uint g7 = dg + 62;
            uint r6 = (uint)(sbyte)(byte)(dr - dg) + 32, b6 = (uint)(sbyte)(byte)(db - dg) + 32;
            if ((g7 < 128) & ((r6 | b6) < 64))
            {
                uint v = g7 << 12 | r6 << 6 | b6;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), (0xC0 + (v >> 16)) | (v >> 8 & 0xFF) << 8 | (v & 0xFF) << 16);
                return o + 3;
            }
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), 0xFEu | (px & 0x00FFFFFFu) << 8);
            return o + 4;
        }

        private static int ColourCost(uint px, uint b)
        {
            int dr = (sbyte)(byte)(px - b), dg = (sbyte)(byte)((px >> 8) - (b >> 8)), db = (sbyte)(byte)((px >> 16) - (b >> 16));
            if (dr >= -2 && dr <= 1 && dg >= -2 && dg <= 1 && db >= -2 && db <= 1) return 1;
            int drdg = dr - dg, dbdg = db - dg;
            if (dg >= -32 && dg <= 31 && drdg >= -8 && drdg <= 7 && dbdg >= -8 && dbdg <= 7) return 2;
            int r6 = (sbyte)(byte)(dr - dg), b6 = (sbyte)(byte)(db - dg);
            if (dg >= -64 && dg <= 63 && r6 >= -32 && r6 <= 31 && b6 >= -32 && b6 <= 31) return 3;
            return 4;
        }

        private static void BuildMasks(Span<uint> u, Span<uint> pred, Span<ulong> masks, int words, int mw, int n, int w, Span<int> dist, bool hasZ, bool simd)
        {
            int nc = dist.Length;
            int anyCls = 2 + nc;
            ref uint U = ref MemoryMarshal.GetReference(u);
            ref ulong M = ref MemoryMarshal.GetReference(masks);
            int maxD = w + 1;
            for (int j = 0; j < nc; j++) maxD = Math.Max(maxD, dist[j]);
            int headEnd = simd ? Math.Min(n, (maxD + 8 + 63) & ~63) : n;
            for (int p = 0; p < headEnd; p++)
            {
                uint px = Unsafe.Add(ref U, p);
                uint prevpx = p == 0 ? 0xFF000000u : Unsafe.Add(ref U, p - 1);
                ulong bit = 1UL << (p & 63);
                int wi = p >> 6;
                bool any = false;
                if (px == prevpx) { Unsafe.Add(ref M, wi * mw) |= bit; any = true; }
                if (TB.Med)
                {
                    uint pr = p >= w + 1 ? (simd ? Med(Unsafe.Add(ref U, p - 1), Unsafe.Add(ref U, p - w), Unsafe.Add(ref U, p - w - 1))
                                                 : MedS(Unsafe.Add(ref U, p - 1), Unsafe.Add(ref U, p - w), Unsafe.Add(ref U, p - w - 1))) : prevpx;
                    pred[p] = pr;
                    if (hasZ && px == pr) { Unsafe.Add(ref M, wi * mw + 1) |= bit; any = true; }
                }
                for (int j = 0; j < nc; j++)
                {
                    int d = dist[j];
                    if (d > 0 && p >= d && px == Unsafe.Add(ref U, p - d)) { Unsafe.Add(ref M, wi * mw + 2 + j) |= bit; any = true; }
                }
                if (any) Unsafe.Add(ref M, wi * mw + anyCls) |= bit;
            }
            ref uint P = ref (TB.Med ? ref MemoryMarshal.GetReference(pred) : ref U);
            Span<ulong> mc = stackalloc ulong[16];
            for (int wi = (headEnd + 63) >> 6; wi < words; wi++) // headEnd is a multiple of 64 or n
            {
                int p0 = wi << 6;
                ulong mW = 0, mZ = 0, mAny = 0;
                mc.Clear();
                int cnt = Math.Min(64, n - p0);
                for (int g = 0; g < cnt; g += 8)
                {
                    int p = p0 + g;
                    var cur = Unsafe.ReadUnaligned<Vector256<uint>>(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref U, p)));
                    var wv = Unsafe.ReadUnaligned<Vector256<uint>>(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref U, p - 1)));
                    var e0 = Vector256.Equals(cur, wv);
                    var anyV = e0;
                    mW |= (ulong)e0.ExtractMostSignificantBits() << g;
                    if (TB.Med)
                    {
                        var nv = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref U, p - w)));
                        var nwv = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref U, p - w - 1)));
                        var W = wv.AsByte();
                        var gg = Avx2.SubtractSaturate(Avx2.AddSaturate(W, Avx2.SubtractSaturate(nv, nwv)), Avx2.SubtractSaturate(nwv, nv));
                        var med = Avx2.Min(Avx2.Max(gg, Avx2.Min(W, nv)), Avx2.Max(W, nv)).AsUInt32();
                        Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref P, p)), med);
                        if (hasZ)
                        {
                            var e = Vector256.Equals(cur, med);
                            anyV |= e;
                            mZ |= (ulong)e.ExtractMostSignificantBits() << g;
                        }
                    }
                    for (int j = 0; j < nc; j++)
                    {
                        int d = dist[j];
                        if (d <= 0) continue;
                        var sv = Unsafe.ReadUnaligned<Vector256<uint>>(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref U, p - d)));
                        var e = Vector256.Equals(cur, sv);
                        anyV |= e;
                        mc[j] |= (ulong)e.ExtractMostSignificantBits() << g;
                    }
                    mAny |= (ulong)anyV.ExtractMostSignificantBits() << g;
                }
                if (cnt < 64)
                {
                    ulong keep = (1UL << cnt) - 1;
                    mW &= keep; mZ &= keep; mAny &= keep;
                    for (int j = 0; j < nc; j++) mc[j] &= keep;
                }
                ref ulong Mw = ref Unsafe.Add(ref M, wi * mw);
                Mw = mW;
                Unsafe.Add(ref Mw, 1) = mZ;
                for (int j = 0; j < nc; j++) Unsafe.Add(ref Mw, 2 + j) = mc[j];
                Unsafe.Add(ref Mw, anyCls) = mAny;
            }
        }
    }
}
