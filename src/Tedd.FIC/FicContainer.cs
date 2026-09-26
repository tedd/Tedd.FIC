using System.Buffers.Binary;

namespace Tedd.FIC;

/// <summary>Reads and writes the version 00 TFIC prefix before the FICQ image payload.</summary>
/// <remarks>The prefix contains the magic, version byte, little-endian EXIF length, and uninterpreted EXIF bytes.</remarks>
internal static class FicContainer
{
    public const int HeaderSize = 9;
    public const int MaxExifLength = 16 * 1024 * 1024;

    /// <summary>Rejects EXIF payloads that exceed the container's 16 MiB limit.</summary>
    public static void CheckExif(ReadOnlySpan<byte> exif)
    {
        if (exif.Length > MaxExifLength) throw new ArgumentOutOfRangeException(nameof(exif));
    }

    /// <summary>Writes the fixed prefix and EXIF bytes; the caller writes the image payload after them.</summary>
    public static void Write(Span<byte> destination, ReadOnlySpan<byte> exif)
    {
        "TFIC"u8.CopyTo(destination);
        destination[4] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[5..], (uint)exif.Length);
        exif.CopyTo(destination[HeaderSize..]);
    }

    /// <summary>Splits a valid TFIC prefix into spans of its image payload and raw EXIF bytes.</summary>
    /// <remarks>Checks the prefix and EXIF length only; the caller must validate the image payload separately.</remarks>
    public static bool TryRead(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> exif)
    {
        payload = exif = default;
        if (data.Length < HeaderSize || !data[..4].SequenceEqual("TFIC"u8) || data[4] != 0) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[5..]);
        if (length > MaxExifLength || length > data.Length - HeaderSize) return false;
        exif = data.Slice(HeaderSize, (int)length);
        payload = data[(HeaderSize + (int)length)..];
        return true;
    }
}
