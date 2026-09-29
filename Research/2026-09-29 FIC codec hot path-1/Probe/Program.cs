using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Tedd.FIC;

if (args.Length > 0 && args[0] is "corpus" or "profile-corpus")
{
    Corpus.Run(args);
    return;
}

const int width = 512, height = 512;
string mode = args.Length == 0 ? "measure" : args[0];
if (mode is not ("measure" or "profile" or "profile-noise" or "hash")) throw new ArgumentException("Expected measure, profile, profile-noise, or hash.");
Console.WriteLine($"runtime={Environment.Version}; processors={Environment.ProcessorCount}; mode={mode}");
Console.WriteLine("pattern,effort,operation,run,ms,bytes,allocated_bytes");
foreach (string pattern in new[] { "gradient", "graphics", "noise" })
{
    byte[] pixels = Generate(pattern);
    foreach (FicEffort effort in new[] { FicEffort.Fast, FicEffort.Default })
    {
        byte[] encoded = Fic.Encode(pixels, width, height, 4, effort, compression: FicCompression.None);
        if (!Fic.Decode(encoded, out _, out _, out _).AsSpan().SequenceEqual(pixels))
            throw new InvalidDataException($"Round trip failed: {pattern}, {effort}");
        if (mode == "hash")
        {
            Console.WriteLine($"{pattern},{effort},{encoded.Length},{Convert.ToHexString(SHA256.HashData(encoded))}");
            continue;
        }
        if (mode is "profile" or "profile-noise")
        {
            if (pattern != (mode == "profile" ? "gradient" : "noise") || effort != FicEffort.Default) continue;
            for (int i = 0; i < 100; i++) GC.KeepAlive(Fic.Encode(pixels, width, height, 4, effort, compression: FicCompression.None));
            long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15L;
            while (Stopwatch.GetTimestamp() < end)
            {
                byte[] data = Fic.Encode(pixels, width, height, 4, effort, compression: FicCompression.None);
                GC.KeepAlive(data);
            }
            break;
        }
        for (int i = 0; i < 100; i++)
        {
            GC.KeepAlive(Fic.Encode(pixels, width, height, 4, effort, compression: FicCompression.None));
            GC.KeepAlive(Fic.Decode(encoded, out _, out _, out _));
        }
        for (int run = 0; run < 9; run++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            byte[] output = [];
            for (int batch = 0; batch < 10; batch++) output = Fic.Encode(pixels, width, height, 4, effort, compression: FicCompression.None);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 10;
            Console.WriteLine(string.Join(',', pattern, effort, "encode", run, ms.ToString("F6", CultureInfo.InvariantCulture), output.Length, (GC.GetAllocatedBytesForCurrentThread() - allocated) / 10));
            allocated = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
            byte[] decoded = [];
            for (int batch = 0; batch < 10; batch++) decoded = Fic.Decode(encoded, out _, out _, out _);
            ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 10;
            if (!decoded.AsSpan().SequenceEqual(pixels)) throw new InvalidDataException("Decoded pixels changed.");
            Console.WriteLine(string.Join(',', pattern, effort, "decode", run, ms.ToString("F6", CultureInfo.InvariantCulture), decoded.Length, (GC.GetAllocatedBytesForCurrentThread() - allocated) / 10));
        }
    }
}

static byte[] Generate(string pattern)
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
