namespace Tedd.FIC;

/// <summary>Controls the encoder's speed and compression effort. Every value produces the same container format.</summary>
public enum FicEffort
{
    /// <summary>Prioritizes encoding and decoding speed.</summary>
    Fast,

    /// <summary>Balances compressed size and processing time.</summary>
    Default,

    /// <summary>Tries additional strip modes to favor compressed size.</summary>
    Max,
}

/// <summary>Identifies the strip-codec family recorded in an encoded image.</summary>
public enum FicTier
{
    /// <summary>Fast strip codecs.</summary>
    Fast = 0,
    /// <summary>Compact strip codecs, used by the Default and Max efforts.</summary>
    Compact = 1,
}

/// <summary>Dimensions, channel count, and codec tier of a structurally valid image.</summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Channels">Bytes per pixel: 3 for RGB or 4 for RGBA.</param>
/// <param name="Tier">Strip-codec family used by the image.</param>
public readonly record struct FicImageInfo(int Width, int Height, int Channels, FicTier Tier)
{
    /// <summary>Gets the required decoded buffer length in bytes.</summary>
    public long DecodedLength => (long)Width * Height * Channels;
}

/// <summary>Encodes and decodes lossless, tightly packed RGB and RGBA images in the version 00 TFIC container.</summary>
/// <remarks>Pixels are in row-major order without row padding. The container can carry raw EXIF TIFF bytes alongside
/// the image payload; the library preserves these bytes without interpreting them.</remarks>
public static class Fic
{
    /// <summary>Gets the current TFIC container version name.</summary>
    public const string VersionName = "0";

    /// <summary>Gets the default maximum pixel count accepted by <see cref="Decode"/>.</summary>
    public const long DefaultMaxPixels = 1L << 28;

    /// <summary>Gets the maximum decoded byte count per encoded payload byte for a valid image.</summary>
    public const long MaxDecodedBytesPerStreamByte = Ficq.MaxOutputPerStreamByte;

    private static void CheckImage(ReadOnlySpan<byte> pixels, int width, int height, int channels)
    {
        if (channels != 3 && channels != 4) throw new ArgumentOutOfRangeException(nameof(channels), "channels must be 3 (RGB) or 4 (RGBA)");
        if (width < 1 || height < 1 || width > Ficq.MaxDim || height > Ficq.MaxDim || (long)width * height > Ficq.MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "image dimensions out of range");
        if (pixels.Length != (long)width * height * channels) throw new ArgumentException("pixel buffer length does not match the dimensions", nameof(pixels));
    }

    /// <summary>Gets an upper bound for the encoded length, including the TFIC header and EXIF bytes.</summary>
    /// <remarks>Use valid image dimensions and channel count. This method checks only the EXIF length.</remarks>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="channels">Bytes per pixel: 3 for RGB or 4 for RGBA.</param>
    /// <param name="exifLength">Length of the optional raw EXIF TIFF payload, in bytes.</param>
    /// <returns>A destination length sufficient for <see cref="TryEncode"/> with these inputs.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exifLength"/> is negative or exceeds 16 MiB.</exception>
    public static long GetMaxEncodedLength(int width, int height, int channels, int exifLength = 0)
    {
        if (exifLength < 0 || exifLength > FicContainer.MaxExifLength) throw new ArgumentOutOfRangeException(nameof(exifLength));
        return FicContainer.HeaderSize + exifLength + Ficq.MaxHeaderSize + Ficq.CrcSize + 5L * height + 5L * width * height;
    }

    /// <summary>Encodes a tightly packed RGB or RGBA image into a new TFIC byte array.</summary>
    /// <param name="pixels">Exactly width × height × channels bytes, in row-major order.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="channels">Bytes per pixel: 3 for RGB or 4 for RGBA.</param>
    /// <param name="effort">Compression effort. The decoder accepts streams from every effort.</param>
    /// <param name="threads">Maximum concurrent strip workers; encoded bytes are independent of this value.</param>
    /// <param name="exif">Optional raw EXIF TIFF bytes to preserve unchanged, up to 16 MiB.</param>
    /// <returns>The complete TFIC container.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The channel count, dimensions, or EXIF length are invalid.</exception>
    /// <exception cref="ArgumentException">The pixel buffer length does not match the dimensions.</exception>
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

    /// <summary>Encodes a tightly packed RGB or RGBA image into a caller-provided buffer.</summary>
    /// <remarks><see cref="GetMaxEncodedLength"/> gives a sufficient destination length. A shorter buffer may also
    /// succeed when the encoded stream fits.</remarks>
    /// <param name="pixels">Exactly width × height × channels bytes, in row-major order.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="channels">Bytes per pixel: 3 for RGB or 4 for RGBA.</param>
    /// <param name="destination">Receives the complete TFIC container on success.</param>
    /// <param name="bytesWritten">Encoded length on success; zero if the destination is too small.</param>
    /// <param name="effort">Compression effort.</param>
    /// <param name="threads">Maximum concurrent strip workers; encoded bytes are independent of this value.</param>
    /// <param name="exif">Optional raw EXIF TIFF bytes to preserve unchanged, up to 16 MiB.</param>
    /// <returns><see langword="true"/> if the stream fits; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The channel count, dimensions, or EXIF length are invalid.</exception>
    /// <exception cref="ArgumentException">The pixel buffer length does not match the dimensions.</exception>
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

    /// <summary>Reads image properties after validating the container, image header, and strip table.</summary>
    /// <remarks>This method checks the encoded payload lengths and decoded size bound. It does not decode pixels or
    /// verify their checksum; use <see cref="TryDecode"/> for that.</remarks>
    /// <param name="data">The complete TFIC container.</param>
    /// <param name="info">Image properties on success; the default value on failure.</param>
    /// <returns><see langword="true"/> if the image structure is valid; otherwise <see langword="false"/>.</returns>
    public static bool TryGetInfo(ReadOnlySpan<byte> data, out FicImageInfo info)
    {
        if (!FicContainer.TryRead(data, out var payload, out _)) { info = default; return false; }
        using var hd = FicqHeader.TryParse(payload);
        info = hd == null ? default : new FicImageInfo(hd.W, hd.H, hd.Ch, hd.Tier == Ficq.TierFast ? FicTier.Fast : FicTier.Compact);
        return hd != null;
    }

    /// <summary>Decodes an image into a caller-provided pixel buffer.</summary>
    /// <remarks>Allocate at least <see cref="FicImageInfo.DecodedLength"/> bytes using <see cref="TryGetInfo"/>.
    /// Failure may leave the destination partially written. Temporary strip scratch is pooled.</remarks>
    /// <param name="data">The complete TFIC container.</param>
    /// <param name="destination">Receives row-major, tightly packed RGB or RGBA pixels.</param>
    /// <param name="info">Image properties when the image header is valid and the destination is large enough;
    /// otherwise the default value. A populated value does not imply successful decoding.</param>
    /// <param name="threads">Maximum concurrent strip workers.</param>
    /// <param name="verifyChecksum">Verify the image payload's pixel checksum. EXIF bytes are never covered by it.</param>
    /// <returns><see langword="true"/> if decoding and the requested checksum check succeed; otherwise
    /// <see langword="false"/>.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> data, Span<byte> destination, out FicImageInfo info, int threads = 1, bool verifyChecksum = true)
    {
        if (!FicContainer.TryRead(data, out var payload, out _)) { info = default; return false; }
        return FicqDecoder.TryDecode(payload, destination, threads, verifyChecksum, out info);
    }

    /// <summary>Decodes a TFIC image into a new, tightly packed pixel array.</summary>
    /// <param name="data">The complete TFIC container.</param>
    /// <param name="width">Decoded width in pixels on success.</param>
    /// <param name="height">Decoded height in pixels on success.</param>
    /// <param name="channels">Decoded bytes per pixel on success: 3 for RGB or 4 for RGBA.</param>
    /// <param name="threads">Maximum concurrent strip workers.</param>
    /// <param name="maxPixels">Maximum accepted width × height, checked before output allocation.</param>
    /// <returns>The decoded pixels in row-major order without row padding.</returns>
    /// <exception cref="InvalidDataException">The image is invalid, corrupt, fails its pixel checksum, or exceeds
    /// <paramref name="maxPixels"/>.</exception>
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

    /// <summary>Gets the raw EXIF TIFF payload without copying it.</summary>
    /// <remarks>The returned span aliases <paramref name="data"/> and is valid only while that storage remains
    /// available. The image structure is checked, but neither the pixels nor their checksum are verified. EXIF tags
    /// are not parsed or validated.</remarks>
    /// <param name="data">The complete TFIC container.</param>
    /// <param name="exif">The raw EXIF bytes on success, or an empty span on failure.</param>
    /// <returns><see langword="true"/> if the image structure is valid, including when no EXIF is present; otherwise
    /// <see langword="false"/>.</returns>
    public static bool TryGetExif(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> exif)
    {
        if (FicContainer.TryRead(data, out _, out exif) && TryGetInfo(data, out _)) return true;
        exif = default;
        return false;
    }
}
