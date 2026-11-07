#ifndef MELODICA_PIANO_STATION_MODES_INTERNAL_H
#define MELODICA_PIANO_STATION_MODES_INTERNAL_H

#include <stdint.h>

static char *piano_station_modes_set_json(const PianoHandle_t *ph,
        const PianoRequestDataSetStationMode_t *data, const int32_t timestamp) {
    if (ph == NULL || ph->user.authToken == NULL || data == NULL ||
            data->station == NULL || data->station->id == NULL) return NULL;

    char timestamp_text[32];
    char mode_text[32];
    const int timestamp_digits = snprintf(timestamp_text, sizeof(timestamp_text),
        "%ld", (long)timestamp);
    const int mode_digits = snprintf(mode_text, sizeof(mode_text),
        "%ld", (long)(int32_t)data->id);
    if (timestamp_digits < 0 || (size_t)timestamp_digits >= sizeof(timestamp_text) ||
            mode_digits < 0 || (size_t)mode_digits >= sizeof(mode_text)) return NULL;

    size_t capacity = 96u;
    if (!piano_mutation_size_add(&capacity,
            piano_json_escaped_size(data->station->id)) ||
            !piano_mutation_size_add(&capacity,
                piano_json_escaped_size(ph->user.authToken)) ||
            !piano_mutation_size_add(&capacity, (size_t)timestamp_digits) ||
            !piano_mutation_size_add(&capacity, (size_t)mode_digits)) return NULL;

    char *json = (char *)malloc(capacity + 1u);
    if (json == NULL) return NULL;
    char *out = piano_copy_literal(json, "{ \"stationId\": \"");
    out = piano_json_write_escaped(out, data->station->id);
    out = piano_copy_literal(out, "\", \"modeId\": ");
    out = piano_copy_literal(out, mode_text);
    out = piano_copy_literal(out, ", \"userAuthToken\": \"");
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, "\", \"syncTime\": ");
    out = piano_copy_literal(out, timestamp_text);
    out = piano_copy_literal(out, " }");
    *out = '\0';
    assert((size_t)(out - json) <= capacity);
    return json;
}

static PianoReturn_t piano_request_get_station_modes(PianoHandle_t *ph,
        PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataGetStationModes_t *data =
        (PianoRequestDataGetStationModes_t *)req->data;
    if (data == NULL || data->station == NULL || data->station->id == NULL) {
        return PIANO_RET_ERR;
    }
    req->type = PIANO_REQUEST_GET_STATION_MODES;
    req->secure = true;
    char *json = piano_mutation_json(ph, "stationId", data->station->id,
        NULL, NULL, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req,
        "interactiveradio.v1.getAvailableModesSimple", json);
}

static PianoReturn_t piano_request_set_station_mode(PianoHandle_t *ph,
        PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataSetStationMode_t *data =
        (PianoRequestDataSetStationMode_t *)req->data;
    if (data == NULL || data->station == NULL || data->station->id == NULL) {
        return PIANO_RET_ERR;
    }
    req->type = PIANO_REQUEST_SET_STATION_MODE;
    req->secure = true;
    char *json = piano_station_modes_set_json(ph, data,
        piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req,
        "interactiveradio.v1.setAndGetAvailableModes", json);
}

static PianoReturn_t piano_station_modes_active(const MelodicaJsonDoc *doc,
        const size_t result, int *active) {
    *active = -1;
    if (result == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (result >= doc->count) return PIANO_RET_INVALID_RESPONSE;
    const size_t token = melodica_json_object_get(doc, result, "currentModeId");
    if (token != MELODICA_JSON_NONE) {
        long value = 0;
        if (!melodica_json_long(doc, token, &value)) return PIANO_RET_INVALID_RESPONSE;
        *active = (int)value;
    }
    return PIANO_RET_OK;
}

static PianoReturn_t piano_station_mode_string(const MelodicaJsonDoc *doc,
        const size_t object, const char *key, char **out) {
    *out = NULL;
    const size_t token = melodica_json_object_get(doc, object, key);
    if (token == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (token >= doc->count || melodica_json_is_null(doc, token)) {
        return PIANO_RET_INVALID_RESPONSE;
    }
    *out = melodica_json_string_dup(doc, token);
    return *out == NULL ? PIANO_RET_OUT_OF_MEMORY : PIANO_RET_OK;
}

static PianoReturn_t piano_response_get_station_modes(PianoRequest_t *req,
        const MelodicaJsonDoc *doc, const size_t result) {
    PianoRequestDataGetStationModes_t *data = req == NULL ? NULL :
        (PianoRequestDataGetStationModes_t *)req->data;
    if (data == NULL) return PIANO_RET_INVALID_RESPONSE;

    int active = -1;
    PianoReturn_t ret = piano_station_modes_active(doc, result, &active);
    if (ret != PIANO_RET_OK || result == MELODICA_JSON_NONE) return ret;

    const size_t modes = melodica_json_object_get(doc, result, "availableModes");
    if (modes == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (modes >= doc->count || doc->tokens[modes].type != MELODICA_JSON_ARRAY) {
        return PIANO_RET_INVALID_RESPONSE;
    }

    const size_t count = melodica_json_array_count(doc, modes);
    PianoStationMode_t *parsed = NULL, *tail = NULL;
    for (size_t i = 0u; i < count; ++i) {
        const size_t item = melodica_json_array_get(doc, modes, i);
        if (item == MELODICA_JSON_NONE || item >= doc->count ||
                doc->tokens[item].type != MELODICA_JSON_OBJECT) {
            ret = PIANO_RET_INVALID_RESPONSE;
            goto fail;
        }

        PianoStationMode_t *mode = (PianoStationMode_t *)calloc(1u, sizeof(*mode));
        if (mode == NULL) { ret = PIANO_RET_OUT_OF_MEMORY; goto fail; }
        const size_t mode_id = melodica_json_object_get(doc, item, "modeId");
        if (mode_id != MELODICA_JSON_NONE) {
            long value = 0;
            if (!melodica_json_long(doc, mode_id, &value)) {
                free(mode);
                ret = PIANO_RET_INVALID_RESPONSE;
                goto fail;
            }
            mode->id = (int)value;
            ret = piano_station_mode_string(doc, item, "modeName", &mode->name);
            if (ret == PIANO_RET_OK) {
                ret = piano_station_mode_string(doc, item, "modeDescription",
                    &mode->description);
            }
            if (ret != PIANO_RET_OK) {
                free(mode->name);
                free(mode->description);
                free(mode);
                goto fail;
            }
            mode->isAlgorithmic = piano_json_bool_default(
                doc, item, "isAlgorithmicMode", 0) != 0;
            mode->isTakeover = piano_json_bool_default(
                doc, item, "isTakeoverMode", 0) != 0;
            mode->active = active == mode->id;
        }
        if (tail == NULL) parsed = mode;
        else tail->head.next = &mode->head;
        tail = mode;
    }
    if (data->retModes == NULL) data->retModes = parsed;
    else {
        tail = data->retModes;
        while (tail->head.next != NULL) tail = (PianoStationMode_t *)tail->head.next;
        tail->head.next = parsed == NULL ? NULL : &parsed->head;
    }
    return PIANO_RET_OK;
fail:
    PianoDestroyStationMode(parsed);
    return ret;
}

static PianoReturn_t piano_response_set_station_mode(PianoRequest_t *req,
        const MelodicaJsonDoc *doc, const size_t result) {
    PianoRequestDataSetStationMode_t *data = req == NULL ? NULL :
        (PianoRequestDataSetStationMode_t *)req->data;
    if (data == NULL) return PIANO_RET_INVALID_RESPONSE;
    int active = -1;
    const PianoReturn_t ret = piano_station_modes_active(doc, result, &active);
    if (ret != PIANO_RET_OK) return ret;
    return (unsigned int)active == data->id ? PIANO_RET_OK : PIANO_RET_ERR;
}

static PianoReturn_t piano_response_station_modes(PianoHandle_t *ph,
        PianoRequest_t *req) {
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

    const size_t result = melodica_json_object_get(&doc, root, "result");
    switch (req->type) {
        case PIANO_REQUEST_GET_STATION_MODES:
            ret = piano_response_get_station_modes(req, &doc, result);
            break;
        case PIANO_REQUEST_SET_STATION_MODE:
            ret = piano_response_set_station_mode(req, &doc, result);
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
