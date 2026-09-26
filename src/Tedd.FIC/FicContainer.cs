using System.Buffers.Binary;

namespace Tedd.FIC;

/// <summary>Reads and writes FIC prefixes before the FICQ image payload.</summary>
/// <remarks>Version 01 used 1 for Zstandard. Version 02 assigns 1–4 to Deflate, GZip, Zstandard, and Brotli.</remarks>
internal static class FicContainer
{
    public const int LegacyHeaderSize = 9;
    public const int HeaderSize = 10;
    public const int CurrentHeaderSize = 14;
    public const int MaxExifLength = 16 * 1024 * 1024;

    /// <summary>Rejects EXIF payloads that exceed the container's 16 MiB limit.</summary>
    public static void CheckExif(ReadOnlySpan<byte> exif)
    {
        if (exif.Length > MaxExifLength) throw new ArgumentOutOfRangeException(nameof(exif));
    }

    /// <summary>Writes the fixed prefix and EXIF bytes; the caller writes the image payload after them.</summary>
    public static void Write(Span<byte> destination, ReadOnlySpan<byte> exif, FicCompression compression, bool version1)
    {
        "FIC\0"u8.CopyTo(destination);
        destination[4] = version1 ? (byte)1 : (byte)0;
        int exifLengthOffset = version1 ? 6 : 5;
        if (version1) destination[5] = (byte)compression;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[exifLengthOffset..], (uint)exif.Length);
        exif.CopyTo(destination[(exifLengthOffset + 4)..]);
    }

    public static void WriteCurrent(Span<byte> destination, ReadOnlySpan<byte> exif, FicCompression compression, int payloadLength)
    {
        "FIC\0"u8.CopyTo(destination);
        destination[4] = 2;
        destination[5] = (byte)compression;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[6..], (uint)exif.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[10..], (uint)payloadLength);
        exif.CopyTo(destination[CurrentHeaderSize..]);
    }

    /// <summary>Splits a valid FIC prefix into spans of its image payload and raw EXIF bytes.</summary>
    /// <remarks>Checks the prefix and EXIF length only; the caller must validate the image payload separately.</remarks>
    public static bool TryRead(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> exif,
        out FicCompression compression, out int expandedLength, out byte version)
    {
        payload = exif = default;
        compression = FicCompression.None;
        expandedLength = 0;
        version = 0;
        if (data.Length < LegacyHeaderSize || !data[..4].SequenceEqual("FIC\0"u8)) return false;
        int headerSize;
        if (data[4] == 0) headerSize = LegacyHeaderSize;
        else if (data[4] == 1 && data.Length >= HeaderSize && data[5] <= 1)
        {
            headerSize = HeaderSize;
            compression = (FicCompression)data[5];
        }
        else if (data[4] == 2 && data.Length >= CurrentHeaderSize && data[5] <= (byte)FicCompression.Brotli)
        {
            headerSize = CurrentHeaderSize;
            compression = (FicCompression)data[5];
            uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
            if (rawLength == 0 || rawLength > int.MaxValue) return false;
            expandedLength = (int)rawLength;
        }
        else return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[(headerSize - 4)..]);
        if (headerSize == CurrentHeaderSize) length = BinaryPrimitives.ReadUInt32LittleEndian(data[6..]);
        if (length > MaxExifLength || length > data.Length - headerSize) return false;
        exif = data.Slice(headerSize, (int)length);
        payload = data[(headerSize + (int)length)..];
        version = data[4];
        if (version == 2 && compression == FicCompression.None && payload.Length != expandedLength) return false;
        return true;
    }
}
