# C implementation

`fic_encode` produces uncompressed version `00` FIC files with Fast-tier
literal strips. `fic_decode` accepts uncompressed containers (`00`, `01`,
and `02`) and every Fast and Compact strip codec. It validates dimensions,
strip lengths, padding, and pixel CRC-32C. `fic_decode_ex` accepts a caller
supplied decompressor for Deflate, GZip, Zstandard, and Brotli payloads;
`fic_decode` returns `FIC_UNSUPPORTED` for those payloads.

The API in `fic.h` operates on tightly packed, row-major RGB or RGBA bytes.
The encoded buffer belongs to the caller and must be freed with `free()`.
Call `fic_image_free()` after decoding. Decoded EXIF points into the input
buffer and remains valid only while that buffer is alive.

Build and run the C tests with CMake:

```sh
cmake -S c -B c/build
cmake --build c/build --config Release
ctest --test-dir c/build -C Release --output-on-failure
```

`fic_test verify image.fic image.raw` checks an encoded image against raw
pixels. `fic_test encode image.raw WIDTH HEIGHT CHANNELS image.fic` writes a
file suitable for the .NET and JavaScript decoders.
