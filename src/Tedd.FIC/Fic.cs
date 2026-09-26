namespace Tedd.FIC;

public enum FicEffort
{
    Fast,

    Default,

    Max,
}

public enum FicTier
{
    Fast = 0,
    Compact = 1,
}

public readonly record struct FicImageInfo(int Width, int Height, int Channels, FicTier Tier)
{
    public long DecodedLength => (long)Width * Height * Channels;
}

/// <summary>Lossless RGB and RGBA encoding in the version 00 TFIC container.</summary>
public static class Fic
{
    public const string VersionName = "0";

    public const long DefaultMaxPixels = 1L << 28;

    public const long MaxDecodedBytesPerStreamByte = Ficq.MaxOutputPerStreamByte;

    private static void CheckImage(ReadOnlySpan<byte> pixels, int width, int height, int channels)
    {
        if (channels != 3 && channels != 4) throw new ArgumentOutOfRangeException(nameof(channels), "channels must be 3 (RGB) or 4 (RGBA)");
        if (width < 1 || height < 1 || width > Ficq.MaxDim || height > Ficq.MaxDim || (long)width * height > Ficq.MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "image dimensions out of range");
        if (pixels.Length != (long)width * height * channels) throw new ArgumentException("pixel buffer length does not match the dimensions", nameof(pixels));
    }

    public static long GetMaxEncodedLength(int width, int height, int channels, int exifLength = 0)
    {
        if (exifLength < 0 || exifLength > FicContainer.MaxExifLength) throw new ArgumentOutOfRangeException(nameof(exifLength));
        return FicContainer.HeaderSize + exifLength + Ficq.MaxHeaderSize + Ficq.CrcSize + 5L * height + 5L * width * height;
    }

    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, FicEffort effort = FicEffort.Default, int threads = 1, ReadOnlySpan<byte> exif = default)
    {
        CheckImage(pixels, width, height, channels);
        FicContainer.CheckExif(exif);
        byte[] payload = effort == FicEffort.Fast
            ? FastEncoder.Encode(pixels, width, height, channels, FastSettings.Default, threads)
            : CompactEncoder.Encode(pixels, width, height, channels, effort == FicEffort.Max ? CompactSettings.Max : CompactSettings.Default, threads);
        byte[] result = GC.AllocateUninitializedArray<byte>(checked(FicContainer.HeaderSize + exif.Length + payload.Length));
        FicContainer.Write(result, exif);
        payload.CopyTo(result.AsSpan(FicContainer.HeaderSize + exif.Length));
        return result;
    }

    public static bool TryEncode(ReadOnlySpan<byte> pixels, int width, int height, int channels, Span<byte> destination, out int bytesWritten,
        FicEffort effort = FicEffort.Default, int threads = 1, ReadOnlySpan<byte> exif = default)
    {
        CheckImage(pixels, width, height, channels);
        FicContainer.CheckExif(exif);
        int prefix = FicContainer.HeaderSize + exif.Length;
        bytesWritten = 0;
        if (destination.Length < prefix) return false;
        bool ok = effort == FicEffort.Fast
            ? FastEncoder.TryEncode(pixels, width, height, channels, FastSettings.Default, threads, destination[prefix..], out bytesWritten)
            : CompactEncoder.TryEncode(pixels, width, height, channels, effort == FicEffort.Max ? CompactSettings.Max : CompactSettings.Default, threads,
                destination[prefix..], out bytesWritten);
        if (!ok) { bytesWritten = 0; return false; }
        FicContainer.Write(destination, exif);
        bytesWritten += prefix;
        return true;
    }

    public static bool TryGetInfo(ReadOnlySpan<byte> data, out FicImageInfo info)
    {
        if (!FicContainer.TryRead(data, out var payload, out _)) { info = default; return false; }
        using var hd = FicqHeader.TryParse(payload);
        info = hd == null ? default : new FicImageInfo(hd.W, hd.H, hd.Ch, hd.Tier == Ficq.TierFast ? FicTier.Fast : FicTier.Compact);
        return hd != null;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, Span<byte> destination, out FicImageInfo info, int threads = 1, bool verifyChecksum = true)
    {
        if (!FicContainer.TryRead(data, out var payload, out _)) { info = default; return false; }
        return FicqDecoder.TryDecode(payload, destination, threads, verifyChecksum, out info);
    }

    public static byte[] Decode(ReadOnlySpan<byte> data, out int width, out int height, out int channels, int threads = 1, long maxPixels = DefaultMaxPixels)
    {
        if (!TryGetInfo(data, out var info)) throw new InvalidDataException("Not a valid FIC image.");
        if ((long)info.Width * info.Height > maxPixels) throw new InvalidDataException("FIC image exceeds maxPixels.");
        var pixels = GC.AllocateUninitializedArray<byte>((int)info.DecodedLength);
        FicContainer.TryRead(data, out var payload, out _);
        if (!FicqDecoder.TryDecode(payload, pixels, threads, true, out _)) throw new InvalidDataException("Corrupt FIC image.");
        width = info.Width;
        height = info.Height;
        channels = info.Channels;
        return pixels;
    }

    /// <summary>Returns the raw EXIF TIFF bytes without copying them.</summary>
    public static bool TryGetExif(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> exif)
    {
        if (FicContainer.TryRead(data, out _, out exif) && TryGetInfo(data, out _)) return true;
        exif = default;
        return false;
    }
}
