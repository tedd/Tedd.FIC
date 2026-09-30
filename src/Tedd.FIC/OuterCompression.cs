using System.Buffers;
using System.IO.Compression;
using System.Buffers.Binary;
#if NET10_0
using ZstdSharp;
#endif

namespace Tedd.FIC;

/// <summary>Span-based outer payload compression. All output is written into caller-owned storage.</summary>
internal static class OuterCompression
{
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, FicCompression codec, int zstdLevel, out int written)
    {
        switch (codec)
        {
            case FicCompression.Deflate:
#if NET10_0
                return TryCompressStream(source, destination, gzip: false, out written);
#else
                return DeflateEncoder.TryCompress(source, destination, out written, 6);
#endif
            case FicCompression.Gzip:
#if NET10_0
                return TryCompressStream(source, destination, gzip: true, out written);
#else
                return GZipEncoder.TryCompress(source, destination, out written, 6);
#endif
            case FicCompression.Zstd:
#if NET10_0
                try { using var compressor = new Compressor(zstdLevel); written = compressor.Wrap(source, destination); return true; }
                catch (ZstdException) { written = 0; return false; }
#else
                return ZstandardEncoder.TryCompress(source, destination, out written, zstdLevel, 0);
#endif
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
#if NET10_0
                return TryDecompressStream(source, destination, gzip: false, out written);
#else
                return DeflateDecoder.TryDecompress(source, destination, out written);
#endif
            case FicCompression.Gzip:
#if NET10_0
                return TryDecompressStream(source, destination, gzip: true, out written);
#else
                return GZipDecoder.TryDecompress(source, destination, out written);
#endif
            case FicCompression.Zstd:
#if NET10_0
                try { using var decompressor = new Decompressor(); written = decompressor.Unwrap(source, destination); return true; }
                catch (ZstdException) { written = 0; return false; }
#else
                return ZstandardDecoder.TryDecompress(source, destination, out written);
#endif
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

    public static bool TryGetZstdDecompressedLength(ReadOnlySpan<byte> source, out long length)
    {
#if NET10_0
        try { length = checked((long)Decompressor.GetDecompressedSize(source)); return true; }
        catch (Exception ex) when (ex is ZstdException or OverflowException) { length = 0; return false; }
#else
        return ZstandardDecoder.TryGetMaxDecompressedLength(source, out length);
#endif
    }

#if NET10_0
    private static bool TryCompressStream(ReadOnlySpan<byte> source, Span<byte> destination, bool gzip, out int written)
    {
        using var output = new MemoryStream();
        using (Stream compressor = gzip ? new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)
                                        : new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(source);
        if (output.Length > destination.Length) { written = 0; return false; }
        written = (int)output.Length;
        output.GetBuffer().AsSpan(0, written).CopyTo(destination);
        return true;
    }

    private static bool TryDecompressStream(ReadOnlySpan<byte> source, Span<byte> destination, bool gzip, out int written)
    {
        using var input = new SingleByteReadStream(source.ToArray());
        using Stream decompressor = gzip ? new GZipStream(input, CompressionMode.Decompress)
                                         : new DeflateStream(input, CompressionMode.Decompress);
        written = 0;
        while (written < destination.Length)
        {
            int count = decompressor.Read(destination[written..]);
            if (count == 0) break;
            written += count;
        }
        if (decompressor.ReadByte() != -1 || input.Position != input.Length) return false;
        if (gzip)
        {
            if (source.Length < 18 || BinaryPrimitives.ReadUInt32LittleEndian(source[^4..]) != (uint)written) return false;
            uint crc = 0xFFFFFFFF;
            foreach (byte value in destination[..written])
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(source[^8..]) != ~crc) return false;
        }
        return true;
    }

    // DeflateStream reads ahead; limiting the source read preserves the exact frame boundary.
    private sealed class SingleByteReadStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }
#endif
}
