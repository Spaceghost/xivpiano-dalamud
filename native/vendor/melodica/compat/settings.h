#ifndef MELODICA_PIANO_SETTINGS_INTERNAL_H
#define MELODICA_PIANO_SETTINGS_INTERNAL_H

#include <stdint.h>
#include <time.h>

static int piano_settings_size_add(size_t *value, const size_t add) {
    if (add > (size_t)-1 - *value) return 0;
    *value += add;
    return 1;
}

static int piano_settings_ready(const PianoHandle_t *ph, const PianoRequest_t *req) {
    return ph != NULL && req != NULL && ph->user.authToken != NULL &&
        ph->user.listenerId != NULL && ph->partner.out != NULL;
}

static int32_t piano_settings_timestamp(const PianoHandle_t *ph) {
    return (int32_t)((int64_t)time(NULL) - (int64_t)ph->timeOffset);
}

static char *piano_settings_get_json(const PianoHandle_t *ph, const int32_t timestamp) {
    if (ph == NULL || ph->user.authToken == NULL) return NULL;
    char number[32];
    const int digits = snprintf(number, sizeof(number), "%ld", (long)timestamp);
    if (digits < 0 || (size_t)digits >= sizeof(number)) return NULL;

    size_t capacity = 64u;
    if (!piano_settings_size_add(&capacity, piano_json_escaped_size(ph->user.authToken)) ||
            !piano_settings_size_add(&capacity, (size_t)digits)) return NULL;
    char *json = (char *)malloc(capacity + 1u);
    if (json == NULL) return NULL;
    char *out = piano_copy_literal(json, "{ \"userAuthToken\": \"");
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, "\", \"syncTime\": ");
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, " }");
    *out = '\0';
    assert((size_t)(out - json) <= capacity);
    return json;
}

static char *piano_settings_change_json(const PianoHandle_t *ph,
        const PianoRequestDataChangeSettings_t *data, const int32_t timestamp) {
    if (ph == NULL || ph->user.authToken == NULL || data == NULL ||
            data->currentUsername == NULL || data->currentPassword == NULL) return NULL;

    char number[32];
    const int digits = snprintf(number, sizeof(number), "%ld", (long)timestamp);
    if (digits < 0 || (size_t)digits >= sizeof(number)) return NULL;

    size_t capacity = 256u;
    const char *const values[] = {
        data->currentUsername, data->currentPassword,
        data->newUsername, data->newPassword, ph->user.authToken
    };
    for (size_t i = 0u; i < sizeof(values) / sizeof(values[0]); ++i) {
        if (values[i] != NULL &&
                !piano_settings_size_add(&capacity, piano_json_escaped_size(values[i]))) {
            return NULL;
        }
    }
    if (!piano_settings_size_add(&capacity, (size_t)digits)) return NULL;

    char *json = (char *)malloc(capacity + 1u);
    if (json == NULL) return NULL;
    char *out = piano_copy_literal(json,
        "{ \"userInitiatedChange\": true, \"currentUsername\": \"");
    out = piano_json_write_escaped(out, data->currentUsername);
    out = piano_copy_literal(out, "\", \"currentPassword\": \"");
    out = piano_json_write_escaped(out, data->currentPassword);
    out = piano_copy_literal(out, "\"");
    if (data->explicitContentFilter != PIANO_UNDEFINED) {
        out = piano_copy_literal(out,
            data->explicitContentFilter == PIANO_TRUE
                ? ", \"isExplicitContentFilterEnabled\": true"
                : ", \"isExplicitContentFilterEnabled\": false");
    }
    if (data->newUsername != NULL) {
        out = piano_copy_literal(out, ", \"newUsername\": \"");
        out = piano_json_write_escaped(out, data->newUsername);
        out = piano_copy_literal(out, "\"");
    }
    if (data->newPassword != NULL) {
        out = piano_copy_literal(out, ", \"newPassword\": \"");
        out = piano_json_write_escaped(out, data->newPassword);
        out = piano_copy_literal(out, "\"");
    }
    out = piano_copy_literal(out, ", \"userAuthToken\": \"");
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, "\", \"syncTime\": ");
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, " }");
    *out = '\0';
    assert((size_t)(out - json) <= capacity);
    return json;
}

static PianoReturn_t piano_request_get_settings(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_settings_ready(ph, req)) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_GET_SETTINGS;
    req->secure = false;
    char *json = piano_settings_get_json(ph, piano_settings_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "user.getSettings", json);
}

static PianoReturn_t piano_request_change_settings(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_settings_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataChangeSettings_t *data = (PianoRequestDataChangeSettings_t *)req->data;
    if (data == NULL || data->currentUsername == NULL || data->currentPassword == NULL) {
        return PIANO_RET_ERR;
    }
    req->type = PIANO_REQUEST_CHANGE_SETTINGS;
    req->secure = true;
    char *json = piano_settings_change_json(ph, data, piano_settings_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "user.changeSettings", json);
}

static PianoReturn_t piano_response_get_settings(PianoRequest_t *req,
        const MelodicaJsonDoc *doc, const size_t result) {
    PianoSettings_t *settings = req == NULL ? NULL : (PianoSettings_t *)req->data;
    if (settings == NULL || result == MELODICA_JSON_NONE || result >= doc->count ||
            melodica_json_is_null(doc, result)) {
        return PIANO_RET_INVALID_RESPONSE;
    }

    char *username = NULL;
    const size_t username_token = melodica_json_object_get(doc, result, "username");
    if (username_token != MELODICA_JSON_NONE) {
        if (username_token >= doc->count ||
                melodica_json_is_null(doc, username_token)) {
            return PIANO_RET_INVALID_RESPONSE;
        }
        username = melodica_json_string_dup(doc, username_token);
        if (username == NULL) return PIANO_RET_OUT_OF_MEMORY;
    }

    settings->explicitContentFilter = piano_json_bool_default(
        doc, result, "isExplicitContentFilterEnabled", 0) != 0;
    /* Upstream assigns this field directly and does not free a pre-existing value. */
    settings->username = username;
    return PIANO_RET_OK;
}

static PianoReturn_t piano_response_settings(PianoHandle_t *ph, PianoRequest_t *req) {
    (void)ph;
    if (req == NULL || req->responseData == NULL) return PIANO_RET_INVALID_RESPONSE;

    MelodicaJsonDoc doc;
    if (!melodica_json_parse(&doc, req->responseData)) return PIANO_RET_INVALID_RESPONSE;
    PianoReturn_t ret = PIANO_RET_INVALID_RESPONSE;
    const size_t root = 0u;
    const size_t stat = melodica_json_object_get(&doc, root, "stat");
    if (stat == MELODICA_JSON_NONE || stat >= doc.count ||
            melodica_json_is_null(&doc, stat)) goto cleanup;
    if (!melodica_json_string_is(&doc, stat, "ok")) {
        ret = piano_response_error(&doc, root, req);
        goto cleanup;
    }

    switch (req->type) {
        case PIANO_REQUEST_GET_SETTINGS:
            ret = piano_response_get_settings(req, &doc,
                melodica_json_object_get(&doc, root, "result"));
            break;
        case PIANO_REQUEST_CHANGE_SETTINGS:
            ret = PIANO_RET_OK;
            break;
        default:
            ret = PIANO_RET_ERR;
            break;
    }
cleanup:
    melodica_json_destroy(&doc);
    return ret;
}

#endif
