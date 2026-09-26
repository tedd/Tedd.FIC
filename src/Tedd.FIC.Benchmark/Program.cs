using System.Diagnostics;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SkiaSharp;
using Tedd.FIC;
using Tedd.FIC.Benchmark;

if (args.Length > 0 && args[0] == "--corpus")
{
    CorpusBenchmark.Run(args);
    return;
}

const int width = 512, height = 512;
int iterations = args.Length > 0 ? int.Parse(args[0]) : 7;
Console.WriteLine($"Runtime: {Environment.Version}; OS: {RuntimeInformation.OSDescription}; CPU: {Environment.ProcessorCount} logical processors; {iterations} timed repetitions; {width}x{height} RGBA");
Console.WriteLine("Image,Codec,Bytes,EncodeMs,DecodeMs");

foreach (string pattern in new[] { "gradient", "graphics", "noise" })
{
    byte[] pixels = Generate(pattern);
    foreach (var codec in new (string Name, Func<byte[], byte[]> Encode, Func<byte[], byte[]> Decode)[]
    {
        ("FIC Fast", p => Fic.Encode(p, width, height, 4, FicEffort.Fast), b => Fic.Decode(b, out _, out _, out _)),
        ("FIC Default", p => Fic.Encode(p, width, height, 4), b => Fic.Decode(b, out _, out _, out _)),
        ("ImageSharp PNG", p => ImageSharpEncode(p, false), b => ImageSharpDecode(b)),
        ("SkiaSharp PNG", p => SkiaEncode(p, SKEncodedImageFormat.Png), b => SkiaDecode(b)),
        ("ImageSharp JPEG q90", p => ImageSharpEncode(p, true), b => ImageSharpDecode(b)),
        ("SkiaSharp JPEG q90", p => SkiaEncode(p, SKEncodedImageFormat.Jpeg), b => SkiaDecode(b)),
    })
    {
        byte[] encoded = codec.Encode(pixels);
        byte[] decoded = codec.Decode(encoded);
        if (decoded.Length != pixels.Length) throw new InvalidDataException($"{codec.Name}: decoded size mismatch");
        if (!codec.Name.Contains("JPEG") && !decoded.AsSpan().SequenceEqual(pixels))
            throw new InvalidDataException($"{codec.Name}: lossless round trip failed");
        double encodeMs = Median(() => codec.Encode(pixels));
        double decodeMs = Median(() => codec.Decode(encoded));
        Console.WriteLine($"{pattern},{codec.Name},{encoded.Length},{encodeMs:F3},{decodeMs:F3}");
    }
}

double Median(Action action)
{
    action();
    double[] measurements = new double[iterations];
    for (int i = 0; i < iterations; i++)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        measurements[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    Array.Sort(measurements);
    return measurements[measurements.Length / 2];
}

byte[] Generate(string pattern)
{
    byte[] pixels = new byte[width * height * 4];
    Random random = new(12345);
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        int i = (y * width + x) * 4;
        int jitter = pattern == "gradient" ? random.Next(-8, 9) : 0;
        pixels[i] = pattern switch { "graphics" => (byte)(((x / 64 + y / 64) % 3) * 95), "noise" => (byte)random.Next(256), _ => (byte)Math.Clamp(x / 2 + jitter, 0, 255) };
        pixels[i + 1] = pattern switch { "graphics" => (byte)((x / 32 % 2) * 185), "noise" => (byte)random.Next(256), _ => (byte)Math.Clamp(y / 2 + jitter, 0, 255) };
        pixels[i + 2] = pattern switch { "graphics" => (byte)((y / 32 % 2) * 220), "noise" => (byte)random.Next(256), _ => (byte)Math.Clamp((x + y) / 4 + jitter, 0, 255) };
        pixels[i + 3] = 255;
    }
    return pixels;
}

byte[] ImageSharpEncode(byte[] pixels, bool jpeg)
{
    using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(pixels, width, height);
    using MemoryStream stream = new();
    if (jpeg) image.Save(stream, new JpegEncoder { Quality = 90 });
    else image.Save(stream, new PngEncoder());
    return stream.ToArray();
}

byte[] ImageSharpDecode(byte[] encoded)
{
    using Image<Rgba32> image = Image.Load<Rgba32>(encoded);
    byte[] pixels = new byte[image.Width * image.Height * 4];
    image.CopyPixelDataTo(pixels);
    return pixels;
}

byte[] SkiaEncode(byte[] pixels, SKEncodedImageFormat format)
{
    using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData data = image.Encode(format, 90);
    return data.ToArray();
}

byte[] SkiaDecode(byte[] encoded)
{
    using SKBitmap bitmap = SKBitmap.Decode(encoded) ?? throw new InvalidDataException("Skia decode failed");
    using SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888);
    byte[] pixels = new byte[bitmap.Width * bitmap.Height * 4];
    Marshal.Copy(rgba.GetPixels(), pixels, 0, pixels.Length);
    return pixels;
}
