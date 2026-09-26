using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace Tedd.FIC.Benchmark;

internal static class CorpusBenchmark
{
    private static readonly string[] Folders = ["Google Photos", "test2017", "train2017", "unlabeled2017", "val2017"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"];

    private sealed record Codec(string Name, bool Lossless, Func<byte[], int, int, byte[]> Encode, Func<byte[], byte[]> Decode);
    private sealed class Totals
    {
        public long Images, Pixels, SourceBytes, EncodedBytes;
        public double EncodeMs, DecodeMs;
    }

    public static void Run(string[] args)
    {
        if (args.Length < 2 || args.Length > 5)
            throw new ArgumentException("Usage: --corpus ROOT [images-per-folder=1000] [seed=20260926] [workers=8]");
        string root = Path.GetFullPath(args[1]);
        int count = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1000;
        int seed = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 20260926;
        int workers = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 8;
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (workers < 1 || workers > 32) throw new ArgumentOutOfRangeException(nameof(workers));

        // Select every file before loading or timing an image. Preserve the manifest locally for audit and replay.
        Random random = new(seed);
        List<(string Folder, string Path)> selection = [];
        List<string> excluded = [];
        foreach (string folder in Folders)
        {
            string directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            string[] candidates = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (candidates.Length < count) throw new InvalidOperationException($"{folder}: only {candidates.Length} supported images");
            random.Shuffle(candidates);
            int selected = 0;
            foreach (string path in candidates)
            {
                try
                {
                    using Image<Rgba32> image = Image.Load<Rgba32>(path);
                    if ((long)image.Width * image.Height * 4 > Array.MaxLength)
                        throw new InvalidDataException("Decoded image exceeds the supported array size.");
                    selection.Add((folder, path));
                    selected++;
                    if (selected == count) break;
                }
                catch (Exception error) when (error is InvalidImageContentException or NotSupportedException or InvalidDataException or OutOfMemoryException)
                {
                    excluded.Add($"{folder}\t{Path.GetRelativePath(root, path)}\t{error.GetType().Name}");
                }
            }
            if (selected != count) throw new InvalidOperationException($"{folder}: only {selected} decodable images found");
            Console.WriteLine($"Selected {selected} decodable images of {candidates.Length} from {folder}");
        }

        string output = Path.GetFullPath(Path.Combine("benchmark-output", $"corpus-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{seed}"));
        Directory.CreateDirectory(output);
        string manifestPath = Path.Combine(output, "selection.tsv");
        File.WriteAllLines(manifestPath, selection.Select(s => $"{s.Folder}\t{Path.GetRelativePath(root, s.Path)}"), Encoding.UTF8);
        File.WriteAllLines(Path.Combine(output, "excluded.tsv"), excluded, Encoding.UTF8);
        string manifestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(output, "run.txt"),
            $"root={root}{Environment.NewLine}seed={seed}{Environment.NewLine}count-per-folder={count}{Environment.NewLine}workers={workers}{Environment.NewLine}manifest-sha256={manifestHash}{Environment.NewLine}runtime={Environment.Version}{Environment.NewLine}os={RuntimeInformation.OSDescription}{Environment.NewLine}logical-processors={Environment.ProcessorCount}{Environment.NewLine}");
        Console.WriteLine($"Manifest: {manifestPath}");
        Console.WriteLine($"Manifest SHA-256: {manifestHash}");

        Codec[] codecs =
        [
            new("FIC Fast", true, (p, w, h) => Fic.Encode(p, w, h, 4, FicEffort.Fast), b => Fic.Decode(b, out _, out _, out _)),
            new("FIC Default", true, (p, w, h) => Fic.Encode(p, w, h, 4), b => Fic.Decode(b, out _, out _, out _)),
            new("ImageSharp PNG", true, (p, w, h) => ImageSharpEncode(p, w, h, false), ImageSharpDecode),
            new("SkiaSharp PNG", true, (p, w, h) => SkiaEncode(p, w, h, SKEncodedImageFormat.Png), SkiaDecode),
            new("ImageSharp JPEG q90", false, (p, w, h) => ImageSharpEncode(p, w, h, true), ImageSharpDecode),
            new("SkiaSharp JPEG q90", false, (p, w, h) => SkiaEncode(p, w, h, SKEncodedImageFormat.Jpeg), SkiaDecode),
        ];
        Dictionary<(string Folder, string Codec), Totals> totals = [];
        using StreamWriter csv = new(Path.Combine(output, "results.csv"), false, Encoding.UTF8);
        csv.WriteLine("folder,image_index,codec,width,height,source_bytes,encoded_bytes,encode_ms,decode_ms");

        // Warm the codec paths outside all measurements.
        byte[] warmPixels = new byte[16 * 16 * 4];
        foreach (Codec codec in codecs) codec.Decode(codec.Encode(warmPixels, 16, 16));

        object outputLock = new();
        int completed = 0;
        int[] workOrder = Enumerable.Range(0, count)
            .SelectMany(imageIndex => Enumerable.Range(0, Folders.Length).Select(folderIndex => folderIndex * count + imageIndex))
            .ToArray();
        Parallel.For(0, workOrder.Length, new ParallelOptions { MaxDegreeOfParallelism = workers }, position =>
        {
            int i = workOrder[position];
            var item = selection[i];
            byte[] pixels;
            int width, height;
            using (Image<Rgba32> image = Image.Load<Rgba32>(item.Path))
            {
                width = image.Width;
                height = image.Height;
                pixels = new byte[checked(width * height * 4)];
                image.CopyPixelDataTo(pixels);
            }
            long sourceBytes = new FileInfo(item.Path).Length;
            foreach (Codec codec in codecs)
            {
                long start = Stopwatch.GetTimestamp();
                byte[] encoded = codec.Encode(pixels, width, height);
                double encodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                start = Stopwatch.GetTimestamp();
                byte[] decoded = codec.Decode(encoded);
                double decodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (decoded.Length != pixels.Length || (codec.Lossless && !decoded.AsSpan().SequenceEqual(pixels)))
                    throw new InvalidDataException($"Round trip failed: {item.Path}, {codec.Name}");
                lock (outputLock)
                {
                    Totals total = totals.GetValueOrDefault((item.Folder, codec.Name)) ?? (totals[(item.Folder, codec.Name)] = new Totals());
                    total.Images++;
                    total.Pixels += (long)width * height;
                    total.SourceBytes += sourceBytes;
                    total.EncodedBytes += encoded.Length;
                    total.EncodeMs += encodeMs;
                    total.DecodeMs += decodeMs;
                    csv.WriteLine(string.Join(',', item.Folder, i % count, codec.Name, width, height, sourceBytes,
                        encoded.Length, encodeMs.ToString("F4", CultureInfo.InvariantCulture), decodeMs.ToString("F4", CultureInfo.InvariantCulture)));
                }
            }
            int done = Interlocked.Increment(ref completed);
            if (done % 25 == 0 || done == selection.Count)
            {
                lock (outputLock)
                {
                    csv.Flush();
                    Console.WriteLine($"Completed {done}/{selection.Count}: {item.Folder}");
                }
            }
        });

        string summaryPath = Path.Combine(output, "summary.csv");
        using (StreamWriter summary = new(summaryPath, false, Encoding.UTF8))
        {
            summary.WriteLine("folder,codec,images,megapixels,source_bytes,encoded_bytes,encode_seconds,decode_seconds");
            foreach (string folder in Folders)
            foreach (Codec codec in codecs)
            {
                Totals t = totals[(folder, codec.Name)];
                summary.WriteLine(string.Join(',', folder, codec.Name, t.Images,
                    (t.Pixels / 1e6).ToString("F3", CultureInfo.InvariantCulture), t.SourceBytes, t.EncodedBytes,
                    (t.EncodeMs / 1000).ToString("F3", CultureInfo.InvariantCulture),
                    (t.DecodeMs / 1000).ToString("F3", CultureInfo.InvariantCulture)));
            }
        }
        Console.WriteLine($"Summary: {summaryPath}");
    }

    private static byte[] ImageSharpEncode(byte[] pixels, int width, int height, bool jpeg)
    {
        using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(pixels, width, height);
        using MemoryStream stream = new();
        if (jpeg) image.Save(stream, new JpegEncoder { Quality = 90 });
        else image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static byte[] ImageSharpDecode(byte[] encoded)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(encoded);
        byte[] pixels = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    private static byte[] SkiaEncode(byte[] pixels, int width, int height, SKEncodedImageFormat format)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] SkiaDecode(byte[] encoded)
    {
        using SKBitmap bitmap = SKBitmap.Decode(encoded) ?? throw new InvalidDataException("Skia decode failed");
        using SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888);
        byte[] pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        Marshal.Copy(rgba.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }
}
