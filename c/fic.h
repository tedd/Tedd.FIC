#ifndef FIC_H
#define FIC_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    FIC_OK = 0,
    FIC_INVALID,
    FIC_LIMIT,
    FIC_NOMEM,
    FIC_UNSUPPORTED
} fic_status;

typedef struct {
    uint32_t width, height;
    uint8_t channels, tier;
    const uint8_t *exif;
    size_t exif_length;
    uint8_t *pixels;
    size_t pixels_length;
} fic_image;

/* Optional outer-compression adapter. expected_length is zero for version 01,
   whose expanded size is not recorded in the container. Allocate *output with
   malloc(); the decoder releases it with free(). */
typedef fic_status (*fic_decompress_fn)(uint8_t compression,
    const uint8_t *input, size_t input_length, size_t expected_length,
    uint8_t **output, size_t *output_length, void *context);

/* The caller owns *output and releases it with free(). EXIF in fic_image
   points into the input; pixels is allocated and released with fic_image_free. */
fic_status fic_encode(const uint8_t *pixels, uint32_t width, uint32_t height,
                      uint8_t channels, const uint8_t *exif, size_t exif_length,
                      uint8_t **output, size_t *output_length);
fic_status fic_decode(const uint8_t *input, size_t input_length, fic_image *image);
fic_status fic_decode_ex(const uint8_t *input, size_t input_length,
                         fic_decompress_fn decompress, void *context,
                         fic_image *image);
void fic_image_free(fic_image *image);
const char *fic_status_string(fic_status status);

#ifdef __cplusplus
}
#endif
#endif
