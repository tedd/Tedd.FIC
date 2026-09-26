# TypeScript codec

`fic.ts` is the source for the official JavaScript codec. The public declarations are generated from its exported types and functions.

From the repository root:

```sh
npm ci --prefix ts
npm run build --prefix ts
```

The build writes `js/fic.js` and `js/fic.d.ts`, then copies the JavaScript module to `site/fic.js` for direct Pages previews. `fzstd.d.ts` describes the separately bundled Zstandard decoder; it is not emitted as JavaScript.
