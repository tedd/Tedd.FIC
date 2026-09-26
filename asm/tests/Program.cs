using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Tedd.FIC;

unsafe
{
if (args.Length is < 1 or > 2) throw new ArgumentException("Expected native DLL path and optional fixture directory.");
if (args.Length == 2) Directory.CreateDirectory(args[1]);
using var library = new NativeLibraryHandle(args[0]);
var encode = Marshal.GetDelegateForFunctionPointer<Encode>(NativeLibrary.GetExport(library.Handle, "fic_encode"));
var decode = Marshal.GetDelegateForFunctionPointer<Decode>(NativeLibrary.GetExport(library.Handle, "fic_decode"));
Decompress decompress = Expand;
int checkedCases = 0;
var seen = new HashSet<int>();
foreach (int channels in new[] { 3, 4 })
foreach ((int width, int height) in new[] { (1, 1), (7, 3), (16, 32), (63, 65), (128, 64), (257, 36) })
foreach (int pattern in Enumerable.Range(0, 8))
{
    var pixels = new byte[width * height * channels];
    var random = new Random(width * 717 + height * 131 + channels * 7 + pattern);
    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
    for (int c = 0; c < channels; c++)
    {
        int i = (y * width + x) * channels + c;
        pixels[i] = pattern switch
        {
            0 => 0,
            1 => (byte)(c == 3 ? 255 : x + y),
            2 => (byte)(x * 5 + y * 7 + c * 17),
            3 => (byte)((x / 8 + y / 8 + c) % 2 * 255),
            4 => (byte)(random.Next(8) * 31),
            5 => (byte)random.Next(256),
            6 => (byte)(c == 3 ? y * 13 : x ^ y),
            _ => (byte)((x % 16 < 8 ? y : x) + c * 53),
        };
    }
    var encoded = new byte[checked(pixels.Length * 5 + 128)];
    unsafe
    {
        fixed (byte* p = pixels, d = encoded)
        {
            if (encode(p, (uint)width, (uint)height, (uint)channels, d, (uint)encoded.Length, out uint written) == 0)
                throw new Exception($"Native encode failed: {width}x{height} pattern={pattern}");
            var result = Fic.Decode(encoded.AsSpan(0, checked((int)written)), out int w, out int h, out int ch);
            if (w != width || h != height || ch != channels || !result.AsSpan().SequenceEqual(pixels))
                throw new Exception(".NET rejected native encode");
        }
    }
    foreach (FicEffort effort in Enum.GetValues<FicEffort>())
    foreach (FicCompression compression in Enum.GetValues<FicCompression>().Where(c => c != FicCompression.Auto))
    {
        byte[] file = Fic.Encode(pixels, width, height, channels, effort, compression: compression);
        if (args.Length == 2 && compression == FicCompression.None)
        {
            string stem = $"w{width}_h{height}_c{channels}_p{pattern}_e{effort}";
            File.WriteAllBytes(Path.Combine(args[1], stem + ".fic"), file);
            File.WriteAllBytes(Path.Combine(args[1], stem + ".raw"), pixels);
        }
        byte[] output = new byte[pixels.Length];
        unsafe
        {
            fixed (byte* f = file, o = output)
            {
                if (decode(f, (uint)file.Length, o, (uint)output.Length, out var info, decompress, null) == 0 ||
                    info.Width != width || info.Height != height || info.Channels != channels ||
                    !output.AsSpan().SequenceEqual(pixels))
                    throw new Exception($"Native decode failed: {width}x{height} ch={channels} pattern={pattern} effort={effort} compression={compression}");
            }
        }
        checkedCases++;
        if (file[4] == 0)
        {
            int q = 9 + BitConverter.ToInt32(file, 5);
            int tier = file[q + 5], p = q + 6;
            _ = ReadVar(file, ref p); // width
            int parsedHeight = ReadVar(file, ref p);
            int flags = file[p++];
            int rows = ReadVar(file, ref p);
            if ((flags & 4) != 0) p += 1 + (file[p] + 1) * channels + 10;
            for (int i = 0; i < (parsedHeight + rows - 1) / rows; i++)
                seen.Add(tier * 8 + (ReadVar(file, ref p) & 7));
        }
    }
}
{
    var pixels = new byte[128 * 128 * 4];
    var exif = new byte[] { 0x49, 0x49, 0x2a, 0, 8, 0, 0, 0 };
    byte[] v0 = Fic.Encode(pixels, 128, 128, 4, FicEffort.Fast, exif: exif, compression: FicCompression.None);
    Check(v0, pixels, exif);
    byte[] v1 = new byte[v0.Length + 1];
    v0.AsSpan(0, 4).CopyTo(v1);
    v1[4] = 1;
    v1[5] = 0;
    v0.AsSpan(5).CopyTo(v1.AsSpan(6));
    Check(v1, pixels, exif);
    byte[] v2 = new byte[v0.Length + 5];
    v0.AsSpan(0, 4).CopyTo(v2);
    v2[4] = 2;
    v2[5] = 0;
    v0.AsSpan(5, 4).CopyTo(v2.AsSpan(6));
    BitConverter.TryWriteBytes(v2.AsSpan(10, 4), v0.Length - 9 - exif.Length);
    v0.AsSpan(9).CopyTo(v2.AsSpan(14));
    Check(v2, pixels, exif);
    byte[] z2 = Fic.Encode(pixels, 128, 128, 4, FicEffort.Fast, exif: exif, compression: FicCompression.Zstd);
    if (z2[4] != 2 || z2[5] != 3) throw new Exception("Expected compressed Zstandard fixture");
    byte[] z1 = new byte[z2.Length - 4];
    z2.AsSpan(0, 4).CopyTo(z1);
    z1[4] = 1;
    z1[5] = 1;
    z2.AsSpan(6, 4).CopyTo(z1.AsSpan(6));
    z2.AsSpan(14).CopyTo(z1.AsSpan(10));
    Check(z1, pixels, exif);
    var fuzz = new Random(20260926);
    foreach (byte[] source in new[] { v0, v1, v2, z1, z2 })
    for (int attempt = 0; attempt < 40; attempt++)
    {
        byte[] changed = (byte[])source.Clone();
        int index = fuzz.Next(changed.Length);
        changed[index] ^= (byte)(1 << fuzz.Next(8));
        var result = new byte[pixels.Length];
        bool nativeAccepted;
        fixed (byte* f = changed, o = result)
            nativeAccepted = decode(f, (uint)changed.Length, o, (uint)result.Length,
                out _, decompress, null) != 0;
        if (nativeAccepted)
        {
            byte[] managed = Fic.Decode(changed, out _, out _, out _);
            if (!result.AsSpan().SequenceEqual(managed))
                throw new Exception("Native and managed decode disagree on a mutated frame");
        }
    }
    v0[^1] ^= 1;
    unsafe
    {
        var output = new byte[pixels.Length];
        fixed (byte* f = v0, o = output)
        {
            if (decode(f, (uint)v0.Length, o, (uint)output.Length, out _, decompress, null) != 0)
                throw new Exception("Corrupt CRC accepted");
            if (decode(f, (uint)(v0.Length - 1), o, (uint)output.Length, out _, decompress, null) != 0)
                throw new Exception("Truncated frame accepted");
            if (decode(f, (uint)v0.Length, o, (uint)(output.Length - 1), out _, decompress, null) != 0)
                throw new Exception("Undersized destination accepted");
        }
    }
}
if (!seen.SetEquals(new[] { 0, 1, 2, 3, 8, 9, 10, 11, 12 }))
    throw new Exception($"Strip slot coverage incomplete: {string.Join(',', seen.Order())}");
Console.WriteLine($"PASS: {checkedCases} .NET-to-native cases and native-to-.NET round trips. Slots: {string.Join(',', seen.Order())}");

void Check(byte[] file, byte[] pixels, byte[] exif)
{
    var output = new byte[pixels.Length];
    fixed (byte* f = file, o = output)
    {
        if (decode(f, (uint)file.Length, null, 0, out var header, decompress, null) == 0 ||
            header.Width != 128 || header.Height != 128 || header.Channels != 4 ||
            header.ExifLength != exif.Length)
            throw new Exception("Native info query failed");
        if (decode(f, (uint)file.Length, o, (uint)output.Length, out var info, decompress, null) == 0 ||
            info.Width != 128 || info.Height != 128 || info.Channels != 4 ||
            info.ExifLength != exif.Length || !new ReadOnlySpan<byte>((byte*)info.Exif, exif.Length).SequenceEqual(exif) ||
            !output.AsSpan().SequenceEqual(pixels))
            throw new Exception($"Version/EXIF interop failed: v{file[4]} codec={(file[4] != 0 ? file[5] : 0)}");
    }
}

int ReadVar(byte[] bytes, ref int offset)
{
    int value = 0, shift = 0;
    while (true)
    {
        int b = bytes[offset++];
        value |= (b & 127) << shift;
        if (b < 128) return value;
        shift += 7;
    }
}

unsafe int Expand(uint codec, byte* source, uint sourceLength, byte* destination,
    uint destinationLength, uint* written, void* context)
{
    if (destination == null)
    {
        if (codec != 3 || !ZstandardDecoder.TryGetMaxDecompressedLength(new ReadOnlySpan<byte>(source, (int)sourceLength), out long n) || n > uint.MaxValue)
            return 0;
        *written = (uint)n;
        return 1;
    }
    var input = new ReadOnlySpan<byte>(source, (int)sourceLength);
    var output = new Span<byte>(destination, (int)destinationLength);
    bool ok;
    int nWritten;
    switch (codec)
    {
        case 1: ok = DeflateDecoder.TryDecompress(input, output, out nWritten); break;
        case 2: ok = GZipDecoder.TryDecompress(input, output, out nWritten); break;
        case 3: ok = ZstandardDecoder.TryDecompress(input, output, out nWritten); break;
        case 4:
            using (var dec = new BrotliDecoder())
            {
                ok = dec.Decompress(input, output, out int consumed, out nWritten) == OperationStatus.Done && consumed == input.Length;
            }
            break;
        default: return 0;
    }
    *written = (uint)nWritten;
    return ok ? 1 : 0;
}
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
unsafe delegate int Encode(byte* pixels, uint width, uint height, uint channels,
    byte* destination, uint capacity, out uint written);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
unsafe delegate int Decode(byte* source, uint length, byte* destination, uint capacity,
    out FicInfo info, Decompress decompress, void* context);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
unsafe delegate int Decompress(uint codec, byte* source, uint sourceLength,
    byte* destination, uint destinationLength, uint* written, void* context);
[StructLayout(LayoutKind.Sequential)]
struct FicInfo
{
    public uint Width, Height, Channels, Tier;
    public IntPtr Exif;
    public uint ExifLength;
}
sealed class NativeLibraryHandle : IDisposable
{
    public IntPtr Handle { get; }
    public NativeLibraryHandle(string path) => Handle = NativeLibrary.Load(Path.GetFullPath(path));
    public void Dispose() => NativeLibrary.Free(Handle);
}
