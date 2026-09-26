using Tedd.FIC;

if (args.Length != 2) throw new ArgumentException("usage: generate|verify directory");
Directory.CreateDirectory(args[1]);
if (args[0] == "generate")
{
    const int w = 128, h = 96;
    foreach (int ch in new[] { 3, 4 })
    foreach (string pattern in new[] { "flat", "gradient", "noise", "checker", "photo" })
    {
        var pixels = new byte[w * h * ch];
        var random = new Random(1234);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int p = (y * w + x) * ch;
            int v = pattern switch
            {
                "flat" => 0,
                "gradient" => (x + y) & 255,
                "checker" => ((x / 8 + y / 8) & 1) * 255,
                "photo" => ((x * 7 + y * 3 + random.Next(11)) & 255),
                _ => random.Next(256)
            };
            pixels[p] = (byte)v;
            pixels[p + 1] = (byte)(pattern == "noise" ? random.Next(256) : (v + 47) & 255);
            pixels[p + 2] = (byte)(pattern == "noise" ? random.Next(256) : (v + 83) & 255);
            if (ch == 4) pixels[p + 3] = (byte)(pattern == "noise" ? random.Next(256) : pattern == "checker" ? v : 255);
        }
        string basename = $"{pattern}-{w}-{h}-{ch}";
        File.WriteAllBytes(Path.Combine(args[1], basename + ".raw"), pixels);
        foreach (FicEffort effort in Enum.GetValues<FicEffort>())
            File.WriteAllBytes(Path.Combine(args[1], basename + $"-{effort}.tfic"), Fic.Encode(pixels, w, h, ch, effort));
    }
}
else if (args[0] == "verify")
{
    foreach (string path in Directory.GetFiles(args[1], "*.js.tfic"))
    {
        var decoded = Fic.Decode(File.ReadAllBytes(path), out int w, out int h, out int ch);
        string raw = path[..^".js.tfic".Length] + ".raw";
        string[] name = Path.GetFileNameWithoutExtension(raw).Split('-');
        if (w != int.Parse(name[^3]) || h != int.Parse(name[^2]) || ch != int.Parse(name[^1]) ||
            !decoded.SequenceEqual(File.ReadAllBytes(raw)))
            throw new Exception($"JS stream failed .NET interoperability: {path}");
    }
}
else throw new ArgumentException("unknown mode");
