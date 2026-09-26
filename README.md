# Tedd.FIC

[![Build](https://github.com/tedd/Tedd.FIC/actions/workflows/dotnet.yml/badge.svg)](https://github.com/tedd/Tedd.FIC/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/Tedd.FIC.svg)](https://www.nuget.org/packages/Tedd.FIC)
[![NuGet downloads](https://img.shields.io/nuget/dt/Tedd.FIC.svg)](https://www.nuget.org/packages/Tedd.FIC)

Tedd.FIC is a lossless image format for tightly packed RGB and RGBA pixels, with a .NET library and a JavaScript module. Its independent strips can encode and decode in parallel in .NET. The file container starts with `TFIC` and version byte `00`, and can preserve EXIF metadata.

Across the 5,000-image corpus below, FIC Fast produced **24.6% fewer bytes** than SkiaSharp PNG and spent **53× less aggregate time encoding** the same decoded pixels. Results depend on image content, codec settings, hardware, and runtime; run the included benchmark on representative images before making a format choice.

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

### JavaScript

The dependency-free [JavaScript module](site/tfic.js) works in browsers and Node.js. It accepts all current Fast and Compact strip codecs and verifies the pixel CRC-32C. Its encoder writes Fast-tier literal strips, so its output may be larger than the optimized .NET encoder's output.

```js
import { encode, decode, getInfo } from './tfic.js';

const file = encode(rgba, width, height, 4); // Uint8Array of RGBA pixels
const info = getInfo(file);                   // dimensions before decoding
const { pixels, width: w, height: h, channels } = decode(file);
```

The [browser converter](http://tedd.no/Tedd.FIC/#convert) accepts JPEG, PNG, WebP, and TFIC files. It reports actual file-size ratios, displays the converted image, and provides a download. Browser-produced WebP and JPEG may be lossy.

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

### Real-image corpus

The corpus benchmark selects 1,000 decodable images without replacement from each of `Google Photos`, `test2017`, `train2017`, `unlabeled2017`, and `val2017` under `I:\Images`. It shuffles each folder with seed `20260926`, writes the complete selection before timing, and uses that same list for all six codecs. Two unreadable JPEG candidates were excluded while selecting the 5,000-image sample. The manifest stays local because it contains file names. Its SHA-256 is `dd7f86a141af583b7c0f28f40cfd5d057d6fb32ec77574cc3543be32b06c8531`.

Images retain their original dimensions. Source JPEG decoding is outside the timed sections. Eight independent images run concurrently; reported encode and decode seconds are sums of per-image codec operation times under that load, **not wall-clock duration**. Speed in MP/s is total megapixels divided by that sum. All codecs receive the same decoded RGBA pixels without source metadata. FIC and PNG outputs are verified byte for byte against the decoded input. JPEG outputs use quality 90 and are lossy, so their sizes are not equivalent-quality comparisons. Results below were measured on an AMD Ryzen 9 5950X (16 cores, 32 logical processors), Windows 10.0.26200, and .NET 11 preview with ImageSharp 3.1.12 and SkiaSharp 4.151.2.

Across all 5,000 images (11,880.902 megapixels):

| Codec | Output GB | Encode s | Decode s |
| --- | ---: | ---: | ---: |
| FIC Fast | 10.607 | 62.101 | 30.976 |
| FIC Default | 7.904 | 66.572 | 63.411 |
| ImageSharp PNG | 13.772 | 4,007.196 | 164.190 |
| SkiaSharp PNG | 14.070 | 3,312.809 | 244.089 |
| ImageSharp JPEG q90 (lossy) | 2.526 | 109.804 | 81.188 |
| SkiaSharp JPEG q90 (lossy) | 2.469 | 246.971 | 181.548 |

Lossless output size by folder, in decimal MB (1,000 images per row):

| Folder | FIC Fast | FIC Default | ImageSharp PNG | SkiaSharp PNG |
| --- | ---: | ---: | ---: | ---: |
| Google Photos | 8,843.9 | 6,425.5 | 11,568.4 | 11,915.1 |
| test2017 | 439.8 | 369.0 | 550.3 | 538.5 |
| train2017 | 439.4 | 368.9 | 549.7 | 537.0 |
| unlabeled2017 | 449.3 | 377.5 | 561.1 | 548.0 |
| val2017 | 434.6 | 362.7 | 542.9 | 530.9 |

Summed per-image operation time by folder (1,000 images per row):

| Folder | FIC Fast encode s | SkiaSharp PNG encode s | FIC Fast decode s | SkiaSharp PNG decode s |
| --- | ---: | ---: | ---: | ---: |
| Google Photos | 53.582 | 3,035.826 | 28.163 | 214.977 |
| test2017 | 2.132 | 68.950 | 0.694 | 7.220 |
| train2017 | 2.097 | 69.046 | 0.693 | 7.283 |
| unlabeled2017 | 2.197 | 69.535 | 0.727 | 7.394 |
| val2017 | 2.093 | 69.452 | 0.699 | 7.215 |

FIC Default produced 43.8% fewer bytes than SkiaSharp PNG overall, with 49.8× less summed encode time. The folder selector on the [project site](https://tedd.no/Tedd.FIC/) shows lossless sizes for FIC and PNG, plus encode and decode speeds for all six codecs.

The original JPEG files total 3.700 GB. Their smaller size reflects lossy compression; the FIC and PNG figures are lossless encodings of the decoded pixels.

```sh
dotnet run --project src/Tedd.FIC.Benchmark/Tedd.FIC.Benchmark.csproj -c Release -- --corpus I:\Images 1000 20260926 8
```

### Synthetic reference

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
