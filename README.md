# Tedd.FIC

[![Build](https://github.com/tedd/Tedd.FIC/actions/workflows/dotnet.yml/badge.svg)](https://github.com/tedd/Tedd.FIC/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/Tedd.FIC.svg)](https://www.nuget.org/packages/Tedd.FIC)
[![NuGet downloads](https://img.shields.io/nuget/dt/Tedd.FIC.svg)](https://www.nuget.org/packages/Tedd.FIC)

Tedd.FIC is a .NET library and new lossless image format for tightly packed RGB and RGBA pixels. Its independent strips can encode and decode in parallel. The file container starts with `TFIC` and version byte `00`, and can preserve EXIF metadata.

On the synthetic 512×512 gradient used below, FIC Fast encoded to **292,045 bytes in 2.06 ms**, compared with **353,053 bytes in 71.8 ms** for SkiaSharp PNG. Results depend on image content, codec settings, hardware, and runtime; run the included benchmark on representative images before making a format choice.

## Install

```sh
dotnet add package Tedd.FIC
```

The current package targets .NET 11 preview.

## Use

```csharp
using Tedd.FIC;

// rgb contains width * height * 3 bytes in row-major RGB order.
byte[] file = Fic.Encode(rgb, width, height, channels: 3, effort: FicEffort.Fast);
File.WriteAllBytes("image.tfic", file);

byte[] pixels = Fic.Decode(File.ReadAllBytes("image.tfic"),
    out int decodedWidth, out int decodedHeight, out int channels);
```

RGBA uses `channels: 4`. `FicEffort.Default` trades encoding time for size; `FicEffort.Max` tries an additional byte mode. The decoder accepts all effort levels. `threads` controls strip parallelism; encoded bytes are independent of the thread count.

EXIF is passed as a raw TIFF EXIF payload and returned unchanged. Callers obtain this payload from an image metadata parser; FIC does not interpret EXIF tags.

```csharp
byte[] file = Fic.Encode(rgba, width, height, channels: 4, exif: exifTiffBytes);
if (Fic.TryGetExif(file, out ReadOnlySpan<byte> exif))
    Console.WriteLine($"EXIF bytes: {exif.Length}");
```

For untrusted files, use `Fic.TryGetInfo` to inspect dimensions before allocation, or `Fic.Decode` with a suitable `maxPixels` limit. `Fic.TryDecode` writes into a caller-owned buffer and verifies the pixel checksum by default.

## Container version 00

| Offset | Field |
| ---: | --- |
| 0–3 | ASCII `TFIC` |
| 4 | Container version `00` |
| 5–8 | EXIF payload length, unsigned little-endian 32-bit integer |
| 9… | Raw EXIF TIFF payload, at most 16 MiB |
| next… | Lossless image payload with strip table and pixel CRC-32C |

EXIF is optional (`length = 0`). The current decoder rejects unknown container versions and malformed lengths. The pixel checksum covers decoded pixels, not EXIF bytes.

## Benchmarks

Measured on Windows 10.0.26200, .NET 11.0 preview, 32 logical processors, 512×512 RGBA synthetic images. Each cell is the median of five timed runs after one warmup. Encoding includes allocation of the output buffer; decoding includes materializing pixels. The benchmark checks lossless round trips. JPEG at quality 90 is lossy, so its sizes are **not** directly comparable as equivalent output quality.

| Image | Codec | Bytes | Encode ms | Decode ms |
| --- | --- | ---: | ---: | ---: |
| Gradient | FIC Fast | 292,045 | 2.058 | 3.239 |
| Gradient | FIC Default | 235,802 | 4.441 | 4.753 |
| Gradient | ImageSharp PNG | 391,293 | 98.285 | 11.279 |
| Gradient | SkiaSharp PNG | 353,053 | 71.804 | 4.664 |
| Gradient | ImageSharp JPEG q90 | 63,462 | 9.115 | 7.044 |
| Gradient | SkiaSharp JPEG q90 | 58,310 | 5.032 | 4.020 |
| Graphics | FIC Fast | 1,977 | 1.978 | 0.153 |
| Graphics | FIC Default | 1,633 | 1.821 | 0.105 |
| Graphics | ImageSharp PNG | 2,368 | 5.509 | 1.026 |
| Graphics | SkiaSharp PNG | 2,426 | 10.336 | 2.111 |
| Graphics | ImageSharp JPEG q90 | 7,352 | 4.914 | 6.621 |
| Graphics | SkiaSharp JPEG q90 | 3,686 | 3.192 | 2.726 |
| Noise | FIC Fast | 835,271 | 2.919 | 3.362 |
| Noise | FIC Default | 934,915 | 14.806 | 6.774 |
| Noise | ImageSharp PNG | 900,102 | 52.093 | 5.068 |
| Noise | SkiaSharp PNG | 918,060 | 49.049 | 8.156 |
| Noise | ImageSharp JPEG q90 | 235,387 | 5.369 | 5.322 |
| Noise | SkiaSharp JPEG q90 | 216,215 | 11.513 | 7.515 |

The benchmark uses ImageSharp 3.1.12 and SkiaSharp 4.151.2. Run it with:

```sh
dotnet run --project src/Tedd.FIC.Benchmark/Tedd.FIC.Benchmark.csproj -c Release -- 7
```

These generated samples are useful for regression checks, not a substitute for a representative photo and graphics corpus. ImageSharp has separate licensing terms; consult its package license before adopting it in an application.

## Build

```sh
dotnet test src/Tedd.FIC.Tests/Tedd.FIC.Tests.csproj -c Release
dotnet pack src/Tedd.FIC/Tedd.FIC.csproj -c Release
```

License: MIT.
