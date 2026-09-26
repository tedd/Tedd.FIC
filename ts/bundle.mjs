import { build } from 'esbuild';
import { copyFileSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = new URL('../', import.meta.url);
const wasm = new URL('zstd.wasm', import.meta.url);
copyFileSync(new URL('node_modules/@bokuweb/zstd-wasm/dist/web/zstd.wasm', import.meta.url), wasm);
try {
  await build({
    entryPoints: [fileURLToPath(new URL('fic.ts', import.meta.url))],
    outfile: fileURLToPath(new URL('js/fic.js', root)),
    bundle: true,
    platform: 'browser',
    format: 'esm',
    target: 'es2022',
    external: ['./fzstd.js'],
    loader: { '.wasm': 'binary' },
    legalComments: 'inline',
  });
} finally {
  rmSync(wasm);
}
copyFileSync(new URL('.generated/fic.d.ts', import.meta.url), new URL('js/fic.d.ts', root));

const notices = [
  ['fflate', 'LICENSE'],
  ['brotli-lib', 'LICENSE'],
  ['brotli-lib', 'LICENSE_THIRD_PARTY'],
].map(([name, file]) =>
  `${name} / ${file}\n${readFileSync(new URL(`node_modules/${name}/${file}`, import.meta.url), 'utf8').trim()}\n`);
notices.push(readFileSync(new URL('zstd.LICENSES.txt', import.meta.url), 'utf8').trim() + '\n');
writeFileSync(new URL('js/fic.LICENSES.txt', root), notices.join('\n'), 'utf8');
