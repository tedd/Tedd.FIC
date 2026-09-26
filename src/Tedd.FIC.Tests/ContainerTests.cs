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
        byte[] encoded = Fic.Encode(pixels, width, height, channels, effort, exif: exif);
        Assert.Equal("TFIC"u8.ToArray(), encoded[..4]);
        Assert.Equal(0, encoded[4]);
        Assert.Equal((uint)exif.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(5)));
        Assert.True(Fic.TryGetInfo(encoded, out var info));
        Assert.Equal((width, height, channels), (info.Width, info.Height, info.Channels));
        Assert.True(Fic.TryGetExif(encoded, out var readExif));
        Assert.Equal(exif, readExif.ToArray());
        Assert.Equal(pixels, Fic.Decode(encoded, out _, out _, out _));
        byte[] buffer = new byte[encoded.Length];
        Assert.True(Fic.TryEncode(pixels, width, height, channels, buffer, out int written, effort, exif: exif));
        Assert.Equal(encoded, buffer[..written]);
    }

    [Fact]
    public void RejectsInvalidContainerHeaders()
    {
        byte[] pixels = new byte[3 * 16 * 16];
        byte[] valid = Fic.Encode(pixels, 16, 16, 3);
        foreach (Action<byte[]> corrupt in new Action<byte[]>[]
        {
            d => d[0] = 0,
            d => d[4] = 1,
            d => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(5), uint.MaxValue),
        })
        {
            byte[] copy = (byte[])valid.Clone();
            corrupt(copy);
            Assert.False(Fic.TryGetInfo(copy, out _));
            Assert.False(Fic.TryDecode(copy, pixels, out _));
        }
    }
}
