# Tedd.FIC

[![Build](https://github.com/tedd/Tedd.FIC/actions/workflows/dotnet.yml/badge.svg)](https://github.com/tedd/Tedd.FIC/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/Tedd.FIC.svg)](https://www.nuget.org/packages/Tedd.FIC)
[![NuGet downloads](https://img.shields.io/nuget/dt/Tedd.FIC.svg)](https://www.nuget.org/packages/Tedd.FIC)

Tedd.FIC is a lossless image format for tightly packed RGB and RGBA pixels, with a .NET library and a JavaScript module. Its independent strips can encode and decode in parallel in .NET. The `.fic` file container starts with `FIC` followed by a NUL byte, supports optional Deflate, GZip, Zstandard, or Brotli payload compression, and can preserve EXIF metadata.

Across the 5,000-image corpus below, FIC Fast produced **24.6% fewer bytes** than SkiaSharp PNG and spent **53× less aggregate time encoding** the same decoded pixels. Results depend on image content, codec settings, hardware, and runtime; run the included benchmark on representative images before making a format choice.

## Install

```sh
dotnet add package Tedd.FIC
```

The package targets .NET 10 and .NET 11.

## Use

```csharp
using Tedd.FIC;

// rgb contains width * height * 3 bytes in row-major RGB order.
byte[] file = Fic.Encode(rgb, width, height, channels: 3, effort: FicEffort.Fast);
File.WriteAllBytes("image.fic", file);

byte[] pixels = Fic.Decode(File.ReadAllBytes("image.fic"),
    out int decodedWidth, out int decodedHeight, out int channels);
```

RGBA uses `channels: 4`. `FicEffort.Default` trades encoding time for size; `FicEffort.Max` tries an additional byte mode. The decoder accepts all effort levels. `threads` controls strip parallelism; encoded bytes are independent of the thread count.

The default `FicCompression.Auto` uses no outer compression for `FicEffort.Fast`, Zstandard quality 2 for `FicEffort.Default`, and Brotli quality 5 for `FicEffort.Max`. Explicit `FicCompression.Zstd` at Fast effort uses quality 1. Choose `None`, `Deflate`, `Gzip`, `Zstd`, or `Brotli` with the `compression` argument. The encoder stores the raw FICQ payload when compression would increase its size. Uncompressed output uses version `00`; compressed requests use version `02`.

`FicEffort.Max` encodes with two strip partitions and retains the smaller complete file, including outer compression when requested. This adds encoding time and temporary memory but cannot increase output size relative to its original 64-row pass.

Pass `zstdLevel: 3` with `compression: FicCompression.Zstd` to favor size over encoding speed. The default level is determined by effort; this override does not change the format byte.

```csharp
byte[] file = Fic.Encode(rgb, width, height, channels: 3,
    effort: FicEffort.Fast, compression: FicCompression.Zstd, zstdLevel: 3);

// Caller-owned output buffer; the outer compressor writes to a Span<byte>.
byte[] buffer = new byte[checked((int)Fic.GetMaxEncodedLength(width, height, 3))];
if (!Fic.TryEncode(rgb, width, height, 3, buffer, out int written,
    effort: FicEffort.Fast, compression: FicCompression.Zstd, zstdLevel: 3))
    throw new InvalidOperationException("Buffer too small");
```

EXIF is passed as a raw TIFF EXIF payload and returned unchanged. Callers obtain this payload from an image metadata parser; FIC does not interpret EXIF tags.

```csharp
byte[] file = Fic.Encode(rgba, width, height, channels: 4, exif: exifTiffBytes);
if (Fic.TryGetExif(file, out ReadOnlySpan<byte> exif))
    Console.WriteLine($"EXIF bytes: {exif.Length}");
```

For untrusted files, use `Fic.TryGetInfo` to inspect dimensions before pixel allocation, or `Fic.Decode` with a suitable `maxPixels` limit. Compressed payloads are limited to 256 MiB after expansion by default; the `maxCompressedPayloadBytes` argument can change that limit. `Fic.TryDecode` writes into a caller-owned buffer and verifies the pixel checksum by default.

### JavaScript

The [JavaScript module](js/fic.js) works in browsers and Node.js. `encode` and `decode` support uncompressed, Deflate, GZip, Zstandard, and Brotli payloads without platform compression APIs. `decodeAsync` remains available for existing callers. The module accepts all current Fast and Compact strip codecs and verifies the pixel CRC-32C. Its encoder writes Fast-tier literal strips, so its output may be larger than the optimized .NET encoder's output. A requested outer codec is stored only when it reduces the payload size.

The [TypeScript source](ts/fic.ts) generates the official JavaScript module and its [type declarations](js/fic.d.ts). Run `npm ci --prefix ts` and `npm run build --prefix ts` from the repository root.

```js
import { encode, decode, decodeAsync, getInfo } from './js/fic.js';

const file = encode(rgba, width, height, 4, new Uint8Array(), 'Zstd'); // Uint8Array of RGBA pixels
const info = getInfo(file);                   // dimensions before decoding
const { pixels, width: w, height: h, channels } = decode(file);
const response = await fetch('from-dotnet.fic');
const decodedFromDotNet = await decodeAsync(new Uint8Array(await response.arrayBuffer()));
```

The [browser converter](http://tedd.no/Tedd.FIC/#convert) accepts JPEG, PNG, WebP, and FIC files. It reports actual file-size ratios, displays the converted image, and provides a download. Browser-produced WebP and JPEG may be lossy.

## Container

| Offset | Field |
| ---: | --- |
| 0–3 | ASCII `FIC` followed by NUL (`00`) |
| 4 | Container version: `00`, `01`, or `02` |
| 5 | Versions `01`/`02`: compression type |
| 5–8 (`00`), 6–9 (`01`/`02`) | EXIF payload length, unsigned little-endian 32-bit integer |
| 10–13 (`02`) | Expanded FICQ payload length, unsigned little-endian 32-bit integer |
| 9… (`00`), 10… (`01`), 14… (`02`) | Raw EXIF TIFF payload, at most 16 MiB |
| next… | Lossless image payload with strip table and pixel CRC-32C |

EXIF is optional (`length = 0`). Version `00` stores FICQ uncompressed. The decoder also accepts version `01` with `0` = none and `1` = Zstandard under the FIC signature. Version `02` assigns `0` = none, `1` = raw Deflate, `2` = GZip, `3` = Zstandard, `4` = Brotli. Compression covers only FICQ; EXIF bytes precede it. The expanded FICQ length is checked against the configurable 256 MiB default limit before allocation. The decoder rejects unknown versions, compression types, and malformed lengths. The pixel checksum covers decoded pixels, not EXIF bytes.

## Benchmarks

### Outer compression on real images

The span-based `Fic.TryEncode` and `Fic.TryDecode` APIs were measured on 58 losslessly decoded RGBA images: six large camera photos, 32 COCO images, and 20 distinct graphics. Every effort was tested with no outer compression and all four codecs. Deflate and GZip used quality 6, explicit Zstandard used quality 1 at Fast and quality 2 at Default/Max, and Brotli used quality 5. A separate run tested Zstandard quality 3 on the photos. Source decoding, file I/O, and caller-owned output buffer allocation were excluded. Each image/setting had one warmup and three timed runs; times below sum per-image medians. Every output was decoded and compared byte for byte with its input. Measurements used an AMD Ryzen 9 5950X, Windows, and .NET 11 RC1.

| FIC effort | Outer codec | Output MB | Saved vs none | Encode ms | Decode ms |
| --- | --- | ---: | ---: | ---: | ---: |
| Fast | None | 59.821 | — | 255 | 146 |
| Fast | Deflate | 54.258 | 9.30% | 2,012 | 300 |
| Fast | GZip | 54.259 | 9.30% | 2,013 | 306 |
| Fast | Zstandard q1 | 54.755 | 8.47% | 324 | 196 |
| Fast | Brotli q5 | 53.857 | 9.97% | 2,186 | 462 |
| Default | None | 45.638 | — | 331 | 230 |
| Default | Deflate | 43.981 | 3.63% | 1,666 | 355 |
| Default | GZip | 43.982 | 3.63% | 1,670 | 359 |
| Default | Zstandard q2 | 44.493 | 2.51% | 388 | 248 |
| Default | Brotli q5 | 43.712 | 4.22% | 996 | 449 |
| Max | None | 45.635 | — | 339 | 224 |
| Max | Deflate | 43.978 | 3.63% | 1,692 | 355 |
| Max | GZip | 43.979 | 3.63% | 1,691 | 359 |
| Max | Zstandard q2 | 44.492 | 2.50% | 411 | 248 |
| Max | Brotli q5 | 43.709 | 4.22% | 1,028 | 449 |

On the six large photos with Fast FICQ, Zstandard q1 saved 9.67% in 237 ms, Zstandard q3 saved **10.34% in 469 ms**, Deflate q6 saved 10.78% in 1,482 ms, and Brotli q5 saved 11.60% in 1,961 ms. Zstandard q3 was the fastest measured option to exceed 10% on that photo subset. The 10% threshold was not reached by any tested codec on the full 58-image set. `FicEffort.Fast` defaults to no outer compression.

The outer compressors use .NET 11 span APIs and write into caller-owned buffers. The whole encoder is not allocation-free: the FICQ strip encoders still allocate internal scratch. The local CSV retains per-image sizes, timings, and measured thread allocations. The selection manifest remains local because it contains private file names. Re-run with `tools/OuterCompressionStudy` and a corpus selection manifest.

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

Measured on AMD Ryzen 9 5950X, Windows 10.0.26200, .NET 11 RC1, 512×512 RGBA synthetic images. Each cell is the median of three launches, each with 100 warmups and seven timed repetitions per operation. FIC used no outer compression. Encoding includes allocation of the output buffer; decoding includes materializing pixels. The benchmark checks lossless round trips. JPEG at quality 90 is lossy, so its sizes are **not** directly comparable as equivalent output quality.

| Image | Codec | Bytes | Encode ms | Decode ms |
| --- | --- | ---: | ---: | ---: |
| Gradient | FIC Fast | 292,045 | 1.920 | 0.680 |
| Gradient | FIC Default | 235,802 | 1.154 | 1.052 |
| Gradient | ImageSharp PNG | 391,293 | 112.318 | 5.762 |
| Gradient | SkiaSharp PNG | 353,053 | 88.311 | 6.395 |
| Gradient | ImageSharp JPEG q90 | 63,462 | 2.973 | 2.197 |
| Gradient | SkiaSharp JPEG q90 | 58,310 | 6.798 | 5.156 |
| Graphics | FIC Fast | 1,977 | 1.420 | 0.075 |
| Graphics | FIC Default | 1,633 | 2.735 | 0.079 |
| Graphics | ImageSharp PNG | 2,368 | 7.545 | 1.260 |
| Graphics | SkiaSharp PNG | 2,426 | 15.735 | 2.569 |
| Graphics | ImageSharp JPEG q90 | 7,352 | 1.454 | 1.006 |
| Graphics | SkiaSharp JPEG q90 | 3,686 | 4.496 | 2.953 |
| Noise | FIC Fast | 835,271 | 1.899 | 0.662 |
| Noise | FIC Default | 934,915 | 7.544 | 2.300 |
| Noise | ImageSharp PNG | 900,102 | 64.602 | 5.587 |
| Noise | SkiaSharp PNG | 918,060 | 50.227 | 8.072 |
| Noise | ImageSharp JPEG q90 | 235,387 | 5.566 | 4.350 |
| Noise | SkiaSharp JPEG q90 | 216,215 | 10.901 | 6.856 |

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
