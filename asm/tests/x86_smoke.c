#include <windows.h>
#include <io.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include "../fic.h"

typedef int (__cdecl *decode_fn)(const uint8_t *, uint32_t, uint8_t *, uint32_t,
    fic_info *, fic_decompress_fn, void *);
typedef int (__cdecl *encode_fn)(const uint8_t *, uint32_t, uint32_t, uint32_t,
    uint8_t *, uint32_t, uint32_t *);
static uint32_t get32(const uint8_t *p) {
    return (uint32_t)p[0]|(uint32_t)p[1]<<8|(uint32_t)p[2]<<16|(uint32_t)p[3]<<24;
}
static void put32(uint8_t *p, uint32_t v) {
    p[0]=(uint8_t)v; p[1]=(uint8_t)(v>>8); p[2]=(uint8_t)(v>>16); p[3]=(uint8_t)(v>>24);
}
static int __cdecl passthrough(uint32_t codec, const uint8_t *src, uint32_t slen,
    uint8_t *dst, uint32_t cap, uint32_t *written, void *context) {
    (void)context;
    if (codec!=1 && codec!=3) return 0;
    *written=slen;
    if (!dst) return 1;
    if (cap<slen) return 0;
    memcpy(dst,src,slen);
    return 1;
}
static uint8_t *readfile(const char *path, uint32_t *length) {
    FILE *f=fopen(path,"rb");
    if (!f) return 0;
    if (fseek(f,0,SEEK_END) || ftell(f)<0) { fclose(f); return 0; }
    long n=ftell(f);
    if (fseek(f,0,SEEK_SET)) { fclose(f); return 0; }
    uint8_t *data=(uint8_t*)malloc(n?n:1);
    if (!data || fread(data,1,n,f)!=(size_t)n) { free(data); fclose(f); return 0; }
    fclose(f); *length=(uint32_t)n; return data;
}
int main(int argc, char **argv) {
    if (argc!=3) return 2;
    HMODULE dll=LoadLibraryA(argv[1]);
    if (!dll) { fprintf(stderr,"LoadLibrary failed: %lu\n",GetLastError()); return 2; }
    decode_fn decode=(decode_fn)GetProcAddress(dll,"fic_decode");
    encode_fn encode=(encode_fn)GetProcAddress(dll,"fic_encode");
    if (!decode || !encode) return 2;
    char pattern[MAX_PATH], ficpath[MAX_PATH], rawpath[MAX_PATH];
    if (snprintf(pattern,sizeof(pattern),"%s\\*.fic",argv[2])>=sizeof(pattern)) return 2;
    struct _finddata_t item;
    intptr_t search=_findfirst(pattern,&item);
    if (search==-1) return 2;
    unsigned cases=0;
    do {
        if (snprintf(ficpath,sizeof(ficpath),"%s\\%s",argv[2],item.name)>=sizeof(ficpath)) return 2;
        strcpy_s(rawpath,sizeof(rawpath),ficpath);
        strcpy_s(rawpath+strlen(rawpath)-4,5,".raw");
        uint32_t flen=0,rlen=0;
        uint8_t *file=readfile(ficpath,&flen), *raw=readfile(rawpath,&rlen);
        uint8_t *out=(uint8_t*)malloc(rlen?rlen:1);
        fic_info info={0};
        if (!file || !raw || !out || !decode(file,flen,0,0,&info,0,0) ||
            info.width*info.height*info.channels!=rlen ||
            !decode(file,flen,out,rlen,&info,0,0) ||
            memcmp(out,raw,rlen)) { fprintf(stderr,"decode failed: %s\n",item.name); return 1; }
        if (!cases) {
            uint32_t exif=get32(file+5), qlen=flen-9-exif;
            uint8_t *v1=(uint8_t*)malloc(flen+1), *v2=(uint8_t*)malloc(flen+5);
            if (!v1 || !v2) return 2;
            memcpy(v1,file,4); v1[4]=1; v1[5]=1;
            memcpy(v1+6,file+5,flen-5);
            memcpy(v2,file,4); v2[4]=2; v2[5]=1;
            put32(v2+6,exif); put32(v2+10,qlen);
            memcpy(v2+14,file+9,flen-9);
            if (!decode(v1,flen+1,out,rlen,&info,passthrough,0) || memcmp(out,raw,rlen) ||
                !decode(v2,flen+5,out,rlen,&info,passthrough,0) || memcmp(out,raw,rlen)) {
                fprintf(stderr,"callback ABI failed\n"); return 1;
            }
            free(v1); free(v2);
        }
        uint32_t cap=rlen*5+128, written=0;
        uint8_t *encoded=(uint8_t*)malloc(cap);
        if (!encoded || !encode(raw,info.width,info.height,info.channels,encoded,cap,&written) ||
            !decode(encoded,written,out,rlen,&info,0,0) || memcmp(out,raw,rlen)) {
            fprintf(stderr,"encode failed: %s\n",item.name); return 1;
        }
        encoded[written-1]^=1;
        if (decode(encoded,written,out,rlen,&info,0,0)) {
            fprintf(stderr,"bad CRC accepted: %s\n",item.name); return 1;
        }
        free(encoded); free(file); free(raw); free(out);
        cases++;
    } while (_findnext(search,&item)==0);
    _findclose(search);
    printf("PASS: %u x86 decode and round-trip fixtures\n",cases);
    FreeLibrary(dll);
    return 0;
}
