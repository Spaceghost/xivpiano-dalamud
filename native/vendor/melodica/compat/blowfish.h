#ifndef MELODICA_BLOWFISH_H
#define MELODICA_BLOWFISH_H

#include <stddef.h>
#include <stdint.h>

typedef struct MelodicaBlowfish {
    uint32_t p[18];
    uint32_t s[4][256];
} MelodicaBlowfish;

int melodica_blowfish_init(MelodicaBlowfish *ctx, const void *key, size_t key_len);
void melodica_blowfish_encrypt_block(const MelodicaBlowfish *ctx, uint8_t block[8]);
void melodica_blowfish_decrypt_block(const MelodicaBlowfish *ctx, uint8_t block[8]);
void melodica_blowfish_clear(MelodicaBlowfish *ctx);

#endif
