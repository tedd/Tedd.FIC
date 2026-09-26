/* Source for the checked x86/x64 MASM listings. Keep this file in sync with
 * the generated listings; build.ps1 regenerates and assembles both variants. */
#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include "fic.h"

#define MAX_DIM 16777216u
#define MAX_PIXELS 536870912u
#define MAX_EXIF 16777216u
#define MAX_EXPANDED 268435456u
#define MAX_RUN 1024u

typedef struct reader { const uint8_t *p, *end; int error; } reader;
typedef struct bits { const uint8_t *p; uint64_t used, size; int error; } bits;
typedef struct strip { uint32_t len, slot, rows; } strip;

static uint32_t u32(const uint8_t *p) {
    return (uint32_t)p[0] | (uint32_t)p[1]<<8 | (uint32_t)p[2]<<16 | (uint32_t)p[3]<<24;
}
static void put32(uint8_t *p, uint32_t v) {
    p[0]=(uint8_t)v; p[1]=(uint8_t)(v>>8); p[2]=(uint8_t)(v>>16); p[3]=(uint8_t)(v>>24);
}
static uint32_t readbyte(reader *r) {
    if (r->p == r->end) { r->error=1; return 0; }
    return *r->p++;
}
static uint32_t read24(reader *r) {
    uint32_t a=readbyte(r), b=readbyte(r), c=readbyte(r);
    return a|(b<<8)|(c<<16);
}
static uint32_t read32(reader *r) {
    uint32_t a=readbyte(r), b=readbyte(r), c=readbyte(r), d=readbyte(r);
    return a|(b<<8)|(c<<16)|(d<<24);
}
static uint64_t varint(reader *r) {
    uint64_t v=0;
    for (unsigned i=0;i<5;i++) {
        unsigned b=readbyte(r);
        if (r->error) return UINT64_MAX;
        v |= (uint64_t)(b&127)<<(7*i);
        if (!(b&128)) return (i && !b) ? UINT64_MAX : v;
    }
    return UINT64_MAX;
}
static unsigned varsize(uint64_t v) {
    unsigned n=1;
    while (v>=128) { v>>=7; n++; }
    return n;
}
static uint8_t *putvar(uint8_t *p, uint64_t v) {
    while (v>=128) { *p++=(uint8_t)(v|128); v>>=7; }
    *p++=(uint8_t)v;
    return p;
}
static unsigned bitread(bits *b, unsigned n) {
    unsigned v=0;
    if (n>24 || b->used>b->size || n>b->size-b->used) { b->error=1; return 0; }
    for (unsigned i=0;i<n;i++,b->used++) v|=((b->p[b->used>>3]>>(b->used&7))&1u)<<i;
    return v;
}
static unsigned eg0(bits *b) {
    unsigned t=0;
    while (!bitread(b,1) && !b->error) if (++t>8) { b->error=1; return 0; }
    return ((1u<<t)-1)+bitread(b,t);
}
static int bitsdone(bits *b) {
    if (b->error || b->size-b->used>=8) return 0;
    while (b->used<b->size) if (bitread(b,1)) return 0;
    return !b->error;
}
static uint32_t crc32c(uint32_t seed, const uint8_t *p, size_t n) {
    uint32_t c=~seed;
    for (size_t i=0;i<n;i++) {
        c^=p[i];
        for (unsigned k=0;k<8;k++) c=(c>>1)^((c&1)?0x82f63b78u:0);
    }
    return ~c;
}
static unsigned hash(uint32_t v) {
    return ((v&255)*3+((v>>8)&255)*5+((v>>16)&255)*7+(v>>24)*11)&63;
}
static unsigned l2slot(uint32_t v) { return (v*0x9e3779b1u)>>22; }
static uint32_t add_colour(uint32_t b, int r, int g, int bl) {
    return (((b&255)+r)&255) | (((((b>>8)&255)+g)&255)<<8)
        | (((((b>>16)&255)+bl)&255)<<16) | (b&0xff000000u);
}
static unsigned med(unsigned a, unsigned b, unsigned c) {
    unsigned lo=a<b?a:b, hi=a>b?a:b;
    int x=(int)a+(int)b-(int)c;
    return x<(int)lo?lo:x>(int)hi?hi:(unsigned)x;
}
static uint32_t medpx(uint32_t a, uint32_t b, uint32_t c) {
    uint32_t v=0;
    for (unsigned s=0;s<32;s+=8) v|=med((a>>s)&255,(b>>s)&255,(c>>s)&255)<<s;
    return v;
}
static int unzz(unsigned z) { return (int)(z>>1)^-(int)(z&1); }

static int literal(reader *r, uint32_t *out, size_t n, unsigned ch) {
    uint32_t cache[64]={0}, prev=0xff000000u;
    size_t p=0;
    while (p<n && !r->error) {
        unsigned t=readbyte(r), run=1;
        if (t<64) { prev=cache[t]; if (ch==3) prev|=0xff000000u; }
        else if (t<128) prev=add_colour(prev,((t>>4)&3)-2,((t>>2)&3)-2,(t&3)-2);
        else if (t<192) {
            int dg=(int)(t&63)-32; unsigned u=readbyte(r);
            prev=add_colour(prev,dg+(int)(u>>4)-8,dg,dg+(int)(u&15)-8);
        } else if (t<254) run=(t&63)+1;
        else if (t==254) prev=(prev&0xff000000u)|read24(r);
        else {
            if (ch==3) return 0;
            prev=read32(r);
        }
        if (r->error || run>n-p) return 0;
        cache[hash(prev)]=prev;
        while (run--) out[p++]=prev;
    }
    return !r->error && r->p==r->end;
}

static const int copies[9][2]={{1,0},{1,1},{1,-1},{2,0},{0,-2},{0,-3},{0,-4},{1,-2},{1,2}};
static const unsigned gmshorts[9]={6,3,3,3,2,2,3,3,2};
typedef struct group { uint8_t cls, shortlen, count, valid; } group;
static void groupadd_colour(group *g, unsigned *code, unsigned cls, unsigned count) {
    for (unsigned i=1;i<=count;i++) { g[*code].cls=(uint8_t)cls; g[*code].shortlen=(uint8_t)i;
        g[*code].count=(uint8_t)count; g[(*code)++].valid=1; }
    g[*code].cls=(uint8_t)cls; g[*code].shortlen=0;
    g[*code].count=(uint8_t)count; g[(*code)++].valid=1;
}
static int qpmode(reader *r, uint32_t *out, size_t n, unsigned w, unsigned ch, int gm) {
    group groups[256]={0}; uint32_t cache[64]={0}, l2[1024]={0};
    unsigned code=200;
    groupadd_colour(groups,&code,0,gm?5:18);
    if (gm) groupadd_colour(groups,&code,1,3);
    for (unsigned j=0;j<(gm?9u:1u);j++) groupadd_colour(groups,&code,j+2,gm?gmshorts[j]:26);
    uint32_t prev=0xff000000u;
    size_t p=0;
    while (p<n && !r->error) {
        unsigned t=readbyte(r); const uint8_t *op=r->p-1;
        uint32_t base=gm && p>=w+1?medpx(out[p-1],out[p-w],out[p-w-1]):prev;
        if (ch==3) base|=0xff000000u;
        uint32_t v=0; int lit=0;
        if (t<64) v=cache[t];
        else if (t<128) v=add_colour(base,((t>>4)&3)-2,((t>>2)&3)-2,(t&3)-2);
        else if (t<192) { int dg=(int)(t&63)-32; unsigned u=readbyte(r);
            v=add_colour(base,dg+(int)(u>>4)-8,dg,dg+(int)(u&15)-8); }
        else if (t<200) { unsigned hi=readbyte(r), lo=readbyte(r); unsigned x=((t&7)<<16)|(hi<<8)|lo; int dg=(int)(x>>12)-64;
            v=add_colour(base,dg+(int)((x>>6)&63)-32,dg,dg+(int)(x&63)-32); lit=1; }
        else if (t<246) {
            group g=groups[t]; if (!g.valid) return 0;
            uint64_t extra=g.shortlen?0:varint(r);
            if (extra==UINT64_MAX) return 0;
            uint64_t run=g.shortlen?g.shortlen:(uint64_t)g.count+1+extra;
            if (r->error || run>MAX_RUN || run>n-p) return 0;
            if (g.cls==0) { while (run--) out[p++]=prev; }
            else if (g.cls==1) { while (run--) {
                prev=gm && p>=w+1?medpx(out[p-1],out[p-w],out[p-w-1]):prev;
                if (ch==3) prev|=0xff000000u;
                out[p++]=prev;
            } } else {
                unsigned j=g.cls-2; int dist=copies[j][0]*(int)w-copies[j][1];
                if (dist<1 || p<(size_t)dist) return 0;
                while (run--) { out[p]=out[p-dist]; p++; }
            }
            prev=out[p-1]; cache[hash(prev)]=prev; continue;
        } else if (t<250) v=l2[((t-246)<<8)|readbyte(r)];
        else if (t<254) {
            if (ch==3) return 0;
            unsigned a=t==250?0:t==251?255:t==252?(p>=w?out[p-w]>>24:256):readbyte(r);
            if (a>255) return 0;
            uint32_t b=(base&0x00ffffffu)|(a<<24); unsigned t2=readbyte(r);
            if (t2>=64 && t2<128) v=add_colour(b,((t2>>4)&3)-2,((t2>>2)&3)-2,(t2&3)-2);
            else if (t2>=128 && t2<192) { int dg=(int)(t2&63)-32; unsigned u=readbyte(r);
                v=add_colour(b,dg+(int)(u>>4)-8,dg,dg+(int)(u&15)-8); }
            else if (t2>=192 && t2<200) { unsigned hi=readbyte(r), lo=readbyte(r); unsigned x=((t2&7)<<16)|(hi<<8)|lo;
                int dg=(int)(x>>12)-64; v=add_colour(b,dg+(int)((x>>6)&63)-32,dg,dg+(int)(x&63)-32); }
            else if (t2==254) v=(b&0xff000000u)|read24(r);
            else return 0;
            lit=(size_t)(r->p-op)>=3;
        } else if (t==254) { v=(base&0xff000000u)|read24(r); lit=1; }
        else { v=read32(r); lit=1; }
        if (r->error) return 0;
        if (ch==3) v|=0xff000000u;
        out[p++]=prev=v; cache[hash(v)]=v;
        if (lit) l2[l2slot(v)]=v;
    }
    return !r->error && r->p==r->end;
}

static int palette(reader *r, uint32_t *out, size_t n, unsigned w,
    const uint32_t *colours, unsigned k, const uint8_t *shorts) {
    group groups[256]={0}; unsigned code=k;
    for (unsigned cls=0;cls<10;cls++) groupadd_colour(groups,&code,cls,shorts[cls]);
    uint32_t prev=colours[0]; size_t p=0;
    while (p<n && !r->error) {
        unsigned t=readbyte(r);
        if (t<k) { out[p++]=prev=colours[t]; continue; }
        group g=groups[t]; if (!g.valid) return 0;
        uint64_t extra=g.shortlen?0:varint(r);
        if (extra==UINT64_MAX) return 0;
        uint64_t run=g.shortlen?g.shortlen:(uint64_t)g.count+1+extra;
        if (r->error || run>MAX_RUN || run>n-p) return 0;
        if (!g.cls) { while (run--) out[p++]=prev; }
        else {
            unsigned j=g.cls-1; int dist=copies[j][0]*(int)w-copies[j][1];
            if (dist<1 || p<(size_t)dist) return 0;
            while (run--) { out[p]=out[p-dist]; p++; }
            prev=out[p-1];
        }
    }
    return !r->error && r->p==r->end;
}

static void colour(const uint16_t *planes, unsigned w, unsigned count,
    unsigned stride, uint32_t *out) {
    for (unsigned x=0;x<w;x++) {
        unsigned g=planes[x], r=(planes[stride+x]+g)&255;
        unsigned b=(planes[2*stride+x]+((r+g)>>1))&255;
        unsigned a=count==4?planes[3*stride+x]:255;
        out[x]=r|(g<<8)|(b<<16)|(a<<24);
    }
}
static int nfor(const uint8_t *data, size_t len, unsigned w, unsigned rows,
    unsigned count, uint32_t *out) {
    size_t cells=(size_t)4*w;
    uint16_t *cur=(uint16_t*)calloc(cells*3,sizeof(uint16_t));
    if (!cur) return 0;
    uint16_t *prev=cur+cells, *z=prev+cells;
    bits b={data,0,(uint64_t)len*8,0};
    unsigned full=w/8, tail=w%8;
    for (unsigned y=0;y<rows && !b.error;y++) {
        unsigned g=0;
        while (g<full && !b.error) {
            unsigned n0=bitread(&b,4);
            if (n0==9 || n0==10) {
                unsigned count0=n0==9?1:bitread(&b,4)+2;
                if (g+count0>full) { b.error=1; break; }
                for (unsigned p=0;p<count;p++)
                    memset(z+(size_t)p*w+(size_t)g*8,0,(size_t)count0*8*sizeof(uint16_t));
                g+=count0; continue;
            }
            if (n0>8) { b.error=1; break; }
            for (unsigned p=0;p<count;p++) {
                unsigned wd=p?bitread(&b,4):n0;
                if (wd>8) { b.error=1; break; }
                for (unsigned i=0;i<8;i++) z[(size_t)p*w+(size_t)g*8+i]=(uint16_t)bitread(&b,wd);
            }
            g++;
        }
        if (tail) for (unsigned p=0;p<count && !b.error;p++) {
            unsigned wd=bitread(&b,4);
            if (wd>8) { b.error=1; break; }
            for (unsigned i=0;i<tail;i++) z[(size_t)p*w+(size_t)full*8+i]=(uint16_t)bitread(&b,wd);
        }
        for (unsigned p=0;p<count;p++) for (unsigned x=0;x<w;x++) {
            unsigned pred=y?prev[(size_t)p*w+x]:x?cur[(size_t)p*w+x-1]:0;
            cur[(size_t)p*w+x]=(uint16_t)((pred+unzz(z[(size_t)p*w+x]))&255);
        }
        colour(cur,w,count,w,out+(size_t)y*w);
        uint16_t *tmp=cur; cur=prev; prev=tmp;
    }
    int ok=bitsdone(&b);
    free(cur<prev?cur:prev);
    return ok;
}

static int f0c(const uint8_t *data, size_t len, unsigned w, unsigned rows,
    unsigned count, uint32_t *out) {
    size_t per=(size_t)w*rows;
    if (per>SIZE_MAX/(4*sizeof(uint16_t))) return 0;
    uint16_t *v=(uint16_t*)calloc(4*per,sizeof(uint16_t));
    if (!v) return 0;
    bits b={data,0,(uint64_t)len*8,0};
    unsigned lb[4]={0}, up1[4]={0};
    unsigned nbx=(w+3)/4, nfull=w/4;
    for (unsigned by=0;by*4<rows && !b.error;by++) {
        unsigned y0=by*4, chh=rows-y0<4?rows-y0:4, bx=0;
        while (bx<nbx && !b.error) {
            unsigned x0=bx*4, cw=w-x0<4?w-x0:4;
            int full=chh==4 && cw==4;
            unsigned n0=bitread(&b,4);
            if (full && n0>=9) {
                if (n0>12) { b.error=1; break; }
                unsigned repeat=n0>=11?bitread(&b,4)+2:1;
                if (bx+repeat>nfull) { b.error=1; break; }
                int copyup=n0==9 || n0==11;
                if ((copyup && !by) || (!copyup && !bx && !by)) { b.error=1; break; }
                for (unsigned p=0;p<count;p++) {
                    uint16_t *plane=v+(size_t)p*per;
                    unsigned ref=copyup?0:bx?plane[(size_t)y0*w+x0-1]:plane[(size_t)(y0-1)*w+x0];
                    for (unsigned r=0;r<4;r++) for (unsigned x=x0;x<x0+4*repeat;x++)
                        plane[(size_t)(y0+r)*w+x]=(uint16_t)(copyup?plane[(size_t)(y0-4+r)*w+x]:ref);
                    lb[p]=plane[(size_t)y0*w+x0+4*(repeat-1)];
                    if (!bx) up1[p]=plane[(size_t)y0*w];
                }
                bx+=repeat; continue;
            }
            for (unsigned p=0;p<count;p++) {
                unsigned wd=p?bitread(&b,4):n0;
                if (wd>8) { b.error=1; break; }
                unsigned base=0;
                if (wd<8) {
                    unsigned pred=bx?lb[p]:by?up1[p]:0, zb=eg0(&b);
                    if (zb>255) { b.error=1; break; }
                    base=(pred+unzz(zb))&255;
                }
                uint16_t *plane=v+(size_t)p*per;
                for (unsigned r=0;r<chh;r++) for (unsigned x=0;x<cw;x++)
                    plane[(size_t)(y0+r)*w+x0+x]=(uint16_t)((base+bitread(&b,wd))&255);
                unsigned newbase=wd<8?base:plane[(size_t)y0*w+x0];
                lb[p]=newbase;
                if (!bx) up1[p]=newbase;
            }
            bx++;
        }
    }
    if (!b.error) for (unsigned y=0;y<rows;y++) {
        uint16_t *row=(uint16_t*)malloc((size_t)4*w*sizeof(uint16_t));
        if (!row) { b.error=1; break; }
        for (unsigned p=0;p<count;p++) memcpy(row+(size_t)p*w,v+(size_t)p*per+(size_t)y*w,(size_t)w*sizeof(uint16_t));
        colour(row,w,count,w,out+(size_t)y*w);
        free(row);
    }
    int ok=bitsdone(&b);
    free(v);
    return ok;
}

static const unsigned thresholds[4][5]={{6,13,30,70,157},{7,18,35,86,213},
    {7,17,35,82,209},{8,16,32,64,128}};
static int r0(const uint8_t *data, size_t len, unsigned w, unsigned rows,
    unsigned count, uint32_t *out) {
    reader rd={data,data+len,0};
    unsigned x0=readbyte(&rd), plain=x0&1, xbits=1+(plain?4*count:0), xbytes=(xbits+1+7)/8;
    uint32_t xv=x0;
    for (unsigned i=1;i<xbytes;i++) xv|=readbyte(&rd)<<(8*i);
    unsigned masks=(xv>>xbits)&1;
    if (rd.error || (xv>>(xbits+1))) return 0;
    unsigned kplain[4]={0};
    if (plain) for (unsigned p=0;p<count;p++) {
        kplain[p]=(xv>>(1+4*p))&15;
        if (kplain[p]>8) return 0;
    }
    uint64_t le=varint(&rd), lr=varint(&rd), lm=masks?varint(&rd):0;
    if (rd.error || le>len || lr>len-le || lm>len-le-lr ||
        le+lr+lm>(size_t)(rd.end-rd.p)) return 0;
    const uint8_t *ex=rd.p, *rp=ex+le, *mp=rp+lr, *qp=mp+lm;
    bits R={rp,0,lr*8,0}, M={mp,0,lm*8,0}, Q={qp,0,(uint64_t)(rd.end-qp)*8,0};
    size_t ei=0, per=(size_t)w*count;
    uint16_t *buf=(uint16_t*)calloc(per*5,sizeof(uint16_t));
    uint8_t *skip=(uint8_t*)malloc((w+7)/8);
    if (!buf || !skip) { free(buf); free(skip); return 0; }
    uint16_t *z=buf, *prev=buf+3*per, *cur=buf+4*per;
    for (unsigned y=0;y<rows && !Q.error;y++) {
        for (unsigned p=0;p<count && !Q.error;p++) {
            uint16_t *zc=z+((size_t)(y%3)*count+p)*w;
            uint16_t *zn=y?z+((size_t)((y-1)%3)*count+p)*w:0;
            uint16_t *znn=y>=2?z+((size_t)((y-2)%3)*count+p)*w:zn;
            int masked=masks && bitread(&M,1);
            memset(skip,0,(w+7)/8);
            if (masked) for (unsigned g=0;g<(w+7)/8;g++) skip[g]=(uint8_t)bitread(&M,1);
            for (unsigned x=0;x<w && !Q.error;x++) {
                unsigned k=0;
                if (!y) k=plain?kplain[p]:0;
                else {
                    unsigned xm=x?x-1:0, xp=x+1<w?x+1:w-1;
                    unsigned act=2*zn[x]+zn[xm]+zn[xp]+znn[x];
                    if (act>255) act=255;
                    for (unsigned t=0;t<5;t++) if (act>=thresholds[p][t]) k++;
                }
                if (skip[x>>3]) { zc[x]=0; continue; }
                unsigned q=0;
                while (!bitread(&Q,1) && !Q.error) if (++q>16) { Q.error=1; break; }
                if (q==16) { if (ei>=le) { Q.error=1; break; } q=ex[ei++]; if (q<16) { Q.error=1; break; } }
                unsigned zz=q*(1u<<k)+bitread(&R,k);
                if (zz>255) { Q.error=1; break; }
                zc[x]=(uint16_t)zz;
            }
            for (unsigned x=0;x<w;x++) {
                unsigned N=prev[(size_t)p*w+x], W=x?cur[(size_t)p*w+x-1]:N;
                unsigned NW=x?prev[(size_t)p*w+x-1]:N;
                cur[(size_t)p*w+x]=(uint16_t)((med(W,N,NW)+unzz(zc[x]))&255);
            }
        }
        for (unsigned x=0;x<w;x++) {
            unsigned g=cur[x], rv=(cur[w+x]+g+128)&255;
            unsigned bl=(cur[2*w+x]+((rv+g)>>1)+128)&255;
            unsigned a=count==4?cur[3*w+x]:255;
            out[(size_t)y*w+x]=rv|(g<<8)|(bl<<16)|(a<<24);
        }
        uint16_t *tmp=cur; cur=prev; prev=tmp;
    }
    int ok=ei==le && bitsdone(&R) && bitsdone(&M) && bitsdone(&Q);
    free(buf); free(skip);
    return ok;
}

/* A null destination asks the callback for the exact expanded length. */
int __cdecl fic_decode(const uint8_t *source, uint32_t length,
    uint8_t *destination, uint32_t capacity, fic_info *info,
    fic_decompress_fn decompress, void *context) {
    uint8_t *owned=0; uint32_t *pixels=0; strip *strips=0;
    int ok=0;
    if (info) memset(info,0,sizeof(*info));
    if (!source || !info || length<9 || u32(source)!=0x00434946u) return 0;
    unsigned version=source[4], prefix=0, codec=0;
    if (!version) prefix=9;
    else if (version==1 && length>=10 && source[5]<=1) { prefix=10; codec=source[5]?3:0; }
    else if (version==2 && length>=14 && source[5]<=4) { prefix=14; codec=source[5]; }
    else return 0;
    uint32_t exiflen=u32(source+(version?6:5));
    if (exiflen>MAX_EXIF || exiflen>length-prefix) return 0;
    const uint8_t *exif=source+prefix, *q=exif+exiflen;
    uint32_t qlen=length-prefix-exiflen;
    if (version==2) {
        uint32_t expected=u32(source+10);
        if (!expected || expected>MAX_EXPANDED) return 0;
        if (!codec && qlen!=expected) return 0;
    }
    if (codec) {
        if (!decompress) return 0;
        uint32_t expanded=version==2?u32(source+10):0;
        if (version==1 && (!decompress(codec,q,qlen,0,0,&expanded,context) ||
            !expanded || expanded>MAX_EXPANDED)) return 0;
        owned=(uint8_t*)malloc(expanded);
        if (!owned) return 0;
        uint32_t actual=expanded;
        if (!decompress(codec,q,qlen,owned,expanded,&actual,context) || actual!=expanded) goto done;
        q=owned; qlen=expanded;
    }
    if (qlen<12 || u32(q)!=0x51434946u || q[4]!=2 || q[5]>1) goto done;
    unsigned tier=q[5];
    reader head={q+6,q+qlen-4,0};
    uint64_t w0=varint(&head), h0=varint(&head);
    if (head.error || w0<1 || h0<1 || w0>MAX_DIM || h0>MAX_DIM ||
        w0*h0>MAX_PIXELS || head.p==head.end) goto done;
    unsigned w=(unsigned)w0, h=(unsigned)h0, flags=readbyte(&head);
    if (flags&~7u || ((flags&2u) && !(flags&1u)) || ((flags&4u) && tier!=1)) goto done;
    unsigned ch=(flags&1)?4:3, planes=(flags&2)?4:3;
    uint64_t rows0=varint(&head);
    unsigned minrows=(unsigned)((4096u+w-1)/w);
    if (minrows>h) minrows=h;
    if (head.error || rows0<minrows || rows0>h) goto done;
    unsigned rows=(unsigned)rows0, n=(h+rows-1)/rows;
    uint32_t palettecolours[192]={0}; uint8_t shorts[10]={0}; unsigned k=0;
    if (flags&4) {
        k=readbyte(&head)+1;
        if (head.error || k>192 || (size_t)(head.end-head.p)<(size_t)k*ch+10) goto done;
        for (unsigned i=0;i<k;i++) {
            uint32_t c=ch==3?0xff000000u:0;
            for (unsigned b=0;b<ch;b++) c|=(uint32_t)readbyte(&head)<<(8*b);
            palettecolours[i]=c;
        }
        unsigned code=k;
        for (unsigned i=0;i<10;i++) { shorts[i]=(uint8_t)readbyte(&head); code+=shorts[i]+1; }
        if (code>256) goto done;
    }
    size_t headerlen=(size_t)(head.p-q);
    if (n>(size_t)(head.end-head.p)/2) goto done;
    strips=(strip*)calloc(n,sizeof(strip));
    if (!strips) goto done;
    uint64_t total=0;
    for (unsigned i=0;i<n;i++) {
        uint64_t entry=varint(&head), slen=entry>>3;
        unsigned slot=(unsigned)(entry&7);
        if (head.error || entry==UINT64_MAX || slen>UINT32_MAX ||
            (tier==0?slot>3:slot>4) || (tier==1 && slot==3 && !k)) goto done;
        if ((tier==0 && slot<2) || (tier==1 && slot==0))
            if (w<16 || w>65536) goto done;
        unsigned srows=h-i*rows<rows?h-i*rows:rows;
        uint64_t px=(uint64_t)w*srows;
        if ((tier==0 && slot==3) || (tier==1 && slot==4))
            if (slen<(px+61)/62) goto done;
        strips[i].len=(uint32_t)slen; strips[i].slot=slot; strips[i].rows=srows;
        total+=slen;
    }
    if (total!=(uint64_t)(head.end-head.p)) goto done;
    uint64_t outputbytes=w0*h0*ch;
    if (!destination) {
        if (capacity) goto done;
        info->width=w; info->height=h; info->channels=ch; info->tier=tier;
        info->exif=exif; info->exif_length=exiflen;
        ok=1;
        goto done;
    }
    if (outputbytes>capacity || outputbytes>UINT32_MAX || w0*h0>SIZE_MAX/sizeof(uint32_t)) goto done;
    pixels=(uint32_t*)malloc((size_t)(w0*h0)*sizeof(uint32_t));
    if (!pixels) goto done;
    const uint8_t *payload=head.p;
    size_t offset=0, pos=0;
    for (unsigned i=0;i<n;i++) {
        reader sr={payload+offset,payload+offset+strips[i].len,0};
        size_t count=(size_t)w*strips[i].rows;
        unsigned slot=strips[i].slot;
        int decoded=0;
        if ((tier==0 && slot==3) || (tier==1 && slot==4))
            decoded=literal(&sr,pixels+pos,count,ch);
        else if (tier==0 && slot==0)
            decoded=nfor(sr.p,strips[i].len,w,strips[i].rows,planes,pixels+pos);
        else if (tier==0 && slot==1)
            decoded=f0c(sr.p,strips[i].len,w,strips[i].rows,planes,pixels+pos);
        else if (tier==1 && slot==0)
            decoded=r0(sr.p,strips[i].len,w,strips[i].rows,planes,pixels+pos);
        else if (tier==1 && slot==3)
            decoded=palette(&sr,pixels+pos,count,w,palettecolours,k,shorts);
        else decoded=qpmode(&sr,pixels+pos,count,w,ch,tier==1 && slot==2);
        if (!decoded) goto done;
        offset+=strips[i].len; pos+=count;
    }
    for (size_t i=0,j=0;i<(size_t)(w0*h0);i++) {
        uint32_t v=pixels[i]; destination[j++]=(uint8_t)v;
        destination[j++]=(uint8_t)(v>>8); destination[j++]=(uint8_t)(v>>16);
        if (ch==4) destination[j++]=(uint8_t)(v>>24);
    }
    uint32_t crc=crc32c(0,q,headerlen);
    crc=crc32c(crc,destination,(size_t)outputbytes);
    if (crc!=u32(q+qlen-4)) goto done;
    info->width=w; info->height=h; info->channels=ch; info->tier=tier;
    info->exif=exif; info->exif_length=exiflen;
    ok=1;
done:
    free(pixels); free(strips); free(owned);
    return ok;
}

static uint32_t loadpixel(const uint8_t *src, size_t i, unsigned ch) {
    const uint8_t *p=src+i*ch;
    return (uint32_t)p[0]|(uint32_t)p[1]<<8|(uint32_t)p[2]<<16|
        (ch==4?(uint32_t)p[3]<<24:0xff000000u);
}
static size_t encodeliteral(const uint8_t *src, size_t n, unsigned ch, uint8_t *dst) {
    uint32_t prev=0xff000000u;
    size_t written=0; unsigned run=0;
    for (size_t i=0;i<n;i++) {
        uint32_t v=loadpixel(src,i,ch);
        if (v==prev) {
            if (++run==62 || i==n-1) { if (dst) dst[written]=(uint8_t)(0xc0|(run-1)); written++; run=0; }
            continue;
        }
        if (run) { if (dst) dst[written]=(uint8_t)(0xc0|(run-1)); written++; run=0; }
        if (ch==4 && (v^prev)&0xff000000u) {
            if (dst) { dst[written]=255; put32(dst+written+1,v); }
            written+=5;
        } else {
            if (dst) { dst[written]=254; dst[written+1]=(uint8_t)v;
                dst[written+2]=(uint8_t)(v>>8); dst[written+3]=(uint8_t)(v>>16); }
            written+=4;
        }
        prev=v;
    }
    return written;
}

int __cdecl fic_encode(const uint8_t *pixels, uint32_t width,
    uint32_t height, uint32_t channels, uint8_t *destination, uint32_t capacity,
    uint32_t *written) {
    if (written) *written=0;
    if (!pixels || !destination || !written || (channels!=3 && channels!=4) ||
        !width || !height || width>MAX_DIM || height>MAX_DIM ||
        (uint64_t)width*height>MAX_PIXELS) return 0;
    size_t n=(size_t)width*height;
    size_t slen=encodeliteral(pixels,n,channels,0);
    uint64_t entry=(uint64_t)slen*8+3;
    unsigned hlen=6+varsize(width)+varsize(height)+1+varsize(height);
    uint64_t total=9ull+hlen+varsize(entry)+slen+4;
    if (total>capacity || total>UINT32_MAX) return 0;
    uint8_t *d=destination;
    put32(d,0x00434946u); d[4]=0; put32(d+5,0); d+=9;
    uint8_t *header=d;
    put32(d,0x51434946u); d[4]=2; d[5]=0; d+=6;
    d=putvar(d,width); d=putvar(d,height); *d++=(uint8_t)(channels==4);
    d=putvar(d,height);
    uint32_t crc=crc32c(0,header,(size_t)(d-header));
    d=putvar(d,entry);
    if (encodeliteral(pixels,n,channels,d)!=slen) return 0;
    d+=slen;
    crc=crc32c(crc,pixels,n*channels);
    put32(d,crc); d+=4;
    *written=(uint32_t)(d-destination);
    return 1;
}
