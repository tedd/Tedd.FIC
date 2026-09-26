using System.Buffers;
using System.IO.Compression;

namespace Tedd.FIC;

/// <summary>Span-based outer payload compression. All output is written into caller-owned storage.</summary>
internal static class OuterCompression
{
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, FicCompression codec, int zstdLevel, out int written)
    {
        switch (codec)
        {
            case FicCompression.Deflate:
                return DeflateEncoder.TryCompress(source, destination, out written, 6);
            case FicCompression.Gzip:
                return GZipEncoder.TryCompress(source, destination, out written, 6);
            case FicCompression.Zstd:
                return ZstandardEncoder.TryCompress(source, destination, out written, zstdLevel, 0);
            case FicCompression.Brotli:
                return BrotliEncoder.TryCompress(source, destination, out written, quality: 5, window: 22);
            default:
                throw new ArgumentOutOfRangeException(nameof(codec));
        }
    }

    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, FicCompression codec, out int written)
    {
        switch (codec)
        {
            case FicCompression.Deflate:
                return DeflateDecoder.TryDecompress(source, destination, out written);
            case FicCompression.Gzip:
                return GZipDecoder.TryDecompress(source, destination, out written);
            case FicCompression.Zstd:
                return ZstandardDecoder.TryDecompress(source, destination, out written);
            case FicCompression.Brotli:
                using (var decoder = new BrotliDecoder())
                {
                    OperationStatus status = decoder.Decompress(source, destination, out int consumed, out written);
                    return status == OperationStatus.Done && consumed == source.Length;
                }
            default:
                written = 0;
                return false;
        }
    }
}
