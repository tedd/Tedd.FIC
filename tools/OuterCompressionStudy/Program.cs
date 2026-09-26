using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Tedd.FIC;

// Source decoding, file I/O, and destination allocation are excluded from span API timings.
if (args.Length is not (4 or 5) || (args.Length == 5 && args[4] != "--photos"))
    throw new ArgumentException("Usage: OuterCompressionStudy selection.tsv image-root graphics-root output.csv [--photos]");
string selection = args[0], sourceRoot = args[1], graphicsRoot = args[2], output = args[3];
var photos = File.ReadLines(selection).Select(line => line.Split('\t'))
    .Where(parts => parts.Length == 2).GroupBy(parts => parts[0])
    .SelectMany(group => group.Take(group.Key == "Google Photos" ? 6 : 8)
        .Select(parts => (Group: group.Key, Path: Path.Combine(sourceRoot, parts[1])))).ToList();
var graphicPaths = Directory.EnumerateFiles(graphicsRoot, "*.png", SearchOption.AllDirectories)
    .Where(path => !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase))
    .Order(StringComparer.Ordinal).ToArray();
new Random(20260926).Shuffle(graphicPaths);
var graphics = new List<(string Group, string Path)>();
var seenGraphics = new HashSet<string>();
foreach (string path in graphicPaths)
{
    try
    {
        var info = Image.Identify(path);
        if (info == null || info.Width < 128 || info.Height < 128) continue;
        using Image<Rgba32> image = Image.Load<Rgba32>(path);
        byte[] pixels = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(pixels);
        if (!seenGraphics.Add(Convert.ToHexString(SHA256.HashData(pixels)))) continue;
        graphics.Add(("graphics", path));
        if (graphics.Count == 20) break;
    }
    catch (Exception error) when (error is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or IOException) { }
}
var samples = args.Length == 5 ? photos : photos.Concat(graphics).ToList();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
using var csv = new StreamWriter(output);
csv.WriteLine("group,image,effort,compression,width,height,bytes,encode_ms,decode_ms,encode_alloc_bytes");
for (int i = 0; i < samples.Count; i++)
{
    var sample = samples[i];
    using Image<Rgba32> image = Image.Load<Rgba32>(sample.Path);
    byte[] pixels = new byte[checked(image.Width * image.Height * 4)];
    image.CopyPixelDataTo(pixels);
    byte[] encoded = new byte[checked(pixels.Length * 2 + 65536)];
    byte[] decoded = new byte[pixels.Length];
    foreach (FicEffort effort in Enum.GetValues<FicEffort>())
    foreach (var (name, codec, level) in new[] { ("None", FicCompression.None, 0), ("Deflate", FicCompression.Deflate, 0),
        ("Gzip", FicCompression.Gzip, 0), ("Zstd", FicCompression.Zstd, 0),
        ("Zstd3", FicCompression.Zstd, 3), ("Brotli", FicCompression.Brotli, 0) })
    {
        if (!Fic.TryEncode(pixels, image.Width, image.Height, 4, encoded, out int size, effort, compression: codec, zstdLevel: level))
            throw new InvalidDataException($"Benchmark destination too small: {sample.Path}");
        if (!Fic.TryDecode(encoded.AsSpan(0, size), decoded, out _) || !decoded.AsSpan().SequenceEqual(pixels))
            throw new InvalidDataException($"Round trip failed: {sample.Path}, {effort}, {codec}");
        var encodeTimes = new double[3];
        var decodeTimes = new double[3];
        var allocs = new long[3];
        for (int trial = 0; trial < 3; trial++)
        {
            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            if (!Fic.TryEncode(pixels, image.Width, image.Height, 4, encoded, out int measuredSize, effort,
                compression: codec, zstdLevel: level)
                || measuredSize != size) throw new InvalidDataException("Inconsistent encode");
            encodeTimes[trial] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocs[trial] = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
            start = Stopwatch.GetTimestamp();
            if (!Fic.TryDecode(encoded.AsSpan(0, size), decoded, out _)) throw new InvalidDataException("Inconsistent decode");
            decodeTimes[trial] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(encodeTimes);
        Array.Sort(decodeTimes);
        Array.Sort(allocs);
        csv.WriteLine(string.Join(',', sample.Group, i.ToString("D3"), effort, name, image.Width, image.Height, size,
            encodeTimes[1].ToString("F3", CultureInfo.InvariantCulture), decodeTimes[1].ToString("F3", CultureInfo.InvariantCulture), allocs[1]));
        csv.Flush();
    }
    Console.WriteLine($"{i + 1}/{samples.Count} {sample.Group} {image.Width}x{image.Height}");
}
