using System.Buffers;

namespace Tedd.FIC;

/// <summary>Controls the encoder's speed and compression effort. Every value produces the same container format.</summary>
public enum FicEffort
{
    /// <summary>Prioritizes encoding and decoding speed.</summary>
    Fast,

    /// <summary>Balances compressed size and processing time.</summary>
    Default,

    /// <summary>Tries additional strip modes and strip partitions to favor compressed size.</summary>
    Max,
}

/// <summary>Compression of the FICQ payload inside a FIC container.</summary>
public enum FicCompression : byte
{
    /// <summary>Store the FICQ payload unchanged.</summary>
    None = 0,
    /// <summary>Raw Deflate.</summary>
    Deflate = 1,
    /// <summary>GZip.</summary>
    Gzip = 2,
    /// <summary>Zstandard, quality 1 for Fast effort and 2 otherwise.</summary>
    Zstd = 3,
    /// <summary>Brotli quality 5.</summary>
    Brotli = 4,
    /// <summary>Use no outer compression for Fast, Zstandard for Default, or Brotli for Max.</summary>
    Auto = 255,
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

/// <summary>Encodes and decodes lossless, tightly packed RGB and RGBA images in FIC containers.</summary>
/// <remarks>Pixels are in row-major order without row padding. The container can carry raw EXIF TIFF bytes alongside
/// the image payload; the library preserves these bytes without interpreting them.</remarks>
public static class Fic
{
    /// <summary>Gets the latest FIC container version name. Explicitly uncompressed output uses version 00.</summary>
    public const string VersionName = "2";

    /// <summary>Gets the default maximum pixel count accepted by <see cref="Decode"/>.</summary>
    public const long DefaultMaxPixels = 1L << 28;

    /// <summary>Maximum expanded FICQ payload accepted by default from a compressed frame.</summary>
    public const int DefaultMaxCompressedPayloadBytes = 256 * 1024 * 1024;

    /// <summary>Gets the maximum decoded byte count per uncompressed FICQ payload byte for a valid image.</summary>
    public const long MaxDecodedBytesPerStreamByte = Ficq.MaxOutputPerStreamByte;

    private static void CheckImage(ReadOnlySpan<byte> pixels, int width, int height, int channels)
    {
        if (channels != 3 && channels != 4) throw new ArgumentOutOfRangeException(nameof(channels), "channels must be 3 (RGB) or 4 (RGBA)");
        if (width < 1 || height < 1 || width > Ficq.MaxDim || height > Ficq.MaxDim || (long)width * height > Ficq.MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "image dimensions out of range");
        if (pixels.Length != (long)width * height * channels) throw new ArgumentException("pixel buffer length does not match the dimensions", nameof(pixels));
    }

    /// <summary>Gets an upper bound for the encoded length, including the FIC header and EXIF bytes.</summary>
    /// <remarks>Use valid image dimensions and channel count. This method checks only the EXIF length.</remarks>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="channels">Bytes per pixel: 3 for RGB or 4 for RGBA.</param>
    /// <param name="exifLength">Length of the optional raw EXIF TIFF payload, in bytes.</param>
    /// <param name="compression">Optional compression of the FICQ payload.</param>
    /// <returns>A destination length sufficient for <see cref="TryEncode"/> with these inputs.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exifLength"/> is negative or exceeds 16 MiB.</exception>
    public static long GetMaxEncodedLength(int width, int height, int channels, int exifLength = 0, FicCompression compression = FicCompression.Auto)
    {
        if (exifLength < 0 || exifLength > FicContainer.MaxExifLength) throw new ArgumentOutOfRangeException(nameof(exifLength));
        CheckCompression(compression);
        long payloadBound = checked(Ficq.MaxHeaderSize + Ficq.CrcSize + 5L * height + 5L * width * height);
        if (compression == FicCompression.None) return checked(FicContainer.LegacyHeaderSize + exifLength + payloadBound);
        // A compressed request falls back to the raw FICQ payload if compression expands it.
        return checked(FicContainer.CurrentHeaderSize + exifLength + payloadBound);
    }

    private static void CheckCompression(FicCompression compression)
    {
        if (compression is not (FicCompression.None or FicCompression.Deflate or FicCompression.Gzip or FicCompression.Zstd or FicCompression.Brotli or FicCompression.Auto))
            throw new ArgumentOutOfRangeException(nameof(compression));
    }

    private static FicCompression ResolveCompression(FicCompression compression, FicEffort effort) =>
        compression == FicCompression.Auto ? effort switch
        {
            FicEffort.Fast => FicCompression.None,
            FicEffort.Max => FicCompression.Brotli,
            _ => FicCompression.Zstd,
        } : compression;

    private static int ResolveZstdLevel(FicCompression selected, FicEffort effort, int zstdLevel)
    {
        if (zstdLevel < 0 || zstdLevel > 22 || (selected != FicCompression.Zstd && zstdLevel != 0))
            throw new ArgumentOutOfRangeException(nameof(zstdLevel));
        return zstdLevel == 0 ? effort == FicEffort.Fast ? 1 : 2 : zstdLevel;
    }

    /// <summary>Encodes a tightly packed RGB or RGBA image into a new FIC byte array.</summary>
    /// <param name="pixels">Exactly width × height × channels bytes, in row-major order.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="channels">Bytes per pixel: 3 for RGB or 4 for RGBA.</param>
    /// <param name="effort">Compression effort. The decoder accepts streams from every effort.</param>
    /// <param name="threads">Maximum concurrent strip workers; encoded bytes are independent of this value.</param>
    /// <param name="exif">Optional raw EXIF TIFF bytes to preserve unchanged, up to 16 MiB.</param>
    /// <param name="compression">Outer compression codec; Auto selects a codec from the effort.</param>
    /// <param name="zstdLevel">Zstandard quality 1–22; zero uses quality 1 for Fast or 2 otherwise.</param>
    /// <returns>The complete FIC container.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The channel count, dimensions, or EXIF length are invalid.</exception>
    /// <exception cref="ArgumentException">The pixel buffer length does not match the dimensions.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, FicEffort effort = FicEffort.Default, int threads = 1,
        ReadOnlySpan<byte> exif = default, FicCompression compression = FicCompression.Auto, int zstdLevel = 0)
    {
        CheckImage(pixels, width, height, channels);
        FicContainer.CheckExif(exif);
        CheckCompression(compression);
        var selected = ResolveCompression(compression, effort);
        int quality = ResolveZstdLevel(selected, effort, zstdLevel);
        var settings = effort == FicEffort.Max ? CompactSettings.Max : CompactSettings.Default;
        byte[] best = EncodeContainer(pixels, width, height, channels, effort, threads, exif, settings, selected, quality);
        if (effort != FicEffort.Max) return best;

        // A second partition can improve both the strip coding and the outer stream.
        // Compare complete containers because smaller FICQ need not compress better.
        int trialRows = (int)Math.Min(512L, Math.Max(64L, 2_097_152L / width));
        if (FastEncoder.StripRows(width, height, trialRows, settings.MinStripPixels) ==
            FastEncoder.StripRows(width, height, settings.Rows, settings.MinStripPixels)) return best;
        byte[] trial = EncodeContainer(pixels, width, height, channels, effort, threads, exif,
            settings with { Rows = trialRows }, selected, quality);
        return trial.Length < best.Length ? trial : best;
    }

    private static byte[] EncodeContainer(ReadOnlySpan<byte> pixels, int width, int height, int channels,
        FicEffort effort, int threads, ReadOnlySpan<byte> exif, CompactSettings settings, FicCompression selected, int quality)
    {
        byte[] payload = effort == FicEffort.Fast
            ? FastEncoder.Encode(pixels, width, height, channels, FastSettings.Default, threads)
            : CompactEncoder.Encode(pixels, width, height, channels, settings, threads);
        if (selected == FicCompression.None)
        {
            byte[] legacy = GC.AllocateUninitializedArray<byte>(checked(FicContainer.LegacyHeaderSize + exif.Length + payload.Length));
            FicContainer.Write(legacy, exif, FicCompression.None, version1: false);
            payload.CopyTo(legacy.AsSpan(FicContainer.LegacyHeaderSize + exif.Length));
            return legacy;
        }
        byte[] scratch = ArrayPool<byte>.Shared.Rent(payload.Length);
        int prefix = FicContainer.CurrentHeaderSize + exif.Length;
        try
        {
            bool compressed = OuterCompression.TryCompress(payload, scratch, selected, quality, out int compressedLength)
                && compressedLength < payload.Length;
            byte[] result = GC.AllocateUninitializedArray<byte>(checked(prefix + (compressed ? compressedLength : payload.Length)));
            FicContainer.WriteCurrent(result, exif, compressed ? selected : FicCompression.None, payload.Length);
            (compressed ? scratch.AsSpan(0, compressedLength) : payload.AsSpan()).CopyTo(result.AsSpan(prefix));
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    /// <summary>Encodes a tightly packed RGB or RGBA image into a caller-provided buffer.</summary>
    /// <remarks><see cref="GetMaxEncodedLength"/> gives a sufficient destination length. A shorter buffer may also
    /// succeed when the encoded stream fits. Max effort compares two complete encodings before copying the winner;
    /// other efforts write compression into the destination and pool temporary FICQ storage.</remarks>
    /// <param name="pixels">Exactly width × height × channels bytes, in row-major order.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="channels">Bytes per pixel: 3 for RGB or 4 for RGBA.</param>
    /// <param name="destination">Receives the complete FIC container on success.</param>
    /// <param name="bytesWritten">Encoded length on success; zero if the destination is too small.</param>
    /// <param name="effort">Compression effort.</param>
    /// <param name="threads">Maximum concurrent strip workers; encoded bytes are independent of this value.</param>
    /// <param name="exif">Optional raw EXIF TIFF bytes to preserve unchanged, up to 16 MiB.</param>
    /// <param name="compression">Outer compression codec; Auto selects a codec from the effort.</param>
    /// <param name="zstdLevel">Zstandard quality 1–22; zero uses quality 1 for Fast or 2 otherwise.</param>
    /// <returns><see langword="true"/> if the stream fits; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The channel count, dimensions, or EXIF length are invalid.</exception>
    /// <exception cref="ArgumentException">The pixel buffer length does not match the dimensions.</exception>
    public static bool TryEncode(ReadOnlySpan<byte> pixels, int width, int height, int channels, Span<byte> destination, out int bytesWritten,
        FicEffort effort = FicEffort.Default, int threads = 1, ReadOnlySpan<byte> exif = default, FicCompression compression = FicCompression.Auto,
        int zstdLevel = 0)
    {
        CheckImage(pixels, width, height, channels);
        FicContainer.CheckExif(exif);
        CheckCompression(compression);
        bytesWritten = 0;
        if (effort == FicEffort.Max)
        {
            byte[] encoded = Encode(pixels, width, height, channels, effort, threads, exif, compression, zstdLevel);
            if (encoded.Length > destination.Length) return false;
            encoded.CopyTo(destination);
            bytesWritten = encoded.Length;
            return true;
        }
        var selected = ResolveCompression(compression, effort);
        int quality = ResolveZstdLevel(selected, effort, zstdLevel);
        if (selected != FicCompression.None)
        {
            int prefixSize = FicContainer.CurrentHeaderSize + exif.Length;
            if (destination.Length < prefixSize) return false;
            long payloadBound = GetMaxEncodedLength(width, height, channels, compression: FicCompression.None) - FicContainer.LegacyHeaderSize;
            int capacity = (int)Math.Min(int.MaxValue, Math.Min(payloadBound, (long)pixels.Length * 2 + 65536));
            byte[] rented;
            int payloadLength;
            while (true)
            {
                rented = ArrayPool<byte>.Shared.Rent(capacity);
                bool fits = effort == FicEffort.Fast
                    ? FastEncoder.TryEncode(pixels, width, height, channels, FastSettings.Default, threads, rented, out payloadLength)
                    : CompactEncoder.TryEncode(pixels, width, height, channels,
                        effort == FicEffort.Max ? CompactSettings.Max : CompactSettings.Default, threads, rented, out payloadLength);
                if (fits) break;
                ArrayPool<byte>.Shared.Return(rented);
                if (capacity == int.MaxValue) return false;
                capacity = (int)Math.Min(int.MaxValue, Math.Min(payloadBound, (long)capacity * 2));
            }
            try
            {
                ReadOnlySpan<byte> payload = rented.AsSpan(0, payloadLength);
                bool compressed = OuterCompression.TryCompress(payload, destination[prefixSize..], selected, quality, out int length)
                    && length < payloadLength;
                if (!compressed && destination.Length - prefixSize < payloadLength)
                {
                    // Some one-shot encoders require spare capacity even when their final frame fits.
                    byte[] scratch = ArrayPool<byte>.Shared.Rent(payloadLength);
                    try
                    {
                        compressed = OuterCompression.TryCompress(payload, scratch, selected, quality, out length)
                            && length < payloadLength && length <= destination.Length - prefixSize;
                        if (compressed) scratch.AsSpan(0, length).CopyTo(destination[prefixSize..]);
                    }
                    finally { ArrayPool<byte>.Shared.Return(scratch); }
                }
                if (!compressed)
                {
                    if (destination.Length - prefixSize < payloadLength) return false;
                    payload.CopyTo(destination[prefixSize..]);
                    length = payloadLength;
                }
                FicContainer.WriteCurrent(destination, exif, compressed ? selected : FicCompression.None, payloadLength);
                bytesWritten = prefixSize + length;
                return true;
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }
        int prefix = FicContainer.LegacyHeaderSize + exif.Length;
        if (destination.Length < prefix) return false;
        bool ok = effort == FicEffort.Fast
            ? FastEncoder.TryEncode(pixels, width, height, channels, FastSettings.Default, threads, destination[prefix..], out bytesWritten)
            : CompactEncoder.TryEncode(pixels, width, height, channels, effort == FicEffort.Max ? CompactSettings.Max : CompactSettings.Default, threads,
                destination[prefix..], out bytesWritten);
        if (!ok) { bytesWritten = 0; return false; }
        FicContainer.Write(destination, exif, FicCompression.None, version1: false);
        bytesWritten += prefix;
        return true;
    }

    /// <summary>Reads image properties after validating the container, image header, and strip table.</summary>
    /// <remarks>This method checks the encoded payload lengths and decoded size bound. It expands a compressed payload
    /// before inspecting it. It does not decode pixels or verify their checksum; use <see cref="TryDecode"/> for that.</remarks>
    /// <param name="data">The complete FIC container.</param>
    /// <param name="info">Image properties on success; the default value on failure.</param>
    /// <param name="maxCompressedPayloadBytes">Maximum accepted expanded payload length.</param>
    /// <returns><see langword="true"/> if the image structure is valid; otherwise <see langword="false"/>.</returns>
    public static bool TryGetInfo(ReadOnlySpan<byte> data, out FicImageInfo info, int maxCompressedPayloadBytes = DefaultMaxCompressedPayloadBytes)
    {
        if (!TryReadPayload(data, maxCompressedPayloadBytes, out var payload)) { info = default; return false; }
        using var hd = FicqHeader.TryParse(payload);
        info = hd == null ? default : new FicImageInfo(hd.W, hd.H, hd.Ch, hd.Tier == Ficq.TierFast ? FicTier.Fast : FicTier.Compact);
        return hd != null;
    }

    private static bool TryReadPayload(ReadOnlySpan<byte> data, int maxCompressedPayloadBytes, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (!FicContainer.TryRead(data, out var stored, out _, out var compression, out int expandedLength, out byte version)) return false;
        if (compression == FicCompression.None) { payload = stored; return true; }
        if (maxCompressedPayloadBytes < 1) return false;
        try
        {
            if (version == 1)
            {
                if (!OuterCompression.TryGetZstdDecompressedLength(stored, out long oldLength)
                    || oldLength < 1 || oldLength > maxCompressedPayloadBytes) return false;
                expandedLength = (int)oldLength;
            }
            if (expandedLength < 1 || expandedLength > maxCompressedPayloadBytes) return false;
            byte[] expanded = GC.AllocateUninitializedArray<byte>(expandedLength);
            if (!OuterCompression.TryDecompress(stored, expanded, version == 1 ? FicCompression.Zstd : compression, out int written)
                || written != expanded.Length) return false;
            payload = expanded;
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Decodes an image into a caller-provided pixel buffer.</summary>
    /// <remarks>Allocate at least <see cref="FicImageInfo.DecodedLength"/> bytes using <see cref="TryGetInfo"/>.
    /// Failure may leave the destination partially written. Temporary strip scratch is pooled.</remarks>
    /// <param name="data">The complete FIC container.</param>
    /// <param name="destination">Receives row-major, tightly packed RGB or RGBA pixels.</param>
    /// <param name="info">Image properties when the image header is valid and the destination is large enough;
    /// otherwise the default value. A populated value does not imply successful decoding.</param>
    /// <param name="threads">Maximum concurrent strip workers.</param>
    /// <param name="verifyChecksum">Verify the image payload's pixel checksum. EXIF bytes are never covered by it.</param>
    /// <param name="maxCompressedPayloadBytes">Maximum accepted expanded payload length.</param>
    /// <returns><see langword="true"/> if decoding and the requested checksum check succeed; otherwise
    /// <see langword="false"/>.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> data, Span<byte> destination, out FicImageInfo info, int threads = 1,
        bool verifyChecksum = true, int maxCompressedPayloadBytes = DefaultMaxCompressedPayloadBytes)
    {
        if (!TryReadPayload(data, maxCompressedPayloadBytes, out var payload)) { info = default; return false; }
        return FicqDecoder.TryDecode(payload, destination, threads, verifyChecksum, out info);
    }

    /// <summary>Decodes a FIC image into a new, tightly packed pixel array.</summary>
    /// <param name="data">The complete FIC container.</param>
    /// <param name="width">Decoded width in pixels on success.</param>
    /// <param name="height">Decoded height in pixels on success.</param>
    /// <param name="channels">Decoded bytes per pixel on success: 3 for RGB or 4 for RGBA.</param>
    /// <param name="threads">Maximum concurrent strip workers.</param>
    /// <param name="maxPixels">Maximum accepted width × height, checked before output allocation.</param>
    /// <param name="maxCompressedPayloadBytes">Maximum accepted expanded payload length.</param>
    /// <returns>The decoded pixels in row-major order without row padding.</returns>
    /// <exception cref="InvalidDataException">The image is invalid, corrupt, fails its pixel checksum, or exceeds
    /// <paramref name="maxPixels"/>.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> data, out int width, out int height, out int channels, int threads = 1,
        long maxPixels = DefaultMaxPixels, int maxCompressedPayloadBytes = DefaultMaxCompressedPayloadBytes)
    {
        if (!TryReadPayload(data, maxCompressedPayloadBytes, out var payload)) throw new InvalidDataException("Not a valid FIC image.");
        using var hd = FicqHeader.TryParse(payload);
        if (hd == null) throw new InvalidDataException("Not a valid FIC image.");
        var info = new FicImageInfo(hd.W, hd.H, hd.Ch, hd.Tier == Ficq.TierFast ? FicTier.Fast : FicTier.Compact);
        if ((long)info.Width * info.Height > maxPixels) throw new InvalidDataException("FIC image exceeds maxPixels.");
        var pixels = GC.AllocateUninitializedArray<byte>((int)info.DecodedLength);
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
    /// <param name="data">The complete FIC container.</param>
    /// <param name="exif">The raw EXIF bytes on success, or an empty span on failure.</param>
    /// <param name="maxCompressedPayloadBytes">Maximum accepted expanded payload length.</param>
    /// <returns><see langword="true"/> if the image structure is valid, including when no EXIF is present; otherwise
    /// <see langword="false"/>.</returns>
    public static bool TryGetExif(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> exif,
        int maxCompressedPayloadBytes = DefaultMaxCompressedPayloadBytes)
    {
        if (FicContainer.TryRead(data, out _, out exif, out _, out _, out _) && TryGetInfo(data, out _, maxCompressedPayloadBytes)) return true;
        exif = default;
        return false;
    }
}
