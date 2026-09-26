# TypeScript codec

`fic.ts` is the source for the official JavaScript codec. The public declarations are generated from its exported types and functions.

From the repository root:

```sh
npm ci --prefix ts
npm run build --prefix ts
```

The TypeScript compiler emits declarations and checks the source. The build then bundles the TypeScript codec and its compression libraries into `js/fic.js`, writes `js/fic.d.ts` and license notices, and copies the browser assets to `site/` for direct Pages previews. `fzstd.d.ts` describes the separately bundled Zstandard decoder.

`encode(pixels, width, height, channels, exif, compression)` accepts `None`, `Deflate`, `Gzip`, `Zstd`, `Brotli`, or `Auto`. The default and `Auto` use no outer compression because the JavaScript encoder writes Fast-tier strips. An explicit codec produces a version 02 container and falls back to stored FICQ when compression would enlarge the payload. Both `decode` and `decodeAsync` read every compression type.
