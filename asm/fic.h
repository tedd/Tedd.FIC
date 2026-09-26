#ifndef TEDD_FIC_ASM_H
#define TEDD_FIC_ASM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct fic_info {
    uint32_t width, height, channels, tier;
    const uint8_t *exif;
    uint32_t exif_length;
} fic_info;

/* codec: 1=raw Deflate, 2=GZip, 3=Zstandard, 4=Brotli.
 * For version 01 Zstandard frames, destination is NULL on the first call;
 * return the exact expanded byte count through written. Return 1 on success.
 * Callbacks must return an error code rather than unwind through native code. */
typedef int (__cdecl *fic_decompress_fn)(uint32_t codec, const uint8_t *source,
    uint32_t source_length, uint8_t *destination, uint32_t destination_length,
    uint32_t *written, void *context);

/* Returns 1 on success. The caller owns destination; written is zeroed on failure.
 * A capacity of 64 + 5*width*height is sufficient when it fits in uint32_t.
 * Produces uncompressed version 00 with a Fast-tier Literal strip. */
int __cdecl fic_encode(const uint8_t *pixels, uint32_t width, uint32_t height,
    uint32_t channels, uint8_t *destination, uint32_t capacity, uint32_t *written);

/* Returns 1 on success. Destination must fit width*height*channels bytes.
 * With destination=NULL and capacity=0, validates framing and reports dimensions
 * in info without decoding pixels or checking their CRC.
 * The EXIF pointer in info aliases source and remains valid while source does.
 * The decompression callback is only used for compressed containers. */
int __cdecl fic_decode(const uint8_t *source, uint32_t length,
    uint8_t *destination, uint32_t capacity, fic_info *info,
    fic_decompress_fn decompress, void *context);

#ifdef __cplusplus
}
#endif
#endif
