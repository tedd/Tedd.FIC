#include "fic.h"

#include <limits.h>
#include <stdlib.h>
#include <string.h>

#define MAX_DIM (1u << 24)
#define MAX_PIXELS (1u << 29)
#define MAX_EXIF (16u << 20)
#define MAX_RUN 1024u

typedef struct { uint8_t *p; size_t n, cap; } writer;
typedef struct { const uint8_t *p; size_t n, at; } reader;
typedef struct { const uint8_t *p; size_t bits, at; } bits;
typedef struct { const uint8_t *p; size_t len; uint32_t rows; unsigned codec; } strip;

static int put(writer *w, uint8_t b) {
    if (w->n == w->cap) {
        size_t cap = w->cap ? w->cap * 2 : 256;
        uint8_t *p;
        if (cap <= w->cap) return 0;
        p = (uint8_t *)realloc(w->p, cap);
        if (!p) return 0;
        w->p = p; w->cap = cap;
    }
    w->p[w->n++] = b;
    return 1;
}
static int append(writer *w, const uint8_t *p, size_t n) {
    size_t i;
    for (i = 0; i < n; i++) if (!put(w, p[i])) return 0;
    return 1;
}
static int put32(writer *w, uint32_t v) {
    return put(w, (uint8_t)v) && put(w, (uint8_t)(v >> 8)) &&
           put(w, (uint8_t)(v >> 16)) && put(w, (uint8_t)(v >> 24));
}
static int putvar(writer *w, uint64_t v) {
    while (v >= 128) { if (!put(w, (uint8_t)(v | 128))) return 0; v >>= 7; }
    return put(w, (uint8_t)v);
}
static uint32_t u32(const uint8_t *p) {
    return (uint32_t)p[0] | (uint32_t)p[1] << 8 |
           (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24;
}
static int byte(reader *r, unsigned *v) {
    if (r->at >= r->n) return 0;
    *v = r->p[r->at++]; return 1;
}
static int var(reader *r, unsigned max, uint64_t *v) {
    unsigned i, b;
    *v = 0;
    for (i = 0; i < max; i++) {
        if (!byte(r, &b)) return 0;
        *v |= (uint64_t)(b & 127u) << (i * 7);
        if (b < 128) return i == 0 || b != 0;
    }
    return 0;
}
static uint32_t crc32c(const uint8_t *p, size_t n, uint32_t seed) {
    uint32_t c = ~seed;
    size_t i; unsigned j;
    for (i = 0; i < n; i++) {
        c ^= p[i];
        for (j = 0; j < 8; j++) c = (c >> 1) ^ (0x82f63b78u & (0u - (c & 1u)));
    }
    return ~c;
}
static unsigned hash(uint32_t v) {
    return ((v & 255u) * 3 + ((v >> 8) & 255u) * 5 +
            ((v >> 16) & 255u) * 7 + (v >> 24) * 11) & 63u;
}
static uint32_t color_at(const uint8_t *p, unsigned ch) {
    return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 |
           (ch == 4 ? (uint32_t)p[3] : 255u) << 24;
}
static void pixel_to_bytes(uint32_t v, uint8_t *p, unsigned ch) {
    p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); p[2] = (uint8_t)(v >> 16);
    if (ch == 4) p[3] = (uint8_t)(v >> 24);
}
static uint32_t add(uint32_t v, int dr, int dg, int db) {
    return ((v + (uint32_t)dr) & 255u) |
           ((((v >> 8) + (uint32_t)dg) & 255u) << 8) |
           ((((v >> 16) + (uint32_t)db) & 255u) << 16) | (v & 0xff000000u);
}
static int signed_delta(unsigned a, unsigned b) { return (int)((a - b + 128u) & 255u) - 128; }

fic_status fic_encode(const uint8_t *pixels, uint32_t width, uint32_t height,
                      uint8_t channels, const uint8_t *exif, size_t exif_length,
                      uint8_t **output, size_t *output_length) {
    writer q = {0}, table = {0}, payload = {0}, file = {0};
    uint32_t rows, y, crc, cache[64] = {0};
    size_t pixel_count, i;
    fic_status result = FIC_NOMEM;
    if (!output || !output_length) return FIC_INVALID;
    *output = NULL; *output_length = 0;
    if (!pixels || (exif_length && !exif) || (channels != 3 && channels != 4) ||
        !width || !height || width > MAX_DIM || height > MAX_DIM ||
        (uint64_t)width * height > MAX_PIXELS || exif_length > MAX_EXIF ||
        (uint64_t)width * height * channels > SIZE_MAX) return FIC_INVALID;
    pixel_count = (size_t)width * height;
    rows = (4096u + width - 1u) / width;
    if (rows < 32) rows = 32;
    if (rows > height) rows = height;
    if (!append(&q, (const uint8_t *)"FICQ", 4) || !put(&q, 2) || !put(&q, 0) ||
        !putvar(&q, width) || !putvar(&q, height) || !put(&q, channels == 4) ||
        !putvar(&q, rows)) goto done;
    crc = crc32c(q.p, q.n, 0);
    for (y = 0; y < height; y += rows) {
        uint32_t prev = 0xff000000u;
        size_t start = payload.n, end = (size_t)(y + (rows < height - y ? rows : height - y)) * width;
        unsigned run = 0;
        memset(cache, 0, sizeof cache);
        for (i = (size_t)y * width; i < end; i++) {
            uint32_t v = color_at(pixels + i * channels, channels);
            if (v == prev) {
                if (++run == 62 || i + 1 == end) {
                    if (!put(&payload, (uint8_t)(0xc0u | (run - 1)))) goto done;
                    run = 0;
                }
                continue;
            }
            if (run) { if (!put(&payload, (uint8_t)(0xc0u | (run - 1)))) goto done; run = 0; }
            if (cache[hash(v)] == v) {
                if (!put(&payload, (uint8_t)hash(v))) goto done;
            } else {
                int dr = signed_delta(v & 255u, prev & 255u);
                int dg = signed_delta((v >> 8) & 255u, (prev >> 8) & 255u);
                int db = signed_delta((v >> 16) & 255u, (prev >> 16) & 255u);
                cache[hash(v)] = v;
                if ((v >> 24) != (prev >> 24)) {
                    if (!put(&payload, 255) || !put32(&payload, v)) goto done;
                } else if (dr >= -2 && dr <= 1 && dg >= -2 && dg <= 1 && db >= -2 && db <= 1) {
                    if (!put(&payload, (uint8_t)(0x40 | (dr + 2) << 4 | (dg + 2) << 2 | (db + 2)))) goto done;
                } else if (dg >= -32 && dg <= 31 && dr - dg >= -8 && dr - dg <= 7 && db - dg >= -8 && db - dg <= 7) {
                    if (!put(&payload, (uint8_t)(0x80 | (dg + 32))) ||
                        !put(&payload, (uint8_t)((dr - dg + 8) << 4 | (db - dg + 8)))) goto done;
                } else {
                    if (!put(&payload, 254) || !put(&payload, (uint8_t)v) ||
                        !put(&payload, (uint8_t)(v >> 8)) || !put(&payload, (uint8_t)(v >> 16))) goto done;
                }
            }
            prev = v;
        }
        if (!putvar(&table, ((uint64_t)(payload.n - start) << 3) | 3u)) goto done;
    }
    if (!append(&file, (const uint8_t *)"FIC\0", 4) || !put(&file, 0) ||
        !put32(&file, (uint32_t)exif_length) || !append(&file, exif, exif_length) ||
        !append(&file, q.p, q.n) || !append(&file, table.p, table.n) ||
        !append(&file, payload.p, payload.n)) goto done;
    crc = crc32c(pixels, pixel_count * channels, crc);
    if (!put32(&file, crc)) goto done;
    *output = file.p; *output_length = file.n; file.p = NULL;
    result = FIC_OK;
done:
    free(q.p); free(table.p); free(payload.p); free(file.p);
    return result;
}

static int readbits(bits *b, unsigned n, unsigned *v) {
    unsigned i;
    if (n > 24 || b->at > b->bits || n > b->bits - b->at) return 0;
    *v = 0;
    for (i = 0; i < n; i++, b->at++)
        *v |= ((b->p[b->at >> 3] >> (b->at & 7)) & 1u) << i;
    return 1;
}
static int bitdone(bits *b) {
    unsigned v;
    if (b->bits - b->at >= 8) return 0;
    while (b->at < b->bits) if (!readbits(b, 1, &v) || v) return 0;
    return 1;
}
static int eg0(bits *b, unsigned *v) {
    unsigned t = 0, x;
    do { if (!readbits(b, 1, &x)) return 0; if (!x && ++t > 8) return 0; } while (!x);
    if (!readbits(b, t, &x)) return 0;
    *v = ((1u << t) - 1u) + x; return 1;
}
static int unzz(unsigned v) { return (int)(v >> 1) ^ -(int)(v & 1u); }
static unsigned med(unsigned a, unsigned b, unsigned c) {
    unsigned lo = a < b ? a : b, hi = a > b ? a : b;
    int p = (int)a + (int)b - (int)c;
    return p < (int)lo ? lo : p > (int)hi ? hi : (unsigned)p;
}
static uint32_t medpx(uint32_t a, uint32_t b, uint32_t c) {
    uint32_t v = 0; unsigned s;
    for (s = 0; s < 32; s += 8) v |= med((a >> s) & 255u, (b >> s) & 255u, (c >> s) & 255u) << s;
    return v;
}
static unsigned l2slot(uint32_t v) { return (v * 0x9e3779b1u) >> 22; }
static uint32_t fix(uint32_t v, unsigned ch) { return ch == 3 ? v | 0xff000000u : v; }

static int literal(reader *r, size_t n, unsigned ch, uint32_t *out) {
    uint32_t cache[64] = {0}, prev = 0xff000000u;
    size_t p = 0; unsigned t, x, y, z, run;
    while (p < n) {
        if (!byte(r, &t)) return 0;
        run = 1;
        if (t < 64) prev = fix(cache[t], ch);
        else if (t < 128) prev = add(prev, ((t >> 4) & 3) - 2, ((t >> 2) & 3) - 2, (t & 3) - 2);
        else if (t < 192) {
            if (!byte(r, &x)) return 0;
            y = t & 63u;
            prev = add(prev, (int)y - 32 + (int)(x >> 4) - 8,
                       (int)y - 32, (int)y - 32 + (int)(x & 15) - 8);
        } else if (t < 254) run = (t & 63u) + 1;
        else if (t == 254) {
            if (!byte(r, &x) || !byte(r, &y) || !byte(r, &z)) return 0;
            prev = (prev & 0xff000000u) | x | y << 8 | z << 16;
        } else {
            if (ch == 3 || r->n - r->at < 4) return 0;
            prev = u32(r->p + r->at); r->at += 4;
        }
        if (run > n - p) return 0;
        cache[hash(prev)] = prev;
        while (run--) out[p++] = prev;
    }
    return r->at == r->n;
}

static const int copies[9][2] = {{1,0},{1,1},{1,-1},{2,0},{0,-2},{0,-3},{0,-4},{1,-2},{1,2}};
static const unsigned gm_shorts[9] = {6,3,3,3,2,2,3,3,2};
typedef struct { unsigned cls, length, short_count; } group;

static int palette_strip(reader *r, size_t n, unsigned w, const uint32_t *pal,
                         unsigned k, const uint8_t *shorts, uint32_t *out) {
    group groups[256] = {{0}};
    unsigned code = k, cls, l, t;
    uint32_t prev = pal[0]; size_t p = 0;
    for (cls = 0; cls < 10; cls++) {
        for (l = 1; l <= shorts[cls]; l++) groups[code++] = (group){cls,l,shorts[cls]};
        groups[code++] = (group){cls,0,shorts[cls]};
    }
    while (p < n) {
        unsigned run; uint64_t extra; size_t dist, j;
        if (!byte(r, &t)) return 0;
        if (t < k) { prev = pal[t]; out[p++] = prev; continue; }
        if (t >= code) return 0;
        cls = groups[t].cls;
        run = groups[t].length;
        if (!run) {
            if (!var(r, 2, &extra)) return 0;
            run = groups[t].short_count + 1u + (unsigned)extra;
        }
        if (run > MAX_RUN || run > n - p) return 0;
        if (cls == 0) while (run--) out[p++] = prev;
        else {
            int d = copies[cls - 1][0] * (int)w - copies[cls - 1][1];
            if (d < 1 || p < (size_t)d) return 0;
            dist = (size_t)d;
            for (j = 0; j < run; j++, p++) out[p] = out[p - dist];
            prev = out[p - 1];
        }
    }
    return r->at == r->n;
}

static int qp(reader *r, size_t n, unsigned w, unsigned ch, int gm, uint32_t *out) {
    group groups[256] = {{0}};
    uint32_t cache[64] = {0}, l2[1024] = {0}, prev = 0xff000000u;
    unsigned code = 200, s = gm ? 5 : 18, l, j, t, u;
    size_t p = 0;
    for (l = 1; l <= s; l++) groups[code++] = (group){0,l,s};
    groups[code++] = (group){0,0,s};
    if (gm) {
        for (l = 1; l <= 3; l++) groups[code++] = (group){1,l,3};
        groups[code++] = (group){1,0,3};
    }
    for (j = 0; j < (gm ? 9u : 1u); j++) {
        s = gm ? gm_shorts[j] : 26;
        for (l = 1; l <= s; l++) groups[code++] = (group){j + 2,l,s};
        groups[code++] = (group){j + 2,0,s};
    }
    while (p < n) {
        uint32_t v, base;
        size_t op; int lit = 0;
        if (!byte(r, &t)) return 0;
        op = r->at - 1;
        base = gm && p >= (size_t)w + 1 ? fix(medpx(out[p-1], out[p-w], out[p-w-1]), ch) : prev;
        if (t < 64) v = fix(cache[t], ch);
        else if (t < 128) v = add(base, (int)((t >> 4) & 3) - 2, (int)((t >> 2) & 3) - 2, (int)(t & 3) - 2);
        else if (t < 192) {
            int dg = (int)(t & 63) - 32;
            if (!byte(r, &u)) return 0;
            v = add(base, dg + (int)(u >> 4) - 8, dg, dg + (int)(u & 15) - 8);
        } else if (t < 200) {
            unsigned a, b, val; int dg;
            if (!byte(r, &a) || !byte(r, &b)) return 0;
            val = (t & 7u) << 16 | a << 8 | b; dg = (int)(val >> 12) - 64;
            v = add(base, dg + (int)((val >> 6) & 63) - 32, dg, dg + (int)(val & 63) - 32);
            lit = 1;
        } else if (t < 246) {
            group g = groups[t]; unsigned run = g.length; uint64_t extra;
            if (!run) {
                if (!var(r, 2, &extra)) return 0;
                run = g.short_count + 1u + (unsigned)extra;
            }
            if (run > MAX_RUN || run > n - p) return 0;
            if (g.cls == 0) while (run--) out[p++] = prev;
            else if (g.cls == 1) while (run--) {
                prev = gm && p >= (size_t)w + 1 ? fix(medpx(out[p-1], out[p-w], out[p-w-1]), ch) : prev;
                out[p++] = prev;
            } else {
                int d = copies[g.cls - 2][0] * (int)w - copies[g.cls - 2][1];
                if (d < 1 || p < (size_t)d) return 0;
                while (run--) { out[p] = out[p-(size_t)d]; p++; }
            }
            prev = out[p-1]; cache[hash(prev)] = prev;
            continue;
        } else if (t < 250) {
            if (!byte(r, &u)) return 0;
            v = fix(l2[(t - 246u) << 8 | u], ch);
        } else if (t < 254) {
            unsigned alpha, t2, a, b, val; int dg;
            if (ch == 3) return 0;
            if (t == 250) alpha = 0;
            else if (t == 251) alpha = 255;
            else if (t == 252) { if (p < w) return 0; alpha = out[p-w] >> 24; }
            else if (!byte(r, &alpha)) return 0;
            base = (base & 0xffffffu) | alpha << 24;
            if (!byte(r, &t2)) return 0;
            if (t2 >= 64 && t2 < 128) v = add(base, (int)((t2 >> 4) & 3) - 2, (int)((t2 >> 2) & 3) - 2, (int)(t2 & 3) - 2);
            else if (t2 >= 128 && t2 < 192) {
                dg = (int)(t2 & 63) - 32;
                if (!byte(r, &u)) return 0;
                v = add(base, dg + (int)(u >> 4) - 8, dg, dg + (int)(u & 15) - 8);
            } else if (t2 >= 192 && t2 < 200) {
                if (!byte(r, &a) || !byte(r, &b)) return 0;
                val = (t2 & 7u) << 16 | a << 8 | b; dg = (int)(val >> 12) - 64;
                v = add(base, dg + (int)((val >> 6) & 63) - 32, dg, dg + (int)(val & 63) - 32);
            } else if (t2 == 254) {
                if (r->n - r->at < 3) return 0;
                v = (base & 0xff000000u) | r->p[r->at] | (uint32_t)r->p[r->at+1] << 8 |
                    (uint32_t)r->p[r->at+2] << 16; r->at += 3;
            } else return 0;
            lit = r->at - op >= 3;
        } else if (t == 254) {
            if (r->n - r->at < 3) return 0;
            v = (base & 0xff000000u) | r->p[r->at] | (uint32_t)r->p[r->at+1] << 8 |
                (uint32_t)r->p[r->at+2] << 16; r->at += 3;
            lit = 1;
        } else {
            if (r->n - r->at < 4) return 0;
            v = fix(u32(r->p + r->at), ch); r->at += 4; lit = 1;
        }
        out[p++] = v; prev = v; cache[hash(v)] = v;
        if (lit) l2[l2slot(v)] = v;
    }
    return r->at == r->n;
}

static void colour(uint16_t *planes, size_t stride, unsigned count,
                   unsigned w, unsigned y, uint32_t *out, int r0) {
    unsigned x;
    for (x = 0; x < w; x++) {
        unsigned g = planes[x] & 255u;
        unsigned red = (planes[stride+x] + g + (r0 ? 128u : 0u)) & 255u;
        unsigned blue = (planes[2*stride+x] + ((red + g) >> 1) + (r0 ? 128u : 0u)) & 255u;
        unsigned alpha = count == 4 ? planes[3*stride+x] & 255u : 255u;
        out[(size_t)y*w+x] = red | g << 8 | blue << 16 | alpha << 24;
    }
}
static int nfor(const uint8_t *src, size_t len, unsigned w, unsigned rows,
                unsigned planes, uint32_t *out) {
    bits b = {src, len*8, 0};
    uint16_t *memory = (uint16_t *)calloc((size_t)planes*3*w, sizeof(uint16_t));
    uint16_t *cur, *prev, *z;
    unsigned y, g, p, i, wd, n0, count, v, nfull = w/8, tail = w%8;
    int ok = 0;
    if (!memory) return 0;
    cur = memory; prev = cur + (size_t)planes*w; z = prev + (size_t)planes*w;
    for (y = 0; y < rows; y++) {
        g = 0;
        while (g < nfull) {
            if (!readbits(&b, 4, &n0)) goto done;
            if (n0 == 9 || n0 == 10) {
                if (n0 == 9) count = 1;
                else { if (!readbits(&b, 4, &count)) goto done; count += 2; }
                if (count > nfull-g) goto done;
                for (p = 0; p < planes; p++) memset(z+(size_t)p*w+g*8, 0, count*8*sizeof(uint16_t));
                g += count; continue;
            }
            if (n0 > 8) goto done;
            for (p = 0; p < planes; p++) {
                wd = n0;
                if (p && !readbits(&b, 4, &wd)) goto done;
                if (wd > 8) goto done;
                for (i = 0; i < 8; i++) {
                    if (!readbits(&b, wd, &v)) goto done;
                    z[(size_t)p*w+g*8+i] = (uint16_t)v;
                }
            }
            g++;
        }
        if (tail) for (p = 0; p < planes; p++) {
            if (!readbits(&b, 4, &wd) || wd > 8) goto done;
            for (i = 0; i < tail; i++) {
                if (!readbits(&b, wd, &v)) goto done;
                z[(size_t)p*w+nfull*8+i] = (uint16_t)v;
            }
        }
        for (p = 0; p < planes; p++) for (i = 0; i < w; i++) {
            unsigned pred = y ? prev[(size_t)p*w+i] : i ? cur[(size_t)p*w+i-1] : 0;
            cur[(size_t)p*w+i] = (uint16_t)((pred + unzz(z[(size_t)p*w+i])) & 255);
        }
        colour(cur, w, planes, w, y, out, 0);
        memcpy(prev, cur, (size_t)planes*w*sizeof(uint16_t));
    }
    ok = bitdone(&b);
done:
    free(memory); return ok;
}

static int f0c(const uint8_t *src, size_t len, unsigned w, unsigned rows,
               unsigned planes, uint32_t *out) {
    bits b = {src, len*8, 0};
    uint16_t *v = (uint16_t *)calloc((size_t)planes*rows*w, sizeof(uint16_t));
    uint16_t lb[4] = {0}, up1[4] = {0};
    unsigned by, bx, p, x, y, n0, wd, zb, base, value;
    unsigned nbx = (w+3)/4, nfull = w/4;
    int ok = 0;
    if (!v) return 0;
    for (by = 0; by*4 < rows; by++) {
        unsigned y0 = by*4, chh = rows-y0 < 4 ? rows-y0 : 4;
        bx = 0;
        while (bx < nbx) {
            unsigned x0 = bx*4, cw = w-x0 < 4 ? w-x0 : 4;
            int full = chh == 4 && cw == 4;
            if (!readbits(&b, 4, &n0)) goto done;
            if (full && n0 >= 9) {
                unsigned count = 1, copy_up = n0 == 9 || n0 == 11;
                if (n0 > 12) goto done;
                if (n0 >= 11) { if (!readbits(&b, 4, &count)) goto done; count += 2; }
                if (count > nfull-bx || (copy_up && by == 0) || (!copy_up && bx == 0 && by == 0)) goto done;
                for (p = 0; p < planes; p++) {
                    size_t ps = (size_t)p*rows*w;
                    uint16_t ref = copy_up ? 0 : bx ? v[ps+(size_t)y0*w+x0-1] : v[ps+(size_t)(y0-1)*w+x0];
                    for (y = 0; y < 4; y++) for (x = x0; x < x0+4*count; x++)
                        v[ps+(size_t)(y0+y)*w+x] = copy_up ? v[ps+(size_t)(y0-4+y)*w+x] : ref;
                    lb[p] = v[ps+(size_t)y0*w+x0+4*(count-1)];
                    if (bx == 0) up1[p] = v[ps+(size_t)y0*w];
                }
                bx += count; continue;
            }
            for (p = 0; p < planes; p++) {
                size_t ps = (size_t)p*rows*w;
                wd = n0;
                if (p && !readbits(&b, 4, &wd)) goto done;
                if (wd > 8) goto done;
                base = 0;
                if (wd < 8) {
                    unsigned pred = bx ? lb[p] : by ? up1[p] : 0;
                    if (!eg0(&b, &zb) || zb > 255) goto done;
                    base = (pred + unzz(zb)) & 255u;
                }
                for (y = 0; y < chh; y++) for (x = 0; x < cw; x++) {
                    if (!readbits(&b, wd, &value)) goto done;
                    v[ps+(size_t)(y0+y)*w+x0+x] = (uint16_t)((base+value)&255u);
                }
                lb[p] = wd < 8 ? (uint16_t)base : v[ps+(size_t)y0*w+x0];
                if (bx == 0) up1[p] = lb[p];
            }
            bx++;
        }
    }
    for (y = 0; y < rows; y++) {
        unsigned xx;
        for (xx = 0; xx < w; xx++) {
            unsigned g = v[(size_t)y*w+xx] & 255u;
            unsigned red = (v[(size_t)rows*w+(size_t)y*w+xx]+g)&255u;
            unsigned blue = (v[2u*(size_t)rows*w+(size_t)y*w+xx]+((red+g)>>1))&255u;
            unsigned alpha = planes == 4 ? v[3u*(size_t)rows*w+(size_t)y*w+xx]&255u : 255u;
            out[(size_t)y*w+xx] = red | g<<8 | blue<<16 | alpha<<24;
        }
    }
    ok = bitdone(&b);
done:
    free(v); return ok;
}

static const unsigned thresholds[4][5] = {
    {6,13,30,70,157}, {7,18,35,86,213}, {7,17,35,82,209}, {8,16,32,64,128}
};
static int r0(const uint8_t *src, size_t len, unsigned w, unsigned rows,
              unsigned planes, uint32_t *out) {
    reader rd = {src,len,0};
    bits R, M, Q;
    uint16_t *z = NULL, *prev = NULL, *cur = NULL;
    uint8_t *skip = NULL;
    uint64_t le, lr, lm, xv;
    unsigned x0, plain, xbits, xbytes, masks, kplain[4] = {0};
    unsigned i, p, y, x, v, masked, q, k, j, act, zz;
    size_t e_start, ei = 0;
    int ok = 0;
    if (!byte(&rd, &x0)) return 0;
    plain = x0 & 1u; xbits = 1 + (plain ? 4*planes : 0);
    xbytes = (xbits+2+7)/8;
    xv = x0;
    for (i = 1; i < xbytes; i++) {
        if (!byte(&rd, &v)) return 0;
        xv |= (uint64_t)v << (8*i);
    }
    masks = (unsigned)((xv >> xbits) & 1u);
    if (xv >> (xbits+1)) return 0;
    if (plain) for (p = 0; p < planes; p++) {
        kplain[p] = (unsigned)((xv >> (1+4*p)) & 15u);
        if (kplain[p] > 8) return 0;
    }
    if (!var(&rd, 5, &le) || !var(&rd, 5, &lr)) return 0;
    lm = 0;
    if (masks && !var(&rd, 5, &lm)) return 0;
    if (le > rd.n-rd.at || lr > rd.n-rd.at-(size_t)le || lm > rd.n-rd.at-(size_t)le-(size_t)lr) return 0;
    e_start = rd.at;
    R = (bits){src+e_start+(size_t)le, (size_t)lr*8, 0};
    M = (bits){R.p+(size_t)lr, (size_t)lm*8, 0};
    Q = (bits){M.p+(size_t)lm, (rd.n-(size_t)(M.p+(size_t)lm-src))*8, 0};
    z = (uint16_t *)calloc((size_t)planes*3*w, sizeof(uint16_t));
    prev = (uint16_t *)calloc((size_t)planes*w, sizeof(uint16_t));
    cur = (uint16_t *)calloc((size_t)planes*w, sizeof(uint16_t));
    skip = (uint8_t *)malloc((w+7u)/8u);
    if (!z || !prev || !cur || !skip) goto done;
    for (y = 0; y < rows; y++) {
        for (p = 0; p < planes; p++) {
            uint16_t *zc = z+((size_t)p*3+y%3)*w;
            uint16_t *zn = y ? z+((size_t)p*3+(y-1)%3)*w : NULL;
            uint16_t *znn = y >= 2 ? z+((size_t)p*3+(y-2)%3)*w : zn;
            masked = 0;
            if (masks && !readbits(&M, 1, &masked)) goto done;
            memset(skip, 0, (w+7u)/8u);
            if (masked) for (i = 0; i < (w+7u)/8u; i++) {
                if (!readbits(&M, 1, &v)) goto done;
                skip[i] = (uint8_t)v;
            }
            for (x = 0; x < w; x++) {
                if (!y) k = plain ? kplain[p] : 0;
                else {
                    unsigned xl = x ? x-1 : 0, xr = x+1 < w ? x+1 : w-1;
                    act = 2*zn[x] + zn[xl] + zn[xr] + znn[x];
                    if (act > 255) act = 255;
                    k = 0;
                    for (j = 0; j < 5; j++) if (act >= thresholds[p][j]) k++;
                }
                if (skip[x>>3]) { zc[x] = 0; continue; }
                q = 0;
                do { if (!readbits(&Q, 1, &v)) goto done; if (!v && ++q > 16) goto done; } while (!v);
                if (q == 16) {
                    if (ei >= le) goto done;
                    q = src[e_start+ei++];
                    if (q < 16) goto done;
                }
                if (!readbits(&R, k, &v)) goto done;
                zz = (q << k) + v;
                if (zz > 255) goto done;
                zc[x] = (uint16_t)zz;
            }
            for (x = 0; x < w; x++) {
                unsigned N = prev[(size_t)p*w+x];
                unsigned W = x ? cur[(size_t)p*w+x-1] : N;
                unsigned NW = x ? prev[(size_t)p*w+x-1] : N;
                cur[(size_t)p*w+x] = (uint16_t)((med(W,N,NW)+unzz(zc[x]))&255);
            }
        }
        colour(cur,w,planes,w,y,out,1);
        { uint16_t *tmp = prev; prev = cur; cur = tmp; }
    }
    ok = ei == le && bitdone(&R) && bitdone(&M) && bitdone(&Q);
done:
    free(z); free(prev); free(cur); free(skip); return ok;
}

static size_t min_payload(unsigned codec, unsigned w, unsigned rows, unsigned planes) {
    uint64_t px = (uint64_t)w*rows;
    if (codec == 0) {
        uint64_t per = 1u+(w+7u)/8u;
        if (per > w) per = w;
        return (size_t)(3u+(rows*planes*per+7)/8);
    }
    if (codec == 4) {
        unsigned nfull = w/8, tail = w%8;
        uint64_t bits_row = 8u*(nfull/17)+(nfull%17 ? 4u : 0u)+(tail ? 4u*planes : 0u);
        return (size_t)((rows*bits_row+7)/8);
    }
    if (codec == 5) {
        unsigned nfb = w/4, nbx = (w+3)/4;
        uint64_t bits_band = 8u*(nfb/17)+(nfb%17 ? 4u : 0u)+(w%4 ? 5u*planes : 0u);
        return (size_t)(((rows/4)*bits_band+(rows%4 ? (uint64_t)nbx*5*planes : 0u)+7)/8);
    }
    if (codec == 6) return (size_t)((px+61)/62);
    return (size_t)((3*px+MAX_RUN-1)/MAX_RUN);
}

fic_status fic_decode_ex(const uint8_t *input, size_t input_length,
                         fic_decompress_fn decompress, void *context,
                         fic_image *image) {
    static const unsigned codec_slot[2][8] = {
        {4,5,1,6,255,255,255,255}, {0,1,2,3,6,255,255,255}
    };
    reader rd;
    const uint8_t *q;
    uint8_t *expanded = NULL;
    uint32_t palette[192] = {0}, width, height;
    uint8_t shorts[10] = {0};
    strip *strips = NULL;
    uint32_t *pixels = NULL;
    size_t prefix, exif_len, q_len, end, count, i, at, total_bytes, header_len;
    unsigned version, compression, flags, ch, planes, tier, rows, k = 0, t;
    uint64_t x, y;
    fic_status status = FIC_INVALID;
    if (!image) return FIC_INVALID;
    memset(image, 0, sizeof *image);
    if (!input || input_length < 9 || memcmp(input, "FIC\0", 4)) return FIC_INVALID;
    version = input[4];
    if (version > 2) return FIC_INVALID;
    prefix = version == 2 ? 14 : version == 1 ? 10 : 9;
    if (input_length < prefix) return FIC_INVALID;
    compression = version ? input[5] : 0;
    if (compression > (version == 2 ? 4u : 1u)) return FIC_INVALID;
    exif_len = u32(input+(version ? 6 : 5));
    if (exif_len > MAX_EXIF || exif_len > input_length-prefix) return FIC_INVALID;
    q = input+prefix+exif_len; q_len = input_length-prefix-exif_len;
    if (version == 2 && (u32(input+10) == 0 || u32(input+10) > (256u<<20) ||
                         (!compression && u32(input+10) != q_len))) return FIC_INVALID;
    if (compression) {
        size_t expected = version == 2 ? u32(input+10) : 0;
        if (!decompress) return FIC_UNSUPPORTED;
        status = decompress((uint8_t)compression,q,q_len,expected,&expanded,&q_len,context);
        if (status != FIC_OK) { free(expanded); return status; }
        if (!expanded || q_len > (256u<<20) || (expected && q_len != expected)) {
            free(expanded); return FIC_INVALID;
        }
        q = expanded;
        status = FIC_INVALID;
    }
    if (q_len < 12 || memcmp(q,"FICQ",4) || q[4] != 2 || q[5] > 1) { free(expanded); return FIC_INVALID; }
    tier = q[5]; end = q_len-4;
    rd = (reader){q,end,6};
    if (!var(&rd,5,&x) || !var(&rd,5,&y) || !x || !y || x > MAX_DIM || y > MAX_DIM || x*y > MAX_PIXELS) goto done;
    width = (uint32_t)x; height = (uint32_t)y;
    if (!byte(&rd,&flags) || (flags & ~7u) || ((flags&2u) && !(flags&1u)) || ((flags&4u) && tier != 1)) goto done;
    ch = (flags&1u) ? 4 : 3; planes = (flags&2u) ? 4 : 3;
    if (!var(&rd,5,&x) || x < (height < (4096u+width-1)/width ? height : (4096u+width-1)/width) || x > height) goto done;
    rows = (unsigned)x;
    if (flags&4u) {
        if (!byte(&rd,&t)) goto done;
        k = t+1;
        if (k > 192 || (size_t)k*ch+10 > rd.n-rd.at) goto done;
        for (i = 0; i < k; i++) { palette[i] = color_at(rd.p+rd.at,ch); rd.at += ch; }
        memcpy(shorts,rd.p+rd.at,10); rd.at += 10;
        t = k;
        for (i = 0; i < 10; i++) t += shorts[i]+1u;
        if (t > 256) goto done;
    }
    header_len = rd.at;
    count = ((size_t)height+rows-1)/rows;
    if (count > (rd.n-rd.at)/2) goto done;
    if ((uint64_t)width*height*ch > SIZE_MAX || (uint64_t)width*height > SIZE_MAX/sizeof(uint32_t)) {
        status = FIC_LIMIT; goto done;
    }
    total_bytes = (size_t)width*height*ch;
    strips = (strip *)calloc(count,sizeof *strips);
    pixels = (uint32_t *)malloc((size_t)width*height*sizeof(uint32_t));
    if (!strips || !pixels) { status = FIC_NOMEM; goto done; }
    for (i = 0; i < count; i++) {
        uint32_t strip_rows = height-(uint32_t)i*rows;
        if (strip_rows > rows) strip_rows = rows;
        if (!var(&rd,5,&x)) goto done;
        t = codec_slot[tier][x&7u];
        if (t == 255 || (t == 3 && !k) || ((t == 0 || t == 4 || t == 5) && (width < 16 || width > 65536))) goto done;
        y = x >> 3;
        if (y > SIZE_MAX || y < min_payload(t,width,strip_rows,planes)) goto done;
        strips[i] = (strip){NULL,(size_t)y,strip_rows,t};
    }
    at = rd.at;
    for (i = 0; i < count; i++) {
        if (strips[i].len > end-at) goto done;
        strips[i].p = q+at; at += strips[i].len;
    }
    if (at != end) goto done;
    for (i = 0; i < count; i++) {
        strip *s = strips+i;
        uint32_t *dst = pixels+(size_t)i*rows*width;
        size_t n = (size_t)s->rows*width;
        reader sr = {s->p,s->len,0};
        int ok;
        switch (s->codec) {
            case 0: ok = r0(s->p,s->len,width,s->rows,planes,dst); break;
            case 1: ok = qp(&sr,n,width,ch,0,dst); break;
            case 2: ok = qp(&sr,n,width,ch,1,dst); break;
            case 3: ok = palette_strip(&sr,n,width,palette,k,shorts,dst); break;
            case 4: ok = nfor(s->p,s->len,width,s->rows,planes,dst); break;
            case 5: ok = f0c(s->p,s->len,width,s->rows,planes,dst); break;
            default: ok = literal(&sr,n,ch,dst); break;
        }
        if (!ok) goto done;
    }
    image->pixels = (uint8_t *)malloc(total_bytes);
    if (!image->pixels) { status = FIC_NOMEM; goto done; }
    for (i = 0; i < (size_t)width*height; i++) pixel_to_bytes(pixels[i],image->pixels+i*ch,ch);
    if (crc32c(image->pixels,total_bytes,crc32c(q,header_len,0)) != u32(q+end)) goto done;
    image->width = width; image->height = height; image->channels = (uint8_t)ch;
    image->tier = (uint8_t)tier; image->exif = input+prefix;
    image->exif_length = exif_len; image->pixels_length = total_bytes;
    status = FIC_OK;
done:
    free(strips); free(pixels); free(expanded);
    if (status != FIC_OK) fic_image_free(image);
    return status;
}

fic_status fic_decode(const uint8_t *input, size_t input_length, fic_image *image) {
    return fic_decode_ex(input,input_length,NULL,NULL,image);
}

void fic_image_free(fic_image *image) {
    if (image) { free(image->pixels); memset(image,0,sizeof *image); }
}
const char *fic_status_string(fic_status status) {
    switch (status) {
        case FIC_OK: return "ok";
        case FIC_INVALID: return "invalid FIC data";
        case FIC_LIMIT: return "image exceeds implementation limit";
        case FIC_NOMEM: return "out of memory";
        case FIC_UNSUPPORTED: return "outer compression is unsupported";
        default: return "unknown error";
    }
}
