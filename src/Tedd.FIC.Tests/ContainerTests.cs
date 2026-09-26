using System.Buffers.Binary;
using Xunit;

namespace Tedd.FIC.Tests;

public class ContainerTests
{
    [Theory]
    [InlineData(3, FicEffort.Fast)]
    [InlineData(4, FicEffort.Default)]
    [InlineData(4, FicEffort.Max)]
    public void RoundTripsPixelsAndExif(int channels, FicEffort effort)
    {
        const int width = 97, height = 41;
        byte[] pixels = new byte[width * height * channels];
        new Random(123).NextBytes(pixels);
        byte[] exif = [0x49, 0x49, 0x2a, 0x00, 0x08, 0, 0, 0];
        byte[] encoded = Fic.Encode(pixels, width, height, channels, effort, exif: exif, compression: FicCompression.None);
        Assert.Equal("FIC\0"u8.ToArray(), encoded[..4]);
        Assert.Equal(0, encoded[4]);
        Assert.Equal((uint)exif.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(5)));
        Assert.True(Fic.TryGetInfo(encoded, out var info));
        Assert.Equal((width, height, channels), (info.Width, info.Height, info.Channels));
        Assert.True(Fic.TryGetExif(encoded, out var readExif));
        Assert.Equal(exif, readExif.ToArray());
        Assert.Equal(pixels, Fic.Decode(encoded, out _, out _, out _));
        byte[] buffer = new byte[encoded.Length];
        Assert.True(Fic.TryEncode(pixels, width, height, channels, buffer, out int written, effort, exif: exif, compression: FicCompression.None));
        Assert.Equal(encoded, buffer[..written]);
    }

    [Fact]
    public void RejectsInvalidContainerHeaders()
    {
        byte[] pixels = new byte[3 * 16 * 16];
        byte[] valid = Fic.Encode(pixels, 16, 16, 3, compression: FicCompression.None);
        foreach (Action<byte[]> corrupt in new Action<byte[]>[]
        {
            d => d[0] = 0,
            d => d[0] = 84,
            d => d[4] = 2,
            d => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(5), uint.MaxValue),
        })
        {
            byte[] copy = (byte[])valid.Clone();
            corrupt(copy);
            Assert.False(Fic.TryGetInfo(copy, out _));
            Assert.False(Fic.TryDecode(copy, pixels, out _));
        }
    }

    [Theory]
    [InlineData(FicEffort.Default, FicCompression.Auto)]
    [InlineData(FicEffort.Max, FicCompression.Auto)]
    [InlineData(FicEffort.Default, FicCompression.Deflate)]
    [InlineData(FicEffort.Default, FicCompression.Gzip)]
    [InlineData(FicEffort.Default, FicCompression.Zstd)]
    [InlineData(FicEffort.Default, FicCompression.Brotli)]
    public void CompressedContainerRoundTripsAndPreservesExif(FicEffort effort, FicCompression compression)
    {
        const int width = 512, height = 512;
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)((i / 4) % width);
            pixels[i + 1] = (byte)((i / 4) / width);
            pixels[i + 2] = 90;
            pixels[i + 3] = 255;
        }
        byte[] exif = [0x49, 0x49, 0x2a, 0, 8, 0, 0, 0];
        byte[] encoded = Fic.Encode(pixels, width, height, 4, effort, exif: exif, compression: compression);
        Assert.Equal((byte)2, encoded[4]);
        Assert.Equal((byte)(compression == FicCompression.Auto ? effort == FicEffort.Max ? FicCompression.Brotli : FicCompression.Zstd : compression), encoded[5]);
        Assert.Equal((uint)exif.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(6)));
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(10)) > 0);
        Assert.True(Fic.TryGetInfo(encoded, out var info));
        Assert.Equal((width, height, 4), (info.Width, info.Height, info.Channels));
        Assert.True(Fic.TryGetExif(encoded, out var readExif));
        Assert.Equal(exif, readExif.ToArray());
        byte[] decoded = new byte[pixels.Length];
        Assert.True(Fic.TryDecode(encoded, decoded, out _));
        Assert.Equal(pixels, decoded);
        Assert.Equal(pixels, Fic.Decode(encoded, out _, out _, out _));
        Assert.True(encoded.Length <= Fic.GetMaxEncodedLength(width, height, 4, exif.Length, compression));
        byte[] buffer = new byte[encoded.Length];
        Assert.True(Fic.TryEncode(pixels, width, height, 4, buffer, out int written, effort, exif: exif, compression: compression));
        Assert.Equal(encoded, buffer[..written]);
        Assert.False(Fic.TryEncode(pixels, width, height, 4, buffer.AsSpan(0, encoded.Length - 1), out written,
            effort, exif: exif, compression: compression));
        Assert.Equal(0, written);
    }

    [Fact]
    public void FastAutoWritesVersionZero()
    {
        byte[] pixels = new byte[256 * 256 * 4];
        byte[] automatic = Fic.Encode(pixels, 256, 256, 4, FicEffort.Fast);
        byte[] explicitNone = Fic.Encode(pixels, 256, 256, 4, FicEffort.Fast, compression: FicCompression.None);
        Assert.Equal((byte)0, automatic[4]);
        Assert.Equal(explicitNone, automatic);
    }

    [Fact]
    public void ExplicitZstandardLevelThreeRoundTrips()
    {
        byte[] pixels = new byte[256 * 256 * 4];
        byte[] encoded = Fic.Encode(pixels, 256, 256, 4, FicEffort.Fast,
            compression: FicCompression.Zstd, zstdLevel: 3);
        Assert.Equal((byte)3, encoded[5]);
        Assert.Equal(pixels, Fic.Decode(encoded, out _, out _, out _));
        byte[] buffer = new byte[encoded.Length];
        Assert.True(Fic.TryEncode(pixels, 256, 256, 4, buffer, out int written, FicEffort.Fast,
            compression: FicCompression.Zstd, zstdLevel: 3));
        Assert.Equal(encoded, buffer[..written]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Fic.Encode(pixels, 256, 256, 4,
            compression: FicCompression.Brotli, zstdLevel: 3));
    }

    [Fact]
    public void ZstdRequestFallsBackToUncompressedPayloadWhenLarger()
    {
        byte[] pixels = [12, 34, 56];
        byte[] legacy = Fic.Encode(pixels, 1, 1, 3, FicEffort.Fast, compression: FicCompression.None);
        byte[] encoded = Fic.Encode(pixels, 1, 1, 3, FicEffort.Fast, compression: FicCompression.Zstd);
        Assert.Equal((byte)0, legacy[4]);
        Assert.Equal((byte)2, encoded[4]);
        Assert.Equal((byte)0, encoded[5]);
        Assert.Equal(legacy.AsSpan(9).ToArray(), encoded.AsSpan(14).ToArray());
        Assert.Equal(pixels, Fic.Decode(encoded, out _, out _, out _));
    }

    [Fact]
    public void RejectsCorruptOrOversizedZstdPayload()
    {
        byte[] pixels = new byte[512 * 512 * 4];
        byte[] encoded = Fic.Encode(pixels, 512, 512, 4, FicEffort.Fast, compression: FicCompression.Zstd);
        Assert.Equal((byte)3, encoded[5]);
        Assert.False(Fic.TryGetInfo(encoded, out _, maxCompressedPayloadBytes: 16));
        Assert.False(Fic.TryDecode(encoded, pixels, out _, maxCompressedPayloadBytes: 16));
        Assert.Throws<InvalidDataException>(() => Fic.Decode(encoded, out _, out _, out _, maxCompressedPayloadBytes: 16));
        Assert.False(Fic.TryGetInfo([.. encoded, 0], out _));
        foreach (Action<byte[]> corrupt in new Action<byte[]>[]
        {
            data => data[5] = 5,
            data => data[14] = 0,
        })
        {
            byte[] copy = (byte[])encoded.Clone();
            corrupt(copy);
            Assert.False(Fic.TryGetInfo(copy, out _));
            Assert.False(Fic.TryDecode(copy, pixels, out _));
            Assert.False(Fic.TryGetExif(copy, out _));
        }
    }

    [Fact]
    public void ReadsVersionOneZstandardFiles()
    {
        byte[] pixels = new byte[128 * 128 * 4];
        byte[] current = Fic.Encode(pixels, 128, 128, 4, FicEffort.Fast, compression: FicCompression.Zstd);
        Assert.Equal((byte)3, current[5]);
        byte[] older = new byte[current.Length - 4];
        "FIC\0"u8.CopyTo(older);
        older[4] = 1;
        older[5] = 1;
        current.AsSpan(6, 4).CopyTo(older.AsSpan(6));
        current.AsSpan(14).CopyTo(older.AsSpan(10));
        Assert.Equal(pixels, Fic.Decode(older, out _, out _, out _));
    }

    [Theory]
    [InlineData(FicCompression.Deflate)]
    [InlineData(FicCompression.Gzip)]
    [InlineData(FicCompression.Zstd)]
    [InlineData(FicCompression.Brotli)]
    public void RejectsWrongExpandedLength(FicCompression codec)
    {
        byte[] pixels = new byte[128 * 128 * 4];
        byte[] encoded = Fic.Encode(pixels, 128, 128, 4, FicEffort.Fast, compression: codec);
        Assert.Equal((byte)codec, encoded[5]);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(10), 1);
        Assert.False(Fic.TryGetInfo(encoded, out _));
    }

    [Theory]
    [InlineData(FicCompression.Deflate)]
    [InlineData(FicCompression.Gzip)]
    [InlineData(FicCompression.Zstd)]
    [InlineData(FicCompression.Brotli)]
    public void RejectsTrailingCompressedBytes(FicCompression codec)
    {
        byte[] encoded = Fic.Encode(new byte[128 * 128 * 4], 128, 128, 4, FicEffort.Fast, compression: codec);
        Assert.Equal((byte)codec, encoded[5]);
        Assert.False(Fic.TryGetInfo([.. encoded, 0], out _));
    }
}
