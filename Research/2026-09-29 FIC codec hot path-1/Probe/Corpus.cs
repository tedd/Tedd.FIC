using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Tedd.FIC;

internal static class Corpus
{
    private static readonly string[] Folders = ["Google Photos", "test2017", "train2017", "unlabeled2017", "val2017"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"];

    public static void Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Usage: corpus|profile-corpus ROOT COUNT_PER_FOLDER");
        bool profile = args[0] == "profile-corpus";
        using Process process = Process.GetCurrentProcess();
        process.ProcessorAffinity = (nint)(1 << 4);
        process.PriorityClass = ProcessPriorityClass.AboveNormal;
        string root = Path.GetFullPath(args[1]);
        int count = int.Parse(args[2], CultureInfo.InvariantCulture);
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        Random random = new(20260926);
        List<(string Folder, string Path)> selected = [];
        foreach (string folder in Folders)
        {
            string directory = Path.Combine(root, folder);
            string[] candidates = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            random.Shuffle(candidates);
            int accepted = 0;
            foreach (string path in candidates)
            {
                try
                {
                    using Image<Rgba32> image = Image.Load<Rgba32>(path);
                    if ((long)image.Width * image.Height * 4 > Array.MaxLength) continue;
                    selected.Add((folder, path));
                    if (++accepted == count) break;
                }
                catch (Exception error) when (error is InvalidImageContentException or NotSupportedException or InvalidDataException or OutOfMemoryException) { }
            }
            if (accepted != count) throw new InvalidDataException($"Only {accepted} images selected from {folder}.");
        }
        byte[] manifest = Encoding.UTF8.GetBytes(string.Join('\n', selected.Select(x => x.Folder + '\t' + Path.GetRelativePath(root, x.Path))));
        if (profile)
        {
            using Image<Rgba32> target = Image.Load<Rgba32>(selected[1].Path);
            byte[] input = new byte[checked(target.Width * target.Height * 4)];
            target.CopyPixelDataTo(input);
            for (int i = 0; i < 20; i++) GC.KeepAlive(Fic.Encode(input, target.Width, target.Height, 4, FicEffort.Default, compression: FicCompression.None));
            long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15L;
            while (Stopwatch.GetTimestamp() < end)
                GC.KeepAlive(Fic.Encode(input, target.Width, target.Height, 4, FicEffort.Default, compression: FicCompression.None));
            return;
        }
        Console.WriteLine($"manifest_sha256={Convert.ToHexString(SHA256.HashData(manifest))}; count={selected.Count}; seed=20260926; runtime={Environment.Version}; affinity=cpu4; priority=above-normal");
        Console.WriteLine("folder,index,width,height,encoded_bytes,encode_ms,min_ms,max_ms,gc0,gc1,gc2");
        int index = 0;
        foreach (var item in selected)
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(item.Path);
            byte[] pixels = new byte[checked(image.Width * image.Height * 4)];
            image.CopyPixelDataTo(pixels);
            for (int i = 0; i < 20; i++) GC.KeepAlive(Fic.Encode(pixels, image.Width, image.Height, 4, FicEffort.Default, compression: FicCompression.None));
            double[] times = new double[7];
            int size = 0;
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            for (int i = 0; i < times.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                byte[] encoded = Fic.Encode(pixels, image.Width, image.Height, 4, FicEffort.Default, compression: FicCompression.None);
                times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                size = encoded.Length;
                if (!Fic.Decode(encoded, out _, out _, out _).AsSpan().SequenceEqual(pixels)) throw new InvalidDataException("Round trip failed.");
            }
            Array.Sort(times);
            Console.WriteLine(string.Join(',', item.Folder, index++ % count, image.Width, image.Height, size,
                times[3].ToString("F6", CultureInfo.InvariantCulture), times[0].ToString("F6", CultureInfo.InvariantCulture),
                times[^1].ToString("F6", CultureInfo.InvariantCulture), GC.CollectionCount(0) - gc0,
                GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2));
        }
    }
}
