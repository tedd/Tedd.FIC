import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { decode, encode, getInfo } from '../js/tfic.js';

const directory = mkdtempSync(join(tmpdir(), 'tfic-js-'));
const project = new URL('./Interop/Interop.csproj', import.meta.url).pathname.replace(/^\/(?=[A-Za-z]:)/, '');
const crcTable = Uint32Array.from({ length: 256 }, (_, i) => {
  let c = i;
  for (let k = 0; k < 8; k++) c = (c >>> 1) ^ ((c & 1) ? 0x82f63b78 : 0);
  return c >>> 0;
});
function crc32c(d, seed = 0) {
  let c = (~seed) >>> 0;
  for (const b of d) c = crcTable[(c ^ b) & 255] ^ (c >>> 8);
  return (~c) >>> 0;
}
function fixture(slot, palette) {
  const pixels = new Uint8Array(64 * 64 * 3);
  const header = [70, 73, 67, 81, 2, 1, 64, 64, palette ? 4 : 0, 64];
  if (palette) header.push(0, 0, 0, 0, ...new Array(10).fill(1));
  const payload = new Uint8Array(12);
  for (let i = 0; i < 12; i += 3) {
    payload[i] = palette ? 2 : 205; // PAL or GM long run.
    payload[i + 1] = palette ? 254 : 250; // 1024 pixels, after short count.
    payload[i + 2] = 7;
  }
  const q = Uint8Array.from([...header, payload.length * 8 + slot, ...payload, 0, 0, 0, 0]);
  const crc = crc32c(pixels, crc32c(Uint8Array.from(header)));
  q.set([crc & 255, crc >>> 8 & 255, crc >>> 16 & 255, crc >>> 24], q.length - 4);
  return { pixels, file: Uint8Array.from([84, 70, 73, 67, 0, 0, 0, 0, 0, ...q]) };
}
try {
  execFileSync('dotnet', ['run', '--project', project, '--', 'generate', directory], { stdio: 'inherit' });
  let decoded = 0;
  const slots = new Map();
  for (const name of readdirSync(directory).filter(n => n.endsWith('.tfic'))) {
    const source = readFileSync(join(directory, name));
    let at = 15; // TFIC header (9), FICQ magic/version/tier (6).
    const readVarint = () => { let value = 0, shift = 0, b; do { b = source[at++]; value |= (b & 127) << shift; shift += 7; } while (b & 128); return value; };
    readVarint(); readVarint(); // width, height
    const flags = source[at++]; readVarint(); // strip rows
    if (flags & 4) { const count = source[at++] + 1; at += count * (flags & 1 ? 4 : 3) + 10; }
    const slot = readVarint() & 7;
    const key = `${source[14]}:${slot}`;
    slots.set(key, (slots.get(key) || 0) + 1);
    const stem = name.replace(/-(Fast|Default|Max)\.tfic$/, '');
    const expected = readFileSync(join(directory, stem + '.raw'));
    const actual = decode(source);
    assert.deepEqual(Buffer.from(actual.pixels), expected, name);
    assert.equal(getInfo(source).width, 128);
    decoded++;
  }
  for (const name of readdirSync(directory).filter(n => n.endsWith('.raw'))) {
    const pixels = readFileSync(join(directory, name));
    const channels = Number(name.match(/-(\d+)\.raw$/)[1]);
    const encoded = encode(pixels, 128, 96, channels);
    assert.deepEqual(Buffer.from(decode(encoded).pixels), pixels);
    writeFileSync(join(directory, name.replace(/\.raw$/, '.js.tfic')), encoded);
  }
  for (const [name, slot, palette] of [['gm', 2, false], ['pal', 3, true]]) {
    const { pixels, file } = fixture(slot, palette);
    assert.deepEqual(Buffer.from(decode(file).pixels), Buffer.from(pixels));
    writeFileSync(join(directory, `${name}-64-64-3.raw`), pixels);
    writeFileSync(join(directory, `${name}-64-64-3.js.tfic`), file);
  }
  execFileSync('dotnet', ['run', '--no-build', '--project', project, '--', 'verify', directory], { stdio: 'inherit' });
  const corrupt = Uint8Array.from(encode(new Uint8Array(128 * 96 * 3), 128, 96, 3));
  corrupt[corrupt.length - 1] ^= 1;
  assert.throws(() => decode(corrupt), /checksum/);
  for (const [width, height, channels] of [[1, 1, 3], [2, 100, 4], [17, 31, 3]]) {
    const pixels = Uint8Array.from({ length: width * height * channels }, (_, i) => i * 31 & 255);
    const exif = Uint8Array.of(73, 73, 42, 0);
    const file = encode(pixels, width, height, channels, exif);
    const result = decode(file);
    assert.deepEqual(result.pixels, pixels);
    assert.deepEqual(result.exif, exif);
    assert.equal(getInfo(file).exifLength, exif.length);
  }
  console.log(`Verified ${decoded} .NET streams and 12 JavaScript streams across both decoders. First-strip slots: ${[...slots].map(([key, count]) => `${key}=${count}`).join(', ')}`);
} finally {
  rmSync(directory, { recursive: true, force: true });
}
