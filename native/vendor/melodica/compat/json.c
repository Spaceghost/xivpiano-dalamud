#include "json.h"

#include <errno.h>
#include <limits.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define JSON_MAX_DEPTH 32u

typedef struct JsonParser {
    MelodicaJsonDoc *doc;
    size_t pos;
    unsigned depth;
} JsonParser;

static int is_space(const char c) {
    return c == ' ' || c == '\t' || c == '\r' || c == '\n';
}

static int is_hex(const char c) {
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
        (c >= 'A' && c <= 'F');
}

static unsigned hex_value(const char c) {
    if (c >= '0' && c <= '9') return (unsigned)(c - '0');
    if (c >= 'a' && c <= 'f') return 10u + (unsigned)(c - 'a');
    return 10u + (unsigned)(c - 'A');
}

static void skip_space(JsonParser *p) {
    for (;;) {
        while (p->pos < p->doc->length && is_space(p->doc->text[p->pos])) ++p->pos;
        if (p->pos + 1u >= p->doc->length || p->doc->text[p->pos] != '/') return;
        if (p->doc->text[p->pos + 1u] == '/') {
            p->pos += 2u;
            while (p->pos < p->doc->length && p->doc->text[p->pos] != '\n') ++p->pos;
        } else if (p->doc->text[p->pos + 1u] == '*') {
            p->pos += 2u;
            while (p->pos + 1u < p->doc->length &&
                    !(p->doc->text[p->pos] == '*' && p->doc->text[p->pos + 1u] == '/')) ++p->pos;
            if (p->pos + 1u >= p->doc->length) { p->pos = p->doc->length; return; }
            p->pos += 2u;
        } else return;
    }
}

static size_t add_token(MelodicaJsonDoc *doc, const MelodicaJsonType type,
        const size_t start, const size_t end, const size_t parent) {
    if (doc->count == doc->capacity) {
        const size_t new_capacity = doc->capacity == 0u ? 32u : doc->capacity * 2u;
        if (new_capacity < doc->capacity ||
                new_capacity > (size_t)-1 / sizeof(*doc->tokens)) return MELODICA_JSON_NONE;
        MelodicaJsonToken *tokens = (MelodicaJsonToken *)realloc(doc->tokens,
                new_capacity * sizeof(*tokens));
        if (tokens == NULL) return MELODICA_JSON_NONE;
        doc->tokens = tokens;
        doc->capacity = new_capacity;
    }
    const size_t index = doc->count++;
    doc->tokens[index].type = type;
    doc->tokens[index].start = start;
    doc->tokens[index].end = end;
    doc->tokens[index].parent = parent;
    return index;
}

static int parse_value(JsonParser *p, size_t parent);

static int parse_string(JsonParser *p, const size_t parent) {
    if (p->pos >= p->doc->length ||
            (p->doc->text[p->pos] != '"' && p->doc->text[p->pos] != '\'')) return 0;
    const char quote = p->doc->text[p->pos];
    const size_t start = ++p->pos;
    while (p->pos < p->doc->length) {
        const unsigned char c = (unsigned char)p->doc->text[p->pos];
        if (c == (unsigned char)quote) {
            const size_t end = p->pos++;
            return add_token(p->doc, MELODICA_JSON_STRING, start, end, parent) !=
                MELODICA_JSON_NONE;
        }
        if (c == '\\') {
            ++p->pos;
            if (p->pos >= p->doc->length) return 0;
            const char esc = p->doc->text[p->pos];
            if (strchr("\"\\/bfnrt", esc) != NULL) {
                ++p->pos;
                continue;
            }
            if (esc == 'u') {
                if (p->pos + 4u >= p->doc->length) return 0;
                for (unsigned i = 1u; i <= 4u; ++i) {
                    if (!is_hex(p->doc->text[p->pos + i])) return 0;
                }
                p->pos += 5u;
                continue;
            }
            return 0;
        }
        ++p->pos;
    }
    return 0;
}

static int parse_primitive(JsonParser *p, const size_t parent) {
    const size_t start = p->pos;
    while (p->pos < p->doc->length) {
        const char c = p->doc->text[p->pos];
        if (is_space(c) || c == ',' || c == ']' || c == '}' || c == '/') break;
        if ((unsigned char)c < 0x20u || c == '"' || c == '[' || c == '{' || c == ':') return 0;
        ++p->pos;
    }
    if (p->pos == start) return 0;
    size_t end_pos = p->pos;
    const size_t length = end_pos - start;
    const char *text = p->doc->text + start;
    char keyword[10];
    if (length < sizeof(keyword)) {
        for (size_t i = 0u; i < length; ++i) {
            keyword[i] = text[i] >= 'A' && text[i] <= 'Z' ? text[i] + ('a' - 'A') : text[i];
        }
        keyword[length] = '\0';
    } else keyword[0] = '\0';
    if (strcmp(keyword, "true") != 0 && strcmp(keyword, "false") != 0 &&
            strcmp(keyword, "null") != 0 && strcmp(keyword, "nan") != 0 &&
            strcmp(keyword, "infinity") != 0 && strcmp(keyword, "-infinity") != 0) {
        if (!(text[0] == '-' || (text[0] >= '0' && text[0] <= '9'))) return 0;
        int exponent = 0, decimal = 0, sign = 1;
        for (size_t i = 0u; i < length; ++i) {
            const char c = text[i];
            if (c >= '0' && c <= '9') sign = 0;
            else if ((c == '-' || c == '+') && sign) sign = 0;
            else if ((c == 'e' || c == 'E') && !exponent) { exponent = 1; sign = 1; }
            else if (c == '.' && !decimal && !exponent) { decimal = 1; sign = 1; }
            else return 0;
        }
        if (exponent || decimal) {
            while (end_pos > start + 1u && strchr("eE-+", p->doc->text[end_pos - 1u]) != NULL) --end_pos;
        }
        char *end;
        (void)strtod(text, &end);
        if (end == text) return 0;
    }
    return add_token(p->doc, MELODICA_JSON_PRIMITIVE, start, end_pos, parent) !=
        MELODICA_JSON_NONE;
}

static int parse_object(JsonParser *p, const size_t parent) {
    const size_t start = p->pos;
    const size_t object = add_token(p->doc, MELODICA_JSON_OBJECT, start, start, parent);
    if (object == MELODICA_JSON_NONE) return 0;
    ++p->pos;
    skip_space(p);
    if (p->pos < p->doc->length && p->doc->text[p->pos] == '}') {
        p->doc->tokens[object].end = ++p->pos;
        return 1;
    }
    for (;;) {
        skip_space(p);
        if (p->pos < p->doc->length && p->doc->text[p->pos] == '}') {
            p->doc->tokens[object].end = ++p->pos;
            return 1;
        }
        if (!parse_string(p, object)) return 0;
        skip_space(p);
        if (p->pos >= p->doc->length || p->doc->text[p->pos++] != ':') return 0;
        skip_space(p);
        if (!parse_value(p, object)) return 0;
        skip_space(p);
        if (p->pos >= p->doc->length) return 0;
        if (p->doc->text[p->pos] == '}') {
            p->doc->tokens[object].end = ++p->pos;
            return 1;
        }
        if (p->doc->text[p->pos++] != ',') return 0;
    }
}

static int parse_array(JsonParser *p, const size_t parent) {
    const size_t start = p->pos;
    const size_t array = add_token(p->doc, MELODICA_JSON_ARRAY, start, start, parent);
    if (array == MELODICA_JSON_NONE) return 0;
    ++p->pos;
    skip_space(p);
    if (p->pos < p->doc->length && p->doc->text[p->pos] == ']') {
        p->doc->tokens[array].end = ++p->pos;
        return 1;
    }
    for (;;) {
        skip_space(p);
        if (p->pos < p->doc->length && p->doc->text[p->pos] == ']') {
            p->doc->tokens[array].end = ++p->pos;
            return 1;
        }
        if (!parse_value(p, array)) return 0;
        skip_space(p);
        if (p->pos >= p->doc->length) return 0;
        if (p->doc->text[p->pos] == ']') {
            p->doc->tokens[array].end = ++p->pos;
            return 1;
        }
        if (p->doc->text[p->pos++] != ',') return 0;
    }
}

static int parse_value(JsonParser *p, const size_t parent) {
    if (++p->depth > JSON_MAX_DEPTH) return 0;
    skip_space(p);
    int ok = 0;
    if (p->pos < p->doc->length) {
        switch (p->doc->text[p->pos]) {
            case '{': ok = parse_object(p, parent); break;
            case '[': ok = parse_array(p, parent); break;
            case '"': case '\'': ok = parse_string(p, parent); break;
            default: ok = parse_primitive(p, parent); break;
        }
    }
    --p->depth;
    return ok;
}

int melodica_json_parse(MelodicaJsonDoc *doc, const char *text) {
    if (doc == NULL || text == NULL) return 0;
    memset(doc, 0, sizeof(*doc));
    doc->text = text;
    doc->length = strlen(text);
    JsonParser p;
    p.doc = doc;
    p.pos = 0u;
    p.depth = 0u;
    skip_space(&p);
    if (!parse_value(&p, MELODICA_JSON_NONE)) {
        melodica_json_destroy(doc);
        return 0;
    }
    skip_space(&p);
    if (doc->count == 0u) {
        melodica_json_destroy(doc);
        return 0;
    }
    return 1;
}

void melodica_json_destroy(MelodicaJsonDoc *doc) {
    if (doc == NULL) return;
    free(doc->tokens);
    memset(doc, 0, sizeof(*doc));
}

static int token_raw_is(const MelodicaJsonDoc *doc, const size_t token, const char *value) {
    if (doc == NULL || value == NULL || token >= doc->count) return 0;
    const MelodicaJsonToken *t = &doc->tokens[token];
    const size_t length = t->end - t->start;
    if (strlen(value) != length) return 0;
    if (t->type != MELODICA_JSON_PRIMITIVE) return memcmp(doc->text + t->start, value, length) == 0;
    for (size_t i = 0u; i < length; ++i) {
        char a = doc->text[t->start + i], b = value[i];
        if (a >= 'A' && a <= 'Z') a += 'a' - 'A';
        if (b >= 'A' && b <= 'Z') b += 'a' - 'A';
        if (a != b) return 0;
    }
    return 1;
}

size_t melodica_json_object_get(const MelodicaJsonDoc *doc, const size_t object,
        const char *key) {
    if (doc == NULL || object >= doc->count || key == NULL ||
            doc->tokens[object].type != MELODICA_JSON_OBJECT) return MELODICA_JSON_NONE;
    int key_slot = 1;
    int matched = 0;
    size_t found = MELODICA_JSON_NONE;
    for (size_t i = object + 1u; i < doc->count &&
            doc->tokens[i].start < doc->tokens[object].end; ++i) {
        if (doc->tokens[i].parent != object) continue;
        if (key_slot) {
            if (doc->tokens[i].type != MELODICA_JSON_STRING) return MELODICA_JSON_NONE;
            matched = melodica_json_string_is(doc, i, key);
            key_slot = 0;
        } else {
            if (matched) found = i;
            key_slot = 1;
        }
    }
    return found;
}

size_t melodica_json_array_count(const MelodicaJsonDoc *doc, const size_t array) {
    if (doc == NULL || array >= doc->count ||
            doc->tokens[array].type != MELODICA_JSON_ARRAY) return 0u;
    size_t count = 0u;
    for (size_t i = array + 1u; i < doc->count &&
            doc->tokens[i].start < doc->tokens[array].end; ++i) {
        if (doc->tokens[i].parent == array) ++count;
    }
    return count;
}

size_t melodica_json_array_get(const MelodicaJsonDoc *doc, const size_t array,
        const size_t position) {
    if (doc == NULL || array >= doc->count ||
            doc->tokens[array].type != MELODICA_JSON_ARRAY) return MELODICA_JSON_NONE;
    size_t current = 0u;
    for (size_t i = array + 1u; i < doc->count &&
            doc->tokens[i].start < doc->tokens[array].end; ++i) {
        if (doc->tokens[i].parent != array) continue;
        if (current++ == position) return i;
    }
    return MELODICA_JSON_NONE;
}

static int read_u16(const char *p, unsigned *value);
static char *write_utf8(char *out, unsigned cp);

int melodica_json_string_is(const MelodicaJsonDoc *doc, const size_t token,
        const char *value) {
    if (doc == NULL || value == NULL || token >= doc->count ||
            doc->tokens[token].type != MELODICA_JSON_STRING) return 0;
    const MelodicaJsonToken *t = &doc->tokens[token];
    if (memchr(doc->text + t->start, '\\', t->end - t->start) == NULL) {
        return token_raw_is(doc, token, value);
    }
    /* Object lookup must not acquire a new allocation failure mode merely
     * because a server escaped a member name. Compare decoded bytes in place. */
    size_t pos = t->start, offset = 0u;
    while (pos < t->end) {
        char decoded[4];
        size_t length = 1u;
        decoded[0] = doc->text[pos++];
        if (decoded[0] == '\\') {
            const char esc = doc->text[pos++];
            switch (esc) {
                case 'b': decoded[0] = '\b'; break;
                case 'f': decoded[0] = '\f'; break;
                case 'n': decoded[0] = '\n'; break;
                case 'r': decoded[0] = '\r'; break;
                case 't': decoded[0] = '\t'; break;
                case 'u': {
                    unsigned cp;
                    if (!read_u16(doc->text + pos, &cp)) return 0;
                    pos += 4u;
                    if (cp >= 0xd800u && cp <= 0xdbffu) {
                        unsigned low;
                        if (pos + 6u <= t->end && doc->text[pos] == '\\' &&
                                doc->text[pos + 1u] == 'u' &&
                                read_u16(doc->text + pos + 2u, &low) &&
                                low >= 0xdc00u && low <= 0xdfffu) {
                            pos += 6u;
                            cp = 0x10000u + ((cp - 0xd800u) << 10u) + low - 0xdc00u;
                        } else cp = 0xfffdu;
                    } else if (cp >= 0xdc00u && cp <= 0xdfffu) cp = 0xfffdu;
                    length = (size_t)(write_utf8(decoded, cp) - decoded);
                    break;
                }
                default: decoded[0] = esc; break;
            }
        }
        for (size_t i = 0u; i < length; ++i) {
            if (decoded[i] == '\0') return value[offset] == '\0';
            if (value[offset] == '\0' || decoded[i] != value[offset++]) return 0;
        }
    }
    return value[offset] == '\0';
}

static int read_u16(const char *p, unsigned *value) {
    unsigned v = 0u;
    for (unsigned i = 0u; i < 4u; ++i) {
        if (!is_hex(p[i])) return 0;
        v = (v << 4u) | hex_value(p[i]);
    }
    *value = v;
    return 1;
}

static char *write_utf8(char *out, const unsigned cp) {
    if (cp <= 0x7fu) {
        *out++ = (char)cp;
    } else if (cp <= 0x7ffu) {
        *out++ = (char)(0xc0u | (cp >> 6u));
        *out++ = (char)(0x80u | (cp & 0x3fu));
    } else if (cp <= 0xffffu) {
        *out++ = (char)(0xe0u | (cp >> 12u));
        *out++ = (char)(0x80u | ((cp >> 6u) & 0x3fu));
        *out++ = (char)(0x80u | (cp & 0x3fu));
    } else {
        *out++ = (char)(0xf0u | (cp >> 18u));
        *out++ = (char)(0x80u | ((cp >> 12u) & 0x3fu));
        *out++ = (char)(0x80u | ((cp >> 6u) & 0x3fu));
        *out++ = (char)(0x80u | (cp & 0x3fu));
    }
    return out;
}

static char *decode_string_full(const MelodicaJsonDoc *doc, const size_t token,
        size_t *decoded_length) {
    if (doc == NULL || token >= doc->count ||
            doc->tokens[token].type != MELODICA_JSON_STRING) return NULL;
    const MelodicaJsonToken *t = &doc->tokens[token];
    const size_t raw_length = t->end - t->start;
    char *result = (char *)malloc(raw_length + 1u);
    if (result == NULL) return NULL;
    char *out = result;
    size_t i = t->start;
    while (i < t->end) {
        const char c = doc->text[i++];
        if (c != '\\') {
            *out++ = c;
            continue;
        }
        if (i >= t->end) { free(result); return NULL; }
        const char esc = doc->text[i++];
        switch (esc) {
            case '"': *out++ = '"'; break;
            case '\'': *out++ = '\''; break;
            case '\\': *out++ = '\\'; break;
            case '/': *out++ = '/'; break;
            case 'b': *out++ = '\b'; break;
            case 'f': *out++ = '\f'; break;
            case 'n': *out++ = '\n'; break;
            case 'r': *out++ = '\r'; break;
            case 't': *out++ = '\t'; break;
            case 'u': {
                if (i + 4u > t->end) { free(result); return NULL; }
                unsigned cp;
                if (!read_u16(doc->text + i, &cp)) { free(result); return NULL; }
                i += 4u;
                if (cp >= 0xd800u && cp <= 0xdbffu) {
                    unsigned low;
                    if (i + 6u <= t->end && doc->text[i] == '\\' &&
                            doc->text[i + 1u] == 'u' &&
                            read_u16(doc->text + i + 2u, &low) &&
                            low >= 0xdc00u && low <= 0xdfffu) {
                        i += 6u;
                        cp = 0x10000u + ((cp - 0xd800u) << 10u) + (low - 0xdc00u);
                    } else {
                        cp = 0xfffdu;
                    }
                } else if (cp >= 0xdc00u && cp <= 0xdfffu) {
                    cp = 0xfffdu;
                }
                out = write_utf8(out, cp);
                break;
            }
            default: free(result); return NULL;
        }
    }
    *out = '\0';
    if (decoded_length != NULL) *decoded_length = (size_t)(out - result);
    return result;
}

static char *decode_string(const MelodicaJsonDoc *doc, const size_t token) {
    return decode_string_full(doc, token, NULL);
}

int melodica_json_is_null(const MelodicaJsonDoc *doc, const size_t token) {
    return doc == NULL || token >= doc->count ||
        (doc->tokens[token].type == MELODICA_JSON_PRIMITIVE &&
         token_raw_is(doc, token, "null"));
}

static int primitive_is_double(const MelodicaJsonDoc *doc, const MelodicaJsonToken *t) {
    return memchr(doc->text + t->start, '.', t->end - t->start) != NULL ||
        memchr(doc->text + t->start, 'e', t->end - t->start) != NULL ||
        memchr(doc->text + t->start, 'E', t->end - t->start) != NULL ||
        (t->end < doc->length && strchr("eE+-", doc->text[t->end]) != NULL);
}

int melodica_json_double(const MelodicaJsonDoc *doc, const size_t token, double *value) {
    if (doc == NULL || token >= doc->count || value == NULL) return 0;
    *value = 0.0;
    const MelodicaJsonToken *t = &doc->tokens[token];
    if (t->type == MELODICA_JSON_STRING) {
        char *text = decode_string(doc, token);
        if (text == NULL) return 0;
        char *end;
        errno = 0;
        *value = strtod(text, &end);
        if (end == text || *end != '\0' ||
                (isinf(*value) && errno == ERANGE)) *value = 0.0;
        free(text);
    } else if (t->type == MELODICA_JSON_PRIMITIVE) {
        if (token_raw_is(doc, token, "true")) *value = 1.0;
        else if (((doc->text[t->start] == '-' && t->start + 1u < t->end &&
                     doc->text[t->start + 1u] >= '0' && doc->text[t->start + 1u] <= '9') ||
                    (doc->text[t->start] >= '0' && doc->text[t->start] <= '9')) &&
                !primitive_is_double(doc, t)) {
            *value = doc->text[t->start] == '-' ?
                (double)strtoll(doc->text + t->start, NULL, 10) :
                (double)strtoull(doc->text + t->start, NULL, 10);
        } else *value = strtod(doc->text + t->start, NULL);
    }
    return 1;
}

/* These are json_object_get_int/boolean semantics, including string prefixes,
 * saturation, and the false value of containers and JSON null. */
int melodica_json_long(const MelodicaJsonDoc *doc, const size_t token, long *value) {
    if (doc == NULL || token >= doc->count || value == NULL) return 0;
    double number = 0.0;
    if (doc->tokens[token].type == MELODICA_JSON_STRING) {
        char *text = decode_string(doc, token);
        if (text == NULL) return 0;
        number = (double)strtoll(text, NULL, 10);
        free(text);
    } else if (!melodica_json_double(doc, token, &number)) return 0;
    if (number >= (double)INT_MAX) *value = INT_MAX;
    else if (number <= (double)INT_MIN) *value = INT_MIN;
    else if (isnan(number)) *value = 0;
    else *value = (long)number;
    return 1;
}

int melodica_json_bool(const MelodicaJsonDoc *doc, const size_t token, int *value) {
    if (doc == NULL || token >= doc->count || value == NULL) return 0;
    const MelodicaJsonToken *t = &doc->tokens[token];
    *value = 0;
    if (t->type == MELODICA_JSON_STRING) {
        *value = t->end != t->start;
    } else if (t->type == MELODICA_JSON_PRIMITIVE) {
        double number;
        if (!melodica_json_double(doc, token, &number)) return 0;
        *value = number != 0.0;
    }
    return 1;
}

typedef struct JsonOutput { char *text; size_t used, capacity; } JsonOutput;

static int output_bytes(JsonOutput *out, const char *text, const size_t length) {
    if (length > (size_t)-1 - out->used - 1u) return 0;
    const size_t needed = out->used + length + 1u;
    if (needed > out->capacity) {
        size_t capacity = out->capacity == 0u ? 64u : out->capacity;
        while (capacity < needed) {
            if (capacity > (size_t)-1 / 2u) { capacity = needed; break; }
            capacity *= 2u;
        }
        char *text_buffer = (char *)realloc(out->text, capacity);
        if (text_buffer == NULL) return 0;
        out->text = text_buffer; out->capacity = capacity;
    }
    memcpy(out->text + out->used, text, length);
    out->used += length; out->text[out->used] = '\0';
    return 1;
}

static int output_quoted(JsonOutput *out, const char *text, size_t length) {
    static const char hex[] = "0123456789abcdef";
    if (!output_bytes(out, "\"", 1u)) return 0;
    for (const unsigned char *p = (const unsigned char *)text;
            length != 0u; ++p, --length) {
        const char *escape = NULL;
        switch (*p) {
            case '"': escape = "\\\""; break;
            case '\\': escape = "\\\\"; break;
            case '/': escape = "\\/"; break;
            case '\b': escape = "\\b"; break;
            case '\f': escape = "\\f"; break;
            case '\n': escape = "\\n"; break;
            case '\r': escape = "\\r"; break;
            case '\t': escape = "\\t"; break;
        }
        if (escape != NULL) {
            if (!output_bytes(out, escape, 2u)) return 0;
        } else if (*p < 0x20u) {
            char control[] = {'\\', 'u', '0', '0', hex[*p >> 4u], hex[*p & 15u]};
            if (!output_bytes(out, control, sizeof(control))) return 0;
        } else if (!output_bytes(out, (const char *)p, 1u)) return 0;
    }
    return output_bytes(out, "\"", 1u);
}

static int output_value(JsonOutput *out, const MelodicaJsonDoc *doc, size_t token) {
    const MelodicaJsonToken *t = &doc->tokens[token];
    if (t->type == MELODICA_JSON_STRING) {
        size_t length;
        char *decoded = decode_string_full(doc, token, &length);
        if (decoded == NULL) return 0;
        const int ok = output_quoted(out, decoded, length);
        free(decoded); return ok;
    }
    if (t->type == MELODICA_JSON_PRIMITIVE) {
        static const char *const keywords[] = {"true", "false", "null", "NaN", "Infinity", "-Infinity"};
        for (size_t i = 0u; i < sizeof(keywords)/sizeof(*keywords); ++i) {
            if (token_raw_is(doc, token, keywords[i])) {
                return output_bytes(out, keywords[i], strlen(keywords[i]));
            }
        }
        if (primitive_is_double(doc, t)) {
            return output_bytes(out, doc->text + t->start, t->end - t->start);
        }
        char number[32];
        int length;
        if (doc->text[t->start] == '-') {
            length = snprintf(number, sizeof(number), "%lld", strtoll(doc->text + t->start, NULL, 10));
        } else {
            length = snprintf(number, sizeof(number), "%llu", strtoull(doc->text + t->start, NULL, 10));
        }
        return length > 0 && output_bytes(out, number, (size_t)length);
    }
    const int object = t->type == MELODICA_JSON_OBJECT;
    if (!output_bytes(out, object ? "{" : "[", 1u)) return 0;
    int first = 1;
    for (size_t i = token + 1u; i < doc->count && doc->tokens[i].start < t->end; ++i) {
        if (doc->tokens[i].parent != token) continue;
        size_t value = i;
        char *key = NULL;
        if (object) {
            key = decode_string(doc, i);
            if (key == NULL) return 0;
            int prior = 0;
            for (size_t j = token + 1u; j < i; ++j) {
                if (doc->tokens[j].parent == token) {
                    if (melodica_json_string_is(doc, j, key)) prior = 1;
                    ++j;
                    while (j + 1u < i && doc->tokens[j + 1u].parent != token) ++j;
                }
            }
            value = melodica_json_object_get(doc, token, key);
            ++i; /* the next direct child is this key's original value */
            if (prior) { free(key); continue; }
        }
        if (!output_bytes(out, first ? " " : ", ", first ? 1u : 2u)) { free(key); return 0; }
        first = 0;
        if (object && (!output_quoted(out, key, strlen(key)) || !output_bytes(out, ": ", 2u))) {
            free(key); return 0;
        }
        free(key);
        if (!output_value(out, doc, value)) return 0;
    }
    if (!output_bytes(out, " ", 1u)) return 0;
    return output_bytes(out, object ? "}" : "]", 1u);
}

char *melodica_json_string_dup(const MelodicaJsonDoc *doc, const size_t token) {
    if (doc == NULL || token >= doc->count || melodica_json_is_null(doc, token)) return NULL;
    if (doc->tokens[token].type == MELODICA_JSON_STRING) return decode_string(doc, token);
    JsonOutput out = {NULL, 0u, 0u};
    if (!output_value(&out, doc, token)) { free(out.text); return NULL; }
    return out.text;
}
