import { deflateSync, gzipSync, gunzipSync, inflateSync } from 'fflate';
import { brotliDecode, brotliEncode } from 'brotli-lib';
import { compress as compressZstd, init as initZstd } from '@bokuweb/zstd-wasm';
import zstdWasm from './zstd.wasm';

await (initZstd as (bytes: Uint8Array) => Promise<void>)(zstdWasm);

export type CompressionName = 'None' | 'Deflate' | 'Gzip' | 'Zstd' | 'Brotli' | 'Auto';
export type CompressionCode = 0 | 1 | 2 | 3 | 4;

const names: Record<Exclude<CompressionName, 'Auto'>, CompressionCode> = {
  None: 0,
  Deflate: 1,
  Gzip: 2,
  Zstd: 3,
  Brotli: 4,
};

export function compressionCode(name: CompressionName): CompressionCode {
  if (name === 'Auto') return 0; // The JavaScript encoder writes Fast-tier strips.
  if (!Object.hasOwn(names, name)) throw new RangeError('Invalid FIC compression type');
  return names[name];
}

export function compressPayload(input: Uint8Array, code: CompressionCode): Uint8Array {
  switch (code) {
    case 0: return input;
    case 1: return deflateSync(input, { level: 6 });
    case 2: return gzipSync(input, { level: 6 });
    case 3: return compressZstd(input, 2);
    case 4: return brotliEncode(input, { quality: 5 });
  }
}

export function decompressPayload(input: Uint8Array, code: 1 | 2 | 4, expected: number): Uint8Array {
  // fflate writes into caller-owned storage. The extra byte detects output
  // that exceeds the length declared in the FIC container.
  const out = new Uint8Array(expected + 1);
  switch (code) {
    case 1: return inflateSync(input, { out });
    case 2: return gunzipSync(input, { out });
    case 4: return brotliDecode(input, { maxOutputSize: expected + 1 });
  }
}
