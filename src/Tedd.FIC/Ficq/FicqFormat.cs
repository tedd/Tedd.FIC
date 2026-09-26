using System.Buffers;
using System.Buffers.Binary;

namespace Tedd.FIC;

internal enum StripCodec : byte
{
    R0,
    GW,
    GM,
    Pal,
    NFor,
    F0C,
    Literal,
    Invalid = 255,
}

internal static class Ficq
{
    public const uint Magic = 0x51434946u;
    public const byte Version = 2;
    public const byte TierFast = 0, TierCompact = 1;

    public const int MaxDim = 1 << 24;
    public const long MaxPixels = 1L << 29;
    public const int MaxPlanarWidth = 1 << 16;
    public const int MinPlanarWidth = 16;
    public const int MinStripPixels = 4096;
    public const int SlotBits = 3;
    public const int MaxRun = 1024;
    public const int Padding = 8;
    public const int CrcSize = 4;
    public const int MaxPaletteColours = 192, PaletteClasses = 10;

    public const int FlagRgba = 1, FlagAlphaPlane = 2, FlagPalette = 4;

    public const long MaxOutputPerStreamByte = MaxRun * 4 / 3 + 1;

    public static StripCodec SlotCodec(int tier, int slot) => tier switch
    {
        TierFast => slot switch { 0 => StripCodec.NFor, 1 => StripCodec.F0C, 2 => StripCodec.GW, 3 => StripCodec.Literal, _ => StripCodec.Invalid },
        TierCompact => slot switch { 0 => StripCodec.R0, 1 => StripCodec.GW, 2 => StripCodec.GM, 3 => StripCodec.Pal, 4 => StripCodec.Literal, _ => StripCodec.Invalid },
        _ => StripCodec.Invalid,
    };

    public static int CodecSlot(int tier, StripCodec c)
    {
        for (int s = 0; s < 1 << SlotBits; s++) if (SlotCodec(tier, s) == c) return s;
        throw new ArgumentException($"codec {c} is not part of tier {tier}");
    }

    public static bool IsPlanar(StripCodec c) => c is StripCodec.R0 or StripCodec.NFor or StripCodec.F0C;

    public static bool PlanarWidth(int w) => w >= MinPlanarWidth && w <= MaxPlanarWidth;

    public static int MinStripRows(int w, int h) => (int)Math.Min(h, (MinStripPixels + (long)w - 1) / w);

    public static long TableEntry(int len, int slot) => (long)len << SlotBits | (uint)slot;

    public static long MinPayload(StripCodec c, int w, int rows, int P)
    {
        long px = (long)w * rows;
        switch (c)
        {
            case StripCodec.R0:
            {
                long perPlaneRow = Math.Min(w, 1 + ((w + 7) >> 3));
                return 3 + (rows * P * perPlaneRow + 7) / 8;
            }
            case StripCodec.NFor:
            {
                int nfull = w >> 3, tail = w & 7;
                long bitsRow = 8L * (nfull / 17) + (nfull % 17 != 0 ? 4 : 0) + (tail != 0 ? 4 * P : 0);
                return (rows * bitsRow + 7) / 8;
            }
            case StripCodec.F0C:
            {
                int nfb = w >> 2, nbx = (w + 3) >> 2;
                long bitsBand = 8L * (nfb / 17) + (nfb % 17 != 0 ? 4 : 0) + ((w & 3) != 0 ? 5 * P : 0);
                long bits = (rows >> 2) * bitsBand + ((rows & 3) != 0 ? (long)nbx * 5 * P : 0);
                return (bits + 7) / 8;
            }
            case StripCodec.GW:
            case StripCodec.GM:
            case StripCodec.Pal:
                return (3 * px + MaxRun - 1) / MaxRun;
            case StripCodec.Literal:
                return LiteralStrip.MinPayload(px);
            default:
                return long.MaxValue;
        }
    }


    public static long ReadVarint(ReadOnlySpan<byte> d, ref int pos)
    {
        long v = 0;
        for (int sh = 0; sh <= 28; sh += 7)
        {
            if (pos >= d.Length) return -1;
            int b = d[pos++];
            v |= (long)(b & 0x7F) << sh;
            if (b < 0x80) return b == 0 && sh > 0 ? -1 : v;
        }
        return -1;
    }

    public static int VarintLength(long v)
    {
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }

    public static int WriteVarint(Span<byte> d, long v)
    {
        int n = 0;
        while (v >= 0x80) { d[n++] = (byte)(v | 0x80); v >>= 7; }
        d[n++] = (byte)v;
        return n;
    }

    public static int WriteHeader(Span<byte> d, int tier, int w, int h, int ch, int P, int rows, uint[]? palette, byte[]? shorts)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, Magic);
        d[4] = Version;
        d[5] = (byte)tier;
        int o = 6;
        o += WriteVarint(d[o..], w);
        o += WriteVarint(d[o..], h);
        d[o++] = (byte)((ch == 4 ? FlagRgba : 0) | (P == 4 ? FlagAlphaPlane : 0) | (palette != null ? FlagPalette : 0));
        o += WriteVarint(d[o..], rows);
        if (palette != null)
        {
            d[o++] = (byte)(palette.Length - 1);
            foreach (uint c in palette)
                for (int b = 0; b < ch; b++) d[o++] = (byte)(c >> (8 * b));
            shorts!.CopyTo(d[o..]);
            o += shorts!.Length;
        }
        return o;
    }

    public const int MaxHeaderSize = 6 + 5 + 5 + 1 + 5 + 1 + MaxPaletteColours * 4 + PaletteClasses;

    public const int MaxTableEntry = 5;
}

internal sealed class FicqHeader : IDisposable
{
    public int Tier, W, H, Ch, P, Rows, N;
    public int HeaderLength;
    public int PayloadStart;
    public uint StoredCrc;
    public PalTables? Palette;
    public int[] Off = [];
    public int[] Len = [];
    public StripCodec[] Codec = [];
    private bool _rented;

    public int StripRows(int i) => Math.Min(Rows, H - i * Rows);
    public long OutputBytes => (long)W * H * Ch;

    public void Dispose()
    {
        if (_rented)
        {
            ArrayPool<int>.Shared.Return(Off);
            ArrayPool<int>.Shared.Return(Len);
            ArrayPool<StripCodec>.Shared.Return(Codec);
            _rented = false;
        }
        Palette?.Dispose();
        Palette = null;
        Off = Len = [];
        Codec = [];
    }

    public static FicqHeader? TryParse(ReadOnlySpan<byte> d)
    {
        var h = new FicqHeader();
        if (h.Parse(d)) return h;
        h.Dispose();
        return null;
    }

    private bool Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(d) != Ficq.Magic || d[4] != Ficq.Version) return false;
        Tier = d[5];
        if (Tier != Ficq.TierFast && Tier != Ficq.TierCompact) return false;
        int pos = 6;
        long w = Ficq.ReadVarint(d, ref pos), h = Ficq.ReadVarint(d, ref pos);
        if (w < 1 || h < 1 || w > Ficq.MaxDim || h > Ficq.MaxDim || w * h > Ficq.MaxPixels || pos >= d.Length) return false;
        int flags = d[pos++];
        if ((flags & ~(Ficq.FlagRgba | Ficq.FlagAlphaPlane | Ficq.FlagPalette)) != 0) return false;
        W = (int)w; H = (int)h;
        Ch = (flags & Ficq.FlagRgba) != 0 ? 4 : 3;
        P = (flags & Ficq.FlagAlphaPlane) != 0 ? 4 : 3;
        if (P == 4 && Ch != 4) return false;
        if (w * h * Ch > Array.MaxLength) return false;
        bool pal = (flags & Ficq.FlagPalette) != 0;
        if (pal && Tier != Ficq.TierCompact) return false;
        long rows = Ficq.ReadVarint(d, ref pos);
        if (rows < Ficq.MinStripRows(W, H) || rows > h) return false;
        Rows = (int)rows;
        if (pal)
        {
            if (pos >= d.Length) return false;
            int k = d[pos++] + 1;
            if (k > Ficq.MaxPaletteColours || (long)pos + k * Ch + Ficq.PaletteClasses > d.Length) return false;
            Palette = PalTables.Build(d.Slice(pos, k * Ch), k, Ch, d.Slice(pos + k * Ch, Ficq.PaletteClasses));
            if (Palette == null) return false;
            pos += k * Ch + Ficq.PaletteClasses;
        }
        HeaderLength = pos;
        long n = (h + rows - 1) / rows;
        if (n > (d.Length - pos - Ficq.CrcSize) / 2) return false;
        N = (int)n;
        Off = ArrayPool<int>.Shared.Rent(N);
        Len = ArrayPool<int>.Shared.Rent(N);
        Codec = ArrayPool<StripCodec>.Shared.Rent(N);
        _rented = true;
        long payloadEnd = d.Length - Ficq.CrcSize;
        for (int i = 0; i < N; i++)
        {
            long v = Ficq.ReadVarint(d, ref pos);
            if (v < 0 || pos > payloadEnd) return false;
            long len = v >> Ficq.SlotBits;
            var c = Ficq.SlotCodec(Tier, (int)(v & ((1 << Ficq.SlotBits) - 1)));
            if (c == StripCodec.Invalid || (c == StripCodec.Pal && Palette == null) || len > int.MaxValue) return false;
            if (Ficq.IsPlanar(c) && !Ficq.PlanarWidth(W)) return false;
            if (len < Ficq.MinPayload(c, W, StripRows(i), P)) return false;
            Len[i] = (int)len;
            Codec[i] = c;
        }
        PayloadStart = pos;
        long off = pos;
        for (int i = 0; i < N; i++)
        {
            Off[i] = (int)off;
            off += Len[i];
            if (off > payloadEnd) return false;
        }
        if (off != payloadEnd) return false;
        StoredCrc = BinaryPrimitives.ReadUInt32LittleEndian(d[(int)payloadEnd..]);
        return true;
    }
}

internal sealed class PalTables : IDisposable
{
    public const uint Valid = 1u << 21;

    public uint[] Pal = [];
    public uint[] Grp = [];
    public int K;

    public static PalTables? Build(ReadOnlySpan<byte> palBytes, int k, int ch, ReadOnlySpan<byte> shorts)
    {
        if (k < 1 || k > Ficq.MaxPaletteColours || shorts.Length != Ficq.PaletteClasses || palBytes.Length != k * ch) return null;
        var t = new PalTables { K = k, Pal = ArrayPool<uint>.Shared.Rent(256), Grp = ArrayPool<uint>.Shared.Rent(256) };
        Array.Clear(t.Pal, 0, 256);
        Array.Clear(t.Grp, 0, 256);
        for (int i = 0; i < k; i++)
        {
            uint c = ch == 3 ? 0xFF000000u : 0u;
            for (int b = 0; b < ch; b++) c |= (uint)palBytes[i * ch + b] << (8 * b);
            t.Pal[i] = c;
        }
        int code = k;
        for (int c = 0; c < Ficq.PaletteClasses; c++)
        {
            int sc = shorts[c];
            for (int len = 1; len <= sc; len++)
            {
                if (code > 255) { t.Dispose(); return null; }
                t.Grp[code++] = Valid | (uint)c << 16 | (uint)len;
            }
            if (code > 255) { t.Dispose(); return null; }
            t.Grp[code++] = Valid | (uint)c << 16 | (uint)sc << 22;
        }
        return t;
    }

    public void Dispose()
    {
        if (Pal.Length > 0) ArrayPool<uint>.Shared.Return(Pal);
        if (Grp.Length > 0) ArrayPool<uint>.Shared.Return(Grp);
        Pal = Grp = [];
    }
}
