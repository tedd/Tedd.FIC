using System.Buffers;

namespace Tedd.FIC;

internal static unsafe class FicqDecoder
{
    public static bool TryDecode(ReadOnlySpan<byte> data, Span<byte> dst, int threads, bool verifyCrc, out FicImageInfo info, bool simd = true)
    {
        simd = Isa.Use(simd && Isa.Simd);
        info = default;
        using var hd = FicqHeader.TryParse(data);
        if (hd == null || dst.Length < hd.OutputBytes) return false;
        info = new FicImageInfo(hd.W, hd.H, hd.Ch, hd.Tier == Ficq.TierFast ? FicTier.Fast : FicTier.Compact);
        uint[] crcs = ArrayPool<uint>.Shared.Rent(hd.N);
        try
        {
            fixed (byte* dp = data, op = dst)
            {
                nint dAddr = (nint)dp, oAddr = (nint)op;
                long dLen = data.Length;
                if (threads <= 1 || hd.N == 1)
                {
                    for (int i = 0; i < hd.N; i++)
                        if (!Strip(hd, i, (byte*)dAddr, dLen, (byte*)oAddr, verifyCrc, simd, out crcs[i])) return false;
                }
                else
                {
                    int bad = 0;
                    Parallel.For(0, hd.N, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                    {
                        if (Volatile.Read(ref bad) != 0) return;
                        if (!Strip(hd, i, (byte*)dAddr, dLen, (byte*)oAddr, verifyCrc, simd, out crcs[i])) Volatile.Write(ref bad, 1);
                    });
                    if (bad != 0) return false;
                }
            }
            if (!verifyCrc) return true;
            uint crc = Crc32C.Append(0, data[..hd.HeaderLength]);
            long stride = (long)hd.W * hd.Ch;
            for (int i = 0; i < hd.N; i++) crc = Crc32C.Combine(crc, crcs[i], hd.StripRows(i) * stride);
            return crc == hd.StoredCrc;
        }
        finally { ArrayPool<uint>.Shared.Return(crcs); }
    }

    private static bool Strip(FicqHeader hd, int i, byte* data, long dataLen, byte* dst, bool verifyCrc, bool simd, out uint crc)
    {
        crc = 0;
        int r = hd.StripRows(i), w = hd.W, ch = hd.Ch;
        long stride = (long)w * ch;
        long outLen = r * stride;
        byte* sd = dst + (long)i * hd.Rows * stride;
        int off = hd.Off[i], len = hd.Len[i];
        var c = hd.Codec[i];
        bool ok;
        switch (c)
        {
            case StripCodec.R0:
                ok = R0Decoder.Local.Decode(data + off, len, dataLen - off, w, r, ch, hd.P, sd, simd);
                break;
            case StripCodec.NFor:
            case StripCodec.F0C:
                ok = BlkCodec.Decode(c, data + off, len, dataLen - off, sd, stride, w, r, ch, hd.P, simd);
                break;
            case StripCodec.GW:
            case StripCodec.GM:
            case StripCodec.Pal:
            case StripCodec.Literal:
            {
                var outSpan = new Span<byte>(sd, (int)outLen);
                if (off + (long)len + Ficq.Padding <= dataLen)
                {
                    var src = new ReadOnlySpan<byte>(data, off + len + Ficq.Padding);
                    ok = ByteMode(c, src, off, outSpan, w, r, ch, hd.Palette, simd);
                }
                else
                {
                    byte[] pad = ArrayPool<byte>.Shared.Rent(len + Ficq.Padding);
                    try
                    {
                        new ReadOnlySpan<byte>(data + off, len).CopyTo(pad);
                        pad.AsSpan(len, Ficq.Padding).Clear();
                        var src = new ReadOnlySpan<byte>(pad, 0, len + Ficq.Padding);
                        ok = ByteMode(c, src, 0, outSpan, w, r, ch, hd.Palette, simd);
                    }
                    finally { ArrayPool<byte>.Shared.Return(pad); }
                }
                break;
            }
            default:
                return false;
        }
        if (ok && verifyCrc) crc = Crc32C.Compute(new ReadOnlySpan<byte>(sd, (int)outLen));
        return ok;
    }

    private static bool ByteMode(StripCodec c, ReadOnlySpan<byte> src, int s0, Span<byte> dst, int w, int r, int ch, PalTables? pal, bool simd) => c switch
    {
        StripCodec.Pal => PalCodec.Decode(src, s0, dst, w, r, ch, pal!, simd),
        StripCodec.Literal => LiteralStrip.Decode(src, s0, dst, w, r, ch),
        _ => QpCodec.Decode(c == StripCodec.GW ? QpLayout.GW : QpLayout.GM, src, s0, dst, w, r, ch, simd),
    };
}
