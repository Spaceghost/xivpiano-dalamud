#ifndef MELODICA_PIANO_CRYPT_H
#define MELODICA_PIANO_CRYPT_H

#include <stddef.h>

#include "blowfish.h"

char *melodica_piano_encrypt_string(const MelodicaBlowfish *ctx, const char *input);
char *melodica_piano_decrypt_string(const MelodicaBlowfish *ctx, const char *input, size_t *output_size);

#endif
