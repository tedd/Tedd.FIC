// FIC container versions 0–2 / FICQ payload version 2.
import { decompress as decompressZstd } from './fzstd.js';
const MAX_PIXELS = 1 << 26;
const MAX_DIM = 1 << 24;
const MAX_RUN = 1024;
const COPIES = [[1, 0], [1, 1], [1, -1], [2, 0], [0, -2], [0, -3], [0, -4], [1, -2], [1, 2]];
const GM_SHORTS = [6, 3, 3, 3, 2, 2, 3, 3, 2];
const THRESHOLDS = [[6, 13, 30, 70, 157], [7, 18, 35, 86, 213], [7, 17, 35, 82, 209], [8, 16, 32, 64, 128]];
function fail(message) { throw new Error(`Invalid FIC: ${message}`); }
function bytes(input) {
    if (input instanceof Uint8Array)
        return input;
    if (input instanceof ArrayBuffer)
        return new Uint8Array(input);
    if (ArrayBuffer.isView(input))
        return new Uint8Array(input.buffer, input.byteOffset, input.byteLength);
    throw new TypeError('Expected a Uint8Array or ArrayBuffer');
}
function u32(d, p) { return (d[p] | d[p + 1] << 8 | d[p + 2] << 16 | d[p + 3] << 24) >>> 0; }
function put32(d, p, v) { d[p] = v; d[p + 1] = v >>> 8; d[p + 2] = v >>> 16; d[p + 3] = v >>> 24; }
function zstdContentSize(d) {
    if (d.length < 6 || d[0] !== 0x28 || d[1] !== 0xb5 || d[2] !== 0x2f || d[3] !== 0xfd)
        fail('Zstandard frame');
    const flag = d[4], single = !!(flag & 0x20), contentFlag = flag >>> 6;
    if (flag & 8)
        fail('Zstandard frame');
    let pos = single ? 5 : 6;
    pos += (flag & 3) === 3 ? 4 : flag & 3;
    const length = contentFlag ? 1 << contentFlag : single ? 1 : 0;
    if (!length || pos + length > d.length)
        fail('Zstandard content size');
    let size = 0n;
    for (let i = 0; i < length; i++)
        size |= BigInt(d[pos + i]) << BigInt(8 * i);
    if (length === 2)
        size += 256n;
    if (size < 1n || size > 256n * 1024n * 1024n)
        fail('Zstandard content size');
    pos += length;
    while (true) {
        if (pos + 3 > d.length)
            fail('Zstandard block');
        const block = d[pos] | d[pos + 1] << 8 | d[pos + 2] << 16;
        pos += 3;
        const kind = block >>> 1 & 3, blockSize = block >>> 3;
        if (kind === 3)
            fail('Zstandard block');
        pos += kind === 1 ? 1 : blockSize;
        if (pos > d.length)
            fail('Zstandard block');
        if (block & 1)
            break;
    }
    if (flag & 4)
        pos += 4;
    if (pos !== d.length)
        fail('Zstandard trailing bytes');
    return Number(size);
}
function varint(d, state, end, max = 5) {
    let v = 0;
    for (let i = 0; i < max; i++) {
        if (state.pos >= end)
            fail('truncated integer');
        const b = d[state.pos++];
        v += (b & 127) * 2 ** (7 * i);
        if (b < 128) {
            if (i && b === 0)
                fail('nonminimal integer');
            return v;
        }
    }
    fail('integer too long');
}
function writeVarint(out, v) {
    while (v >= 128) {
        out.push((v % 128) | 128);
        v = Math.floor(v / 128);
    }
    out.push(v);
}
class Reader {
    d;
    pos;
    end;
    constructor(d, pos, end) {
        this.d = d;
        this.pos = pos;
        this.end = end;
    }
    byte() { if (this.pos >= this.end)
        fail('truncated strip'); return this.d[this.pos++]; }
    varint(max = 2) { return varint(this.d, this, this.end, max); }
    done() { if (this.pos !== this.end)
        fail('trailing strip bytes'); }
}
class Bits {
    d;
    start;
    length;
    pos = 0;
    constructor(d, start, len) {
        this.d = d;
        this.start = start;
        this.length = len * 8;
    }
    read(n) {
        if (this.pos + n > this.length)
            fail('truncated bitstream');
        let v = 0;
        for (let i = 0; i < n; i++, this.pos++)
            v |= ((this.d[this.start + (this.pos >>> 3)] >>> (this.pos & 7)) & 1) << i;
        return v;
    }
    eg0() {
        let t = 0;
        while (this.read(1) === 0) {
            if (++t > 8)
                fail('invalid Exp-Golomb code');
        }
        return (1 << t) - 1 + this.read(t);
    }
    done() {
        if (this.length - this.pos >= 8)
            fail('trailing bitstream bytes');
        while (this.pos < this.length)
            if (this.read(1))
                fail('nonzero padding');
    }
}
const crcTable = Uint32Array.from({ length: 256 }, (_, i) => {
    let c = i;
    for (let k = 0; k < 8; k++)
        c = (c >>> 1) ^ ((c & 1) ? 0x82f63b78 : 0);
    return c >>> 0;
});
function crc32c(d, start = 0, end = d.length, seed = 0) {
    let c = (~seed) >>> 0;
    for (let i = start; i < end; i++)
        c = crcTable[(c ^ d[i]) & 255] ^ (c >>> 8);
    return (~c) >>> 0;
}
function hash(v) { return ((v & 255) * 3 + ((v >>> 8) & 255) * 5 + ((v >>> 16) & 255) * 7 + (v >>> 24) * 11) & 63; }
function l2slot(v) { return Math.imul(v, 0x9e3779b1) >>> 22; }
function add(base, dr, dg, db) {
    return (((base & 255) + dr) & 255) | (((((base >>> 8) & 255) + dg) & 255) << 8) |
        (((((base >>> 16) & 255) + db) & 255) << 16) | (base & 0xff000000);
}
function med(a, b, c) { return Math.max(Math.min(a, b), Math.min(Math.max(a, b), a + b - c)); }
function medPx(a, b, c) {
    let v = 0;
    for (let s = 0; s < 32; s += 8)
        v |= med((a >>> s) & 255, (b >>> s) & 255, (c >>> s) & 255) << s;
    return v >>> 0;
}
function unzz(v) { return (v >>> 1) ^ -(v & 1); }
function colour(planes, count, w, y, out) {
    for (let x = 0; x < w; x++) {
        const g = planes[0][x] & 255;
        const r = (planes[1][x] + g) & 255;
        const b = (planes[2][x] + ((r + g) >>> 1)) & 255;
        out[y * w + x] = (r | g << 8 | b << 16 | (count === 4 ? (planes[3][x] & 255) : 255) << 24) >>> 0;
    }
}
function minPayload(codec, w, rows, planes) {
    const px = w * rows;
    if (codec === 'R0')
        return 3 + Math.ceil(rows * planes * Math.min(w, 1 + Math.ceil(w / 8)) / 8);
    if (codec === 'NFOR')
        return Math.ceil(rows * (8 * Math.floor(Math.floor(w / 8) / 17) +
            (Math.floor(w / 8) % 17 ? 4 : 0) + (w % 8 ? 4 * planes : 0)) / 8);
    if (codec === 'F0C')
        return Math.ceil((Math.floor(rows / 4) * (8 * Math.floor(Math.floor(w / 4) / 17) +
            (Math.floor(w / 4) % 17 ? 4 : 0) + (w % 4 ? 5 * planes : 0)) +
            (rows % 4 ? Math.ceil(w / 4) * 5 * planes : 0)) / 8);
    if (codec === 'LITERAL')
        return Math.ceil(px / 62);
    return Math.ceil(3 * px / MAX_RUN);
}
function parse(data) {
    const d = bytes(data);
    if (d.length < 9 || String.fromCharCode(...d.subarray(0, 4)) !== 'FIC\0')
        fail('container header');
    const version = d[4];
    if (version > 2)
        fail('container version');
    const prefix = version === 2 ? 14 : version ? 10 : 9;
    if (d.length < prefix)
        fail('container header');
    const compression = version ? d[5] : 0;
    if (compression > (version === 2 ? 4 : 1))
        fail('compression type');
    const exifLength = u32(d, version ? 6 : 5);
    if (exifLength > 16 * 1024 * 1024 || exifLength > d.length - prefix)
        fail('EXIF length');
    const start = prefix + exifLength;
    let q = d.subarray(start);
    const expandedLength = version === 2 ? u32(d, 10) : 0;
    if (version === 2 && (!expandedLength || expandedLength > 256 * 1024 * 1024))
        fail('payload length');
    if (version === 2 && compression === 0 && q.length !== expandedLength)
        fail('payload length');
    if (version === 2 && compression !== 0 && compression !== 3)
        fail('use decodeAsync for this compression type');
    if ((version === 1 && compression === 1) || (version === 2 && compression === 3)) {
        const expected = zstdContentSize(q);
        if (version === 2 && expected !== expandedLength)
            fail('payload length');
        try {
            q = decompressZstd(q);
        }
        catch {
            fail('Zstandard payload');
        }
        if (q.length !== expected)
            fail('Zstandard content size');
    }
    if (q.length < 12 || String.fromCharCode(...q.subarray(0, 4)) !== 'FICQ' || q[4] !== 2 || q[5] > 1)
        fail('FICQ header');
    const tier = q[5], state = { pos: 6 }, end = q.length - 4;
    const width = varint(q, state, end), height = varint(q, state, end);
    if (!width || !height || width > MAX_DIM || height > MAX_DIM || width * height > MAX_PIXELS)
        fail('image dimensions exceed browser limit');
    if (state.pos >= end)
        fail('flags');
    const flags = q[state.pos++];
    const channels = flags & 1 ? 4 : 3, planes = flags & 2 ? 4 : 3;
    if (flags & ~7 || (planes === 4 && channels !== 4) || ((flags & 4) && tier !== 1))
        fail('flags');
    const rows = varint(q, state, end);
    if (rows < Math.min(height, Math.ceil(4096 / width)) || rows > height)
        fail('strip height');
    let palette = null, shorts = null;
    if (flags & 4) {
        if (state.pos >= end)
            fail('palette');
        const k = q[state.pos++] + 1;
        if (k > 192 || state.pos + k * channels + 10 > end)
            fail('palette');
        palette = new Uint32Array(k);
        for (let i = 0; i < k; i++) {
            const p = state.pos;
            palette[i] = (q[p] | q[p + 1] << 8 | q[p + 2] << 16 | (channels === 4 ? q[p + 3] : 255) << 24) >>> 0;
            state.pos += channels;
        }
        shorts = [...q.subarray(state.pos, state.pos + 10)];
        state.pos += 10;
        if (k + shorts.reduce((a, b) => a + b + 1, 0) > 256)
            fail('palette codes');
    }
    const headerLength = state.pos;
    const count = Math.ceil(height / rows);
    if (count > (end - state.pos) / 2)
        fail('strip table');
    const strips = [];
    for (let i = 0; i < count; i++) {
        const entry = varint(q, state, end), len = Math.floor(entry / 8), slot = entry & 7;
        const codec = (tier === 0 ? ['NFOR', 'F0C', 'GW', 'LITERAL'] : ['R0', 'GW', 'GM', 'PAL', 'LITERAL'])[slot];
        const stripRows = Math.min(rows, height - i * rows);
        if (!codec || (codec === 'PAL' && !palette) ||
            (['NFOR', 'F0C', 'R0'].includes(codec) && (width < 16 || width > 65536)) ||
            len < minPayload(codec, width, stripRows, planes))
            fail('strip table entry');
        strips.push({ codec, len, rows: stripRows, offset: 0 });
    }
    let offset = state.pos;
    for (const strip of strips) {
        strip.offset = offset;
        offset += strip.len;
        if (offset > end)
            fail('strip length');
    }
    if (offset !== end)
        fail('payload length');
    return { d, q, width, height, channels, planes, tier, rows, strips, palette, shorts,
        headerLength, storedCrc: u32(q, end), exif: d.subarray(prefix, start) };
}
function decodeLiteral(d, off, len, n, ch, out) {
    const rd = new Reader(d, off, off + len), cache = new Uint32Array(64);
    let prev = 0xff000000, p = 0;
    while (p < n) {
        const t = rd.byte();
        let run = 1;
        if (t < 64) {
            prev = cache[t];
            if (ch === 3)
                prev |= 0xff000000;
        }
        else if (t < 128)
            prev = add(prev, (t >> 4 & 3) - 2, (t >> 2 & 3) - 2, (t & 3) - 2);
        else if (t < 192) {
            const dg = (t & 63) - 32, u = rd.byte();
            prev = add(prev, dg + (u >> 4) - 8, dg, dg + (u & 15) - 8);
        }
        else if (t < 254)
            run = (t & 63) + 1;
        else if (t === 254)
            prev = (prev & 0xff000000) | rd.byte() | rd.byte() << 8 | rd.byte() << 16;
        else {
            if (ch === 3)
                fail('RGBA operation in RGB strip');
            prev = (rd.byte() | rd.byte() << 8 | rd.byte() << 16 | rd.byte() << 24) >>> 0;
        }
        if (run > n - p)
            fail('run crosses strip');
        cache[hash(prev)] = prev;
        out.fill(prev >>> 0, p, p + run);
        p += run;
    }
    rd.done();
}
function decodePalette(d, off, len, n, w, palette, shorts, out) {
    const groups = new Array(256), rd = new Reader(d, off, off + len);
    let code = palette.length;
    for (let cls = 0; cls < 10; cls++) {
        for (let l = 1; l <= shorts[cls]; l++)
            groups[code++] = [cls, l, shorts[cls]];
        groups[code++] = [cls, 0, shorts[cls]];
    }
    let prev = palette[0], p = 0;
    while (p < n) {
        const t = rd.byte();
        if (t < palette.length) {
            prev = palette[t];
            out[p++] = prev;
            continue;
        }
        const group = groups[t];
        if (!group)
            fail('palette code');
        const [cls, shortLen, shortCount] = group;
        const run = shortLen || shortCount + 1 + rd.varint();
        if (run > MAX_RUN || run > n - p)
            fail('palette run');
        if (cls === 0) {
            out.fill(prev, p, p + run);
            p += run;
        }
        else {
            const dist = COPIES[cls - 1][0] * w - COPIES[cls - 1][1];
            if (dist < 1 || p < dist)
                fail('palette copy');
            for (let i = 0; i < run; i++, p++)
                out[p] = out[p - dist];
            prev = out[p - 1];
        }
    }
    rd.done();
}
function decodeQp(d, off, len, n, w, ch, medMode, out) {
    const groups = new Array(256), cache = new Uint32Array(64), l2 = new Uint32Array(1024);
    const copies = medMode ? COPIES : [COPIES[0]];
    const shortCopies = medMode ? GM_SHORTS : [26];
    let code = 200, runShort = medMode ? 5 : 18;
    for (let l = 1; l <= runShort; l++)
        groups[code++] = [0, l, runShort];
    groups[code++] = [0, 0, runShort];
    if (medMode) {
        for (let l = 1; l <= 3; l++)
            groups[code++] = [1, l, 3];
        groups[code++] = [1, 0, 3];
    }
    for (let j = 0; j < copies.length; j++) {
        const s = shortCopies[j];
        for (let l = 1; l <= s; l++)
            groups[code++] = [j + 2, l, s];
        groups[code++] = [j + 2, 0, s];
    }
    const rd = new Reader(d, off, off + len);
    let prev = 0xff000000, p = 0;
    const fix = (v) => ch === 3 ? (v | 0xff000000) >>> 0 : v >>> 0;
    const base = () => medMode && p >= w + 1 ? fix(medPx(out[p - 1], out[p - w], out[p - w - 1])) : prev;
    while (p < n) {
        const t = rd.byte();
        let v, lit = false;
        const opStart = rd.pos - 1;
        if (t < 64)
            v = fix(cache[t]);
        else if (t < 128)
            v = add(base(), (t >> 4 & 3) - 2, (t >> 2 & 3) - 2, (t & 3) - 2);
        else if (t < 192) {
            const dg = (t & 63) - 32, u = rd.byte();
            v = add(base(), dg + (u >> 4) - 8, dg, dg + (u & 15) - 8);
        }
        else if (t < 200) {
            const v19 = (t & 7) << 16 | rd.byte() << 8 | rd.byte(), dg = (v19 >>> 12) - 64;
            v = add(base(), dg + ((v19 >>> 6) & 63) - 32, dg, dg + (v19 & 63) - 32);
            lit = true;
        }
        else if (t < 246) {
            const group = groups[t];
            if (!group)
                fail('byte-mode code');
            const [cls, shortLen, shortCount] = group;
            const run = shortLen || shortCount + 1 + rd.varint();
            if (run > MAX_RUN || run > n - p)
                fail('byte-mode run');
            if (cls === 0) {
                out.fill(prev, p, p + run);
                p += run;
            }
            else if (cls === 1) {
                for (let i = 0; i < run; i++) {
                    prev = base();
                    out[p++] = prev;
                }
            }
            else {
                const dist = copies[cls - 2][0] * w - copies[cls - 2][1];
                if (dist < 1 || p < dist)
                    fail('byte-mode copy');
                for (let i = 0; i < run; i++, p++)
                    out[p] = out[p - dist];
            }
            prev = out[p - 1];
            cache[hash(prev)] = prev;
            continue;
        }
        else if (t < 250)
            v = fix(l2[(t - 246) << 8 | rd.byte()]);
        else if (t < 254) {
            if (ch === 3)
                fail('alpha prefix in RGB strip');
            const alpha = t === 250 ? 0 : t === 251 ? 255 : t === 252 ? (p >= w ? out[p - w] >>> 24 : fail('alpha copy')) : rd.byte();
            const b = (base() & 0x00ffffff) | alpha << 24, t2 = rd.byte();
            if (t2 >= 64 && t2 < 128)
                v = add(b, (t2 >> 4 & 3) - 2, (t2 >> 2 & 3) - 2, (t2 & 3) - 2);
            else if (t2 >= 128 && t2 < 192) {
                const dg = (t2 & 63) - 32, u = rd.byte();
                v = add(b, dg + (u >> 4) - 8, dg, dg + (u & 15) - 8);
            }
            else if (t2 >= 192 && t2 < 200) {
                const v19 = (t2 & 7) << 16 | rd.byte() << 8 | rd.byte(), dg = (v19 >>> 12) - 64;
                v = add(b, dg + ((v19 >>> 6) & 63) - 32, dg, dg + (v19 & 63) - 32);
            }
            else if (t2 === 254)
                v = (b & 0xff000000) | rd.byte() | rd.byte() << 8 | rd.byte() << 16;
            else
                fail('alpha colour operation');
            lit = rd.pos - opStart >= 3;
        }
        else if (t === 254) {
            v = (base() & 0xff000000) | rd.byte() | rd.byte() << 8 | rd.byte() << 16;
            lit = true;
        }
        else {
            v = fix((rd.byte() | rd.byte() << 8 | rd.byte() << 16 | rd.byte() << 24) >>> 0);
            lit = true;
        }
        v >>>= 0;
        out[p++] = v;
        prev = v;
        cache[hash(v)] = v;
        if (lit)
            l2[l2slot(v)] = v;
    }
    rd.done();
}
function decodeNfor(d, off, len, w, rows, planes, out) {
    const bits = new Bits(d, off, len), cur = Array.from({ length: planes }, () => new Uint16Array(w));
    const prev = Array.from({ length: planes }, () => new Uint16Array(w));
    const z = Array.from({ length: planes }, () => new Uint16Array(w));
    const nfull = Math.floor(w / 8), tail = w % 8;
    for (let y = 0; y < rows; y++) {
        let g = 0;
        while (g < nfull) {
            const n0 = bits.read(4);
            if (n0 === 9 || n0 === 10) {
                const count = n0 === 9 ? 1 : bits.read(4) + 2;
                if (g + count > nfull)
                    fail('NFOR zero group');
                for (let p = 0; p < planes; p++)
                    z[p].fill(0, g * 8, (g + count) * 8);
                g += count;
                continue;
            }
            if (n0 > 8)
                fail('NFOR width');
            for (let p = 0; p < planes; p++) {
                const wd = p === 0 ? n0 : bits.read(4);
                if (wd > 8)
                    fail('NFOR width');
                for (let i = 0; i < 8; i++)
                    z[p][g * 8 + i] = bits.read(wd);
            }
            g++;
        }
        if (tail)
            for (let p = 0; p < planes; p++) {
                const wd = bits.read(4);
                if (wd > 8)
                    fail('NFOR tail width');
                for (let i = 0; i < tail; i++)
                    z[p][nfull * 8 + i] = bits.read(wd);
            }
        for (let p = 0; p < planes; p++)
            for (let x = 0; x < w; x++) {
                const pred = y ? prev[p][x] : x ? cur[p][x - 1] : 0;
                cur[p][x] = (pred + unzz(z[p][x])) & 255;
            }
        colour(cur, planes, w, y, out);
        for (let p = 0; p < planes; p++)
            prev[p].set(cur[p]);
    }
    bits.done();
}
function decodeF0c(d, off, len, w, rows, planes, out) {
    const bits = new Bits(d, off, len);
    const v = Array.from({ length: planes }, () => Array.from({ length: rows }, () => new Uint16Array(w)));
    const lb = new Uint16Array(planes), up1 = new Uint16Array(planes);
    const nbx = Math.ceil(w / 4), nfull = Math.floor(w / 4);
    for (let by = 0; by * 4 < rows; by++) {
        const y0 = by * 4, chh = Math.min(4, rows - y0);
        let bx = 0;
        while (bx < nbx) {
            const x0 = bx * 4, cw = Math.min(4, w - x0), full = chh === 4 && cw === 4;
            const n0 = bits.read(4);
            if (full && n0 >= 9) {
                if (n0 > 12)
                    fail('F0C mode');
                const count = n0 >= 11 ? bits.read(4) + 2 : 1;
                if (bx + count > nfull)
                    fail('F0C group');
                const copyUp = n0 === 9 || n0 === 11;
                if ((copyUp && by === 0) || (!copyUp && bx === 0 && by === 0))
                    fail('F0C copy');
                for (let p = 0; p < planes; p++) {
                    const ref = copyUp ? 0 : bx ? v[p][y0][x0 - 1] : v[p][y0 - 1][x0];
                    for (let r = 0; r < 4; r++)
                        for (let x = x0; x < x0 + 4 * count; x++)
                            v[p][y0 + r][x] = copyUp ? v[p][y0 - 4 + r][x] : ref;
                    lb[p] = v[p][y0][x0 + 4 * (count - 1)];
                    if (bx === 0)
                        up1[p] = v[p][y0][0];
                }
                bx += count;
                continue;
            }
            for (let p = 0; p < planes; p++) {
                const wd = p === 0 ? n0 : bits.read(4);
                if (wd > 8)
                    fail('F0C width');
                let base = 0;
                if (wd < 8) {
                    const pred = bx ? lb[p] : by ? up1[p] : 0, zb = bits.eg0();
                    if (zb > 255)
                        fail('F0C base');
                    base = (pred + unzz(zb)) & 255;
                }
                for (let r = 0; r < chh; r++)
                    for (let x = 0; x < cw; x++)
                        v[p][y0 + r][x0 + x] = (base + bits.read(wd)) & 255;
                const newBase = wd < 8 ? base : v[p][y0][x0];
                lb[p] = newBase;
                if (bx === 0)
                    up1[p] = newBase;
            }
            bx++;
        }
    }
    for (let y = 0; y < rows; y++)
        colour(v.map(plane => plane[y]), planes, w, y, out);
    bits.done();
}
function decodeR0(d, off, len, w, rows, planes, out) {
    const rd = new Reader(d, off, off + len);
    const x0 = rd.byte(), plain = !!(x0 & 1), xbits = 1 + (plain ? 4 * planes : 0);
    const xbytes = Math.ceil((xbits + 1) / 8);
    let xv = BigInt(x0);
    for (let i = 1; i < xbytes; i++)
        xv |= BigInt(rd.byte()) << BigInt(8 * i);
    const masks = !!((xv >> BigInt(xbits)) & 1n);
    if ((xv >> BigInt(xbits + 1)) !== 0n)
        fail('R0 header');
    const kPlain = new Uint8Array(planes);
    if (plain)
        for (let p = 0; p < planes; p++) {
            kPlain[p] = Number((xv >> BigInt(1 + 4 * p)) & 15n);
            if (kPlain[p] > 8)
                fail('R0 Rice parameter');
        }
    const le = rd.varint(5), lr = rd.varint(5), lm = masks ? rd.varint(5) : 0;
    if (rd.pos + le + lr + lm > off + len)
        fail('R0 stream lengths');
    const eStart = rd.pos, rStart = eStart + le, mStart = rStart + lr, qStart = mStart + lm;
    const R = new Bits(d, rStart, lr), M = new Bits(d, mStart, lm), Q = new Bits(d, qStart, off + len - qStart);
    let ei = 0;
    const z = Array.from({ length: planes }, () => Array.from({ length: 3 }, () => new Uint16Array(w)));
    let prev = Array.from({ length: planes }, () => new Uint16Array(w));
    let cur = Array.from({ length: planes }, () => new Uint16Array(w));
    for (let y = 0; y < rows; y++) {
        for (let p = 0; p < planes; p++) {
            const zc = z[p][y % 3], zn = y ? z[p][(y - 1) % 3] : null;
            const znn = y >= 2 ? z[p][(y - 2) % 3] : zn;
            const masked = masks && M.read(1) === 1;
            const skip = new Uint8Array(Math.ceil(w / 8));
            if (masked)
                for (let g = 0; g < skip.length; g++)
                    skip[g] = M.read(1);
            for (let x = 0; x < w; x++) {
                let k = 0;
                if (!y)
                    k = plain ? kPlain[p] : 0;
                else {
                    const at = (xx) => zn[Math.max(0, Math.min(w - 1, xx))];
                    const at2 = (xx) => znn[Math.max(0, Math.min(w - 1, xx))];
                    const act = Math.min(255, 2 * at(x) + at(x - 1) + at(x + 1) + at2(x));
                    for (const t of THRESHOLDS[p])
                        if (act >= t)
                            k++;
                }
                if (skip[x >>> 3]) {
                    zc[x] = 0;
                    continue;
                }
                let q = 0;
                while (Q.read(1) === 0)
                    if (++q > 16)
                        fail('R0 quotient');
                if (q === 16) {
                    if (ei >= le)
                        fail('R0 exception');
                    q = d[eStart + ei++];
                    if (q < 16)
                        fail('R0 exception');
                }
                const zz = q * (1 << k) + R.read(k);
                if (zz > 255)
                    fail('R0 residual');
                zc[x] = zz;
            }
            for (let x = 0; x < w; x++) {
                const N = prev[p][x], W = x ? cur[p][x - 1] : N, NW = x ? prev[p][x - 1] : N;
                cur[p][x] = (med(W, N, NW) + unzz(zc[x])) & 255;
            }
        }
        const g = cur[0], r = cur[1], b = cur[2], a = cur[3];
        for (let x = 0; x < w; x++) {
            const rv = (r[x] + g[x] + 128) & 255;
            out[y * w + x] = (rv | g[x] << 8 | ((b[x] + ((rv + g[x]) >>> 1) + 128) & 255) << 16 |
                (planes === 4 ? a[x] : 255) << 24) >>> 0;
        }
        [prev, cur] = [cur, prev];
    }
    if (ei !== le)
        fail('R0 exceptions');
    R.done();
    M.done();
    Q.done();
}
function pixelBytes(pixels, channels) {
    const out = new Uint8Array(pixels.length * channels);
    for (let i = 0, j = 0; i < pixels.length; i++) {
        const v = pixels[i];
        out[j++] = v;
        out[j++] = v >>> 8;
        out[j++] = v >>> 16;
        if (channels === 4)
            out[j++] = v >>> 24;
    }
    return out;
}
export function getInfo(input) {
    const h = parse(input);
    return { width: h.width, height: h.height, channels: h.channels,
        tier: h.tier === 0 ? 'Fast' : 'Compact', exifLength: h.exif.length };
}
export function decode(input) {
    const h = parse(input), pixels = new Uint32Array(h.width * h.height);
    for (let i = 0; i < h.strips.length; i++) {
        const s = h.strips[i], n = h.width * s.rows;
        const out = pixels.subarray(i * h.rows * h.width, i * h.rows * h.width + n);
        if (s.codec === 'LITERAL')
            decodeLiteral(h.q, s.offset, s.len, n, h.channels, out);
        else if (s.codec === 'GW' || s.codec === 'GM')
            decodeQp(h.q, s.offset, s.len, n, h.width, h.channels, s.codec === 'GM', out);
        else if (s.codec === 'PAL')
            decodePalette(h.q, s.offset, s.len, n, h.width, h.palette, h.shorts, out);
        else if (s.codec === 'NFOR')
            decodeNfor(h.q, s.offset, s.len, h.width, s.rows, h.planes, out);
        else if (s.codec === 'F0C')
            decodeF0c(h.q, s.offset, s.len, h.width, s.rows, h.planes, out);
        else
            decodeR0(h.q, s.offset, s.len, h.width, s.rows, h.planes, out);
    }
    const result = pixelBytes(pixels, h.channels);
    let crc = crc32c(h.q, 0, h.headerLength);
    crc = crc32c(result, 0, result.length, crc);
    if (crc !== h.storedCrc)
        fail('pixel checksum');
    return { pixels: result, width: h.width, height: h.height, channels: h.channels,
        exif: h.exif.slice(), tier: h.tier === 0 ? 'Fast' : 'Compact' };
}
// Deflate, GZip, and Brotli use the platform's streaming decoder. Zstandard remains synchronous.
export async function decodeAsync(input) {
    const d = bytes(input);
    if (d.length < 14 || d[4] !== 2 || (d[5] !== 1 && d[5] !== 2 && d[5] !== 4))
        return decode(d);
    if (String.fromCharCode(...d.subarray(0, 4)) !== 'FIC\0')
        fail('container header');
    const exifLength = u32(d, 6), expected = u32(d, 10);
    if (exifLength > 16 * 1024 * 1024 || exifLength > d.length - 14)
        fail('EXIF length');
    if (!expected || expected > 256 * 1024 * 1024)
        fail('payload length');
    const format = d[5] === 1 ? 'deflate-raw' : d[5] === 2 ? 'gzip' : 'brotli';
    const compressed = d.subarray(14 + exifLength);
    let expanded;
    try {
        const stream = new Blob([Uint8Array.from(compressed)]).stream().pipeThrough(new DecompressionStream(format));
        const reader = stream.getReader(), chunks = [];
        let total = 0;
        while (true) {
            const { value, done } = await reader.read();
            if (done)
                break;
            total += value.length;
            if (total > expected) {
                await reader.cancel();
                fail('payload length');
            }
            chunks.push(value);
        }
        if (total !== expected)
            fail('payload length');
        expanded = new Uint8Array(total);
        let offset = 0;
        for (const chunk of chunks) {
            expanded.set(chunk, offset);
            offset += chunk.length;
        }
    }
    catch (error) {
        if (error instanceof Error && error.message.startsWith('Invalid FIC:'))
            throw error;
        fail(`${format} payload or unsupported browser decoder`);
    }
    const legacy = new Uint8Array(9 + exifLength + expanded.length);
    legacy.set([70, 73, 67, 0, 0], 0);
    put32(legacy, 5, exifLength);
    legacy.set(d.subarray(14, 14 + exifLength), 9);
    legacy.set(expanded, 9 + exifLength);
    return decode(legacy);
}
function encodeLiteral(pixels, start, end, channels) {
    const out = [], cache = new Uint32Array(64);
    let prev = 0xff000000, run = 0;
    for (let i = start; i < end; i++) {
        const p = i * channels;
        const v = (pixels[p] | pixels[p + 1] << 8 | pixels[p + 2] << 16 |
            (channels === 4 ? pixels[p + 3] : 255) << 24) >>> 0;
        if (v === prev) {
            if (++run === 62 || i === end - 1) {
                out.push(0xc0 | (run - 1));
                run = 0;
            }
            continue;
        }
        if (run) {
            out.push(0xc0 | (run - 1));
            run = 0;
        }
        const h = hash(v);
        if (cache[h] === v)
            out.push(h);
        else {
            cache[h] = v;
            if ((v >>> 24) !== (prev >>> 24))
                out.push(255, v & 255, (v >>> 8) & 255, (v >>> 16) & 255, v >>> 24);
            else {
                const dr = (v & 255) - (prev & 255), dg = ((v >>> 8) & 255) - ((prev >>> 8) & 255);
                const db = ((v >>> 16) & 255) - ((prev >>> 16) & 255);
                const r = (dr + 128 & 255) - 128, g = (dg + 128 & 255) - 128, b = (db + 128 & 255) - 128;
                if (r >= -2 && r <= 1 && g >= -2 && g <= 1 && b >= -2 && b <= 1)
                    out.push(0x40 | (r + 2) << 4 | (g + 2) << 2 | (b + 2));
                else if (g >= -32 && g <= 31 && r - g >= -8 && r - g <= 7 && b - g >= -8 && b - g <= 7)
                    out.push(0x80 | (g + 32), (r - g + 8) << 4 | (b - g + 8));
                else
                    out.push(254, v & 255, (v >>> 8) & 255, (v >>> 16) & 255);
            }
        }
        prev = v;
    }
    return Uint8Array.from(out);
}
export function encode(input, width, height, channels = 4, exifInput = new Uint8Array()) {
    const pixels = bytes(input), exif = bytes(exifInput);
    if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1 ||
        width > MAX_DIM || height > MAX_DIM || width * height > MAX_PIXELS ||
        (channels !== 3 && channels !== 4) || pixels.length !== width * height * channels)
        throw new RangeError('Invalid image dimensions or pixel buffer');
    if (exif.length > 16 * 1024 * 1024)
        throw new RangeError('EXIF exceeds 16 MiB');
    const rows = Math.min(height, Math.max(32, Math.ceil(4096 / width)));
    const header = [70, 73, 67, 81, 2, 0];
    writeVarint(header, width);
    writeVarint(header, height);
    header.push(channels === 4 ? 1 : 0);
    writeVarint(header, rows);
    const table = [], strips = [];
    for (let y = 0; y < height; y += rows) {
        const payload = encodeLiteral(pixels, y * width, Math.min(height, y + rows) * width, channels);
        strips.push(payload);
        writeVarint(table, payload.length * 8 + 3); // Fast-tier Literal slot.
    }
    const length = 9 + exif.length + header.length + table.length + strips.reduce((a, s) => a + s.length, 0) + 4;
    const result = new Uint8Array(length);
    result.set([70, 73, 67, 0, 0]);
    put32(result, 5, exif.length);
    result.set(exif, 9);
    let pos = 9 + exif.length;
    result.set(header, pos);
    pos += header.length;
    result.set(table, pos);
    pos += table.length;
    for (const strip of strips) {
        result.set(strip, pos);
        pos += strip.length;
    }
    let crc = crc32c(Uint8Array.from(header));
    crc = crc32c(pixels, 0, pixels.length, crc);
    put32(result, pos, crc);
    return result;
}
