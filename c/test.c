#include "fic.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static unsigned char *read_file(const char *path, size_t *len) {
    FILE *f = fopen(path,"rb"); unsigned char *p;
    long n;
    if (!f || fseek(f,0,SEEK_END) || (n=ftell(f)) < 0 || fseek(f,0,SEEK_SET)) { if (f) fclose(f); return NULL; }
    p = (unsigned char *)malloc((size_t)n+1);
    if (!p || fread(p,1,(size_t)n,f) != (size_t)n) { free(p); fclose(f); return NULL; }
    fclose(f); *len = (size_t)n; return p;
}
static int write_file(const char *path, const unsigned char *p, size_t n) {
    FILE *f = fopen(path,"wb"); int ok;
    if (!f) return 0;
    ok = fwrite(p,1,n,f) == n;
    if (fclose(f)) ok = 0;
    return ok;
}
static unsigned crc(const unsigned char *p, size_t n, unsigned seed) {
    unsigned v = ~seed; size_t i; unsigned j;
    for (i = 0; i < n; i++) {
        v ^= p[i];
        for (j = 0; j < 8; j++) v = (v>>1) ^ (0x82f63b78u & (0u-(v&1u)));
    }
    return ~v;
}
static int fixtures(void) {
    unsigned char file[64] = {70,73,67,0,0,0,0,0,0};
    unsigned char zero[64*64*3] = {0};
    unsigned mode;
    for (mode = 0; mode < 2; mode++) {
        unsigned char *q = file+9;
        size_t h = 10, pos, i;
        unsigned check;
        fic_image img = {0};
        memcpy(q,"FICQ",4); q[4]=2; q[5]=1; q[6]=64; q[7]=64;
        q[8] = mode ? 4 : 0; q[9] = 64;
        if (mode) {
            q[h++] = 0; q[h++] = 0; q[h++] = 0; q[h++] = 0;
            for (i = 0; i < 10; i++) q[h++] = 1;
        }
        pos = h; q[pos++] = (unsigned char)(12*8+(mode ? 3 : 2));
        for (i = 0; i < 4; i++) {
            q[pos++] = (unsigned char)(mode ? 2 : 205);
            q[pos++] = (unsigned char)(mode ? 254 : 250);
            q[pos++] = 7;
        }
        check = crc(zero,sizeof zero,crc(q,h,0));
        for (i = 0; i < 4; i++) q[pos++] = (unsigned char)(check >> (8*i));
        if (fic_decode(file,9+pos,&img) != FIC_OK ||
            img.pixels_length != sizeof zero || memcmp(img.pixels,zero,sizeof zero)) {
            fic_image_free(&img); return 0;
        }
        fic_image_free(&img);
    }
    return 1;
}
int main(int argc, char **argv) {
    unsigned char *input = NULL, *raw = NULL, *encoded = NULL;
    size_t input_len = 0, raw_len = 0, encoded_len = 0;
    fic_image img = {0}; fic_status st; int ok = 0;
    if (argc == 2 && !strcmp(argv[1],"fixtures")) {
        ok = fixtures();
    } else if (argc == 5 && !strcmp(argv[1],"roundtrip")) {
        unsigned w = (unsigned)strtoul(argv[2],NULL,10);
        unsigned h = (unsigned)strtoul(argv[3],NULL,10);
        unsigned ch = (unsigned)strtoul(argv[4],NULL,10);
        size_t i; const unsigned char exif[] = {73,73,42,0};
        if (!w || !h || (ch != 3 && ch != 4) || (size_t)w*h*ch > (1u<<24)) return 2;
        raw_len = (size_t)w*h*ch; raw = (unsigned char *)malloc(raw_len);
        if (!raw) return 2;
        for (i = 0; i < raw_len; i++) raw[i] = (unsigned char)(i*31u+(i/17u)*7u);
        st = fic_encode(raw,w,h,(uint8_t)ch,exif,sizeof exif,&encoded,&encoded_len);
        if (st != FIC_OK) goto done;
        st = fic_decode(encoded,encoded_len,&img);
        ok = st == FIC_OK && img.width == w && img.height == h && img.channels == ch &&
             img.exif_length == sizeof exif && !memcmp(img.exif,exif,sizeof exif) &&
             img.pixels_length == raw_len && !memcmp(img.pixels,raw,raw_len);
        if (ok) {
            encoded[encoded_len-1] ^= 1;
            fic_image_free(&img);
            ok = fic_decode(encoded,encoded_len,&img) == FIC_INVALID;
        }
    } else if (argc == 4 && !strcmp(argv[1],"verify")) {
        input = read_file(argv[2],&input_len); raw = read_file(argv[3],&raw_len);
        if (!input || !raw) goto done;
        st = fic_decode(input,input_len,&img);
        ok = st == FIC_OK && img.pixels_length == raw_len && !memcmp(img.pixels,raw,raw_len);
        if (!ok) fprintf(stderr,"decode %s: %s\n",argv[2],fic_status_string(st));
    } else if (argc == 7 && !strcmp(argv[1],"encode")) {
        unsigned w = (unsigned)strtoul(argv[3],NULL,10);
        unsigned h = (unsigned)strtoul(argv[4],NULL,10);
        unsigned ch = (unsigned)strtoul(argv[5],NULL,10);
        raw = read_file(argv[2],&raw_len);
        if (!raw || raw_len != (size_t)w*h*ch) goto done;
        st = fic_encode(raw,w,h,(uint8_t)ch,NULL,0,&encoded,&encoded_len);
        ok = st == FIC_OK && write_file(argv[6],encoded,encoded_len);
    } else {
        fprintf(stderr,"usage: fic_test fixtures | roundtrip W H C | verify file.fic file.raw | encode file.raw W H C file.fic\n");
        return 2;
    }
done:
    fic_image_free(&img); free(input); free(raw); free(encoded);
    if (!ok) { fprintf(stderr,"test failed\n"); return 1; }
    return 0;
}
