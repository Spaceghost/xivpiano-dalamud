#ifndef MELODICA_JSON_H
#define MELODICA_JSON_H

#include <stddef.h>

typedef enum MelodicaJsonType {
    MELODICA_JSON_OBJECT,
    MELODICA_JSON_ARRAY,
    MELODICA_JSON_STRING,
    MELODICA_JSON_PRIMITIVE
} MelodicaJsonType;

typedef struct MelodicaJsonToken {
    MelodicaJsonType type;
    size_t start;
    size_t end;
    size_t parent;
} MelodicaJsonToken;

typedef struct MelodicaJsonDoc {
    const char *text;
    size_t length;
    MelodicaJsonToken *tokens;
    size_t count;
    size_t capacity;
} MelodicaJsonDoc;

int melodica_json_parse(MelodicaJsonDoc *doc, const char *text);
void melodica_json_destroy(MelodicaJsonDoc *doc);
size_t melodica_json_object_get(const MelodicaJsonDoc *doc, size_t object, const char *key);
size_t melodica_json_array_count(const MelodicaJsonDoc *doc, size_t array);
size_t melodica_json_array_get(const MelodicaJsonDoc *doc, size_t array, size_t position);
int melodica_json_string_is(const MelodicaJsonDoc *doc, size_t token, const char *value);
char *melodica_json_string_dup(const MelodicaJsonDoc *doc, size_t token);
int melodica_json_long(const MelodicaJsonDoc *doc, size_t token, long *value);
int melodica_json_bool(const MelodicaJsonDoc *doc, size_t token, int *value);
int melodica_json_double(const MelodicaJsonDoc *doc, size_t token, double *value);
int melodica_json_is_null(const MelodicaJsonDoc *doc, size_t token);

#define MELODICA_JSON_NONE ((size_t)-1)

#endif
