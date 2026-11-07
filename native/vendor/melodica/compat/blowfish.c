#include "blowfish.h"
#include "blowfish_pi.h"

static unsigned hex_nibble(const char c) {
    return c >= '0' && c <= '9' ? (unsigned)(c - '0') :
        c >= 'a' && c <= 'f' ? 10u + (unsigned)(c - 'a') : 0u;
}

static uint32_t pi_word(const size_t word) {
    size_t segment;
    size_t local_word;
    if (word < 348u) {
        segment = 0u;
        local_word = word;
    } else if (word < 696u) {
        segment = 1u;
        local_word = word - 348u;
    } else {
        segment = 2u;
        local_word = word - 696u;
    }
    const char *p = melodica_blowfish_pi_hex[segment] + local_word * 8u;
    uint32_t value = 0u;
    for (unsigned i = 0u; i < 8u; ++i) {
        value = (value << 4u) | hex_nibble(p[i]);
    }
    return value;
}

static uint32_t load_be32(const uint8_t *p) {
    return ((uint32_t)p[0] << 24u) | ((uint32_t)p[1] << 16u) |
           ((uint32_t)p[2] << 8u) | (uint32_t)p[3];
}

static void store_be32(uint8_t *p, const uint32_t x) {
    p[0] = (uint8_t)(x >> 24u);
    p[1] = (uint8_t)(x >> 16u);
    p[2] = (uint8_t)(x >> 8u);
    p[3] = (uint8_t)x;
}

static uint32_t blowfish_f(const MelodicaBlowfish *ctx, const uint32_t x) {
    const uint8_t a = (uint8_t)(x >> 24u);
    const uint8_t b = (uint8_t)(x >> 16u);
    const uint8_t c = (uint8_t)(x >> 8u);
    const uint8_t d = (uint8_t)x;
    return ((ctx->s[0][a] + ctx->s[1][b]) ^ ctx->s[2][c]) + ctx->s[3][d];
}

static void encrypt_words(const MelodicaBlowfish *ctx, uint32_t *left, uint32_t *right) {
    uint32_t l = *left;
    uint32_t r = *right;
    for (unsigned i = 0u; i < 16u; ++i) {
        l ^= ctx->p[i];
        r ^= blowfish_f(ctx, l);
        const uint32_t t = l;
        l = r;
        r = t;
    }
    {
        const uint32_t t = l;
        l = r;
        r = t;
    }
    r ^= ctx->p[16];
    l ^= ctx->p[17];
    *left = l;
    *right = r;
}

static void decrypt_words(const MelodicaBlowfish *ctx, uint32_t *left, uint32_t *right) {
    uint32_t l = *left;
    uint32_t r = *right;
    for (unsigned i = 17u; i > 1u; --i) {
        l ^= ctx->p[i];
        r ^= blowfish_f(ctx, l);
        const uint32_t t = l;
        l = r;
        r = t;
    }
    {
        const uint32_t t = l;
        l = r;
        r = t;
    }
    r ^= ctx->p[1];
    l ^= ctx->p[0];
    *left = l;
    *right = r;
}

int melodica_blowfish_init(MelodicaBlowfish *ctx, const void *key_ptr, const size_t key_len) {
    if (ctx == NULL || key_ptr == NULL || key_len == 0u || key_len > 56u) return 0;
    const uint8_t *key = (const uint8_t *)key_ptr;

    size_t word = 0u;
    for (unsigned i = 0u; i < 18u; ++i) ctx->p[i] = pi_word(word++);
    for (unsigned box = 0u; box < 4u; ++box) {
        for (unsigned i = 0u; i < 256u; ++i) ctx->s[box][i] = pi_word(word++);
    }

    size_t j = 0u;
    for (unsigned i = 0u; i < 18u; ++i) {
        uint32_t value = 0u;
        for (unsigned k = 0u; k < 4u; ++k) {
            value = (value << 8u) | key[j];
            j = (j + 1u) % key_len;
        }
        ctx->p[i] ^= value;
    }

    uint32_t left = 0u;
    uint32_t right = 0u;
    for (unsigned i = 0u; i < 18u; i += 2u) {
        encrypt_words(ctx, &left, &right);
        ctx->p[i] = left;
        ctx->p[i + 1u] = right;
    }
    for (unsigned box = 0u; box < 4u; ++box) {
        for (unsigned i = 0u; i < 256u; i += 2u) {
            encrypt_words(ctx, &left, &right);
            ctx->s[box][i] = left;
            ctx->s[box][i + 1u] = right;
        }
    }
    return 1;
}

void melodica_blowfish_encrypt_block(const MelodicaBlowfish *ctx, uint8_t block[8]) {
    uint32_t left = load_be32(block);
    uint32_t right = load_be32(block + 4);
    encrypt_words(ctx, &left, &right);
    store_be32(block, left);
    store_be32(block + 4, right);
}

void melodica_blowfish_decrypt_block(const MelodicaBlowfish *ctx, uint8_t block[8]) {
    uint32_t left = load_be32(block);
    uint32_t right = load_be32(block + 4);
    decrypt_words(ctx, &left, &right);
    store_be32(block, left);
    store_be32(block + 4, right);
}

void melodica_blowfish_clear(MelodicaBlowfish *ctx) {
    if (ctx == NULL) return;
    volatile uint8_t *p = (volatile uint8_t *)ctx;
    for (size_t i = 0u; i < sizeof(*ctx); ++i) p[i] = 0u;
}
