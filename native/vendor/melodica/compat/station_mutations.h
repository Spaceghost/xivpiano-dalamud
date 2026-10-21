#ifndef MELODICA_PIANO_STATION_MUTATIONS_INTERNAL_H
#define MELODICA_PIANO_STATION_MUTATIONS_INTERNAL_H

#include <stdint.h>

static int piano_mutation_size_add(size_t *value, const size_t add) {
    if (add > (size_t)-1 - *value) return 0;
    *value += add;
    return 1;
}

static char *piano_mutation_json(const PianoHandle_t *ph,
        const char *key1, const char *value1,
        const char *key2, const char *value2,
        const int32_t timestamp) {
    if (ph == NULL || ph->user.authToken == NULL ||
            key1 == NULL || value1 == NULL ||
            ((key2 == NULL) != (value2 == NULL))) return NULL;

    char number[32];
    const int digits = snprintf(number, sizeof(number), "%ld", (long)timestamp);
    if (digits < 0 || (size_t)digits >= sizeof(number)) return NULL;

    const size_t value1_len = piano_json_escaped_size(value1);
    const size_t value2_len = value2 == NULL ? 0u : piano_json_escaped_size(value2);
    const size_t auth_len = piano_json_escaped_size(ph->user.authToken);
    size_t capacity = 64u;
    if (!piano_mutation_size_add(&capacity, strlen(key1)) ||
            !piano_mutation_size_add(&capacity, value1_len) ||
            !piano_mutation_size_add(&capacity, auth_len) ||
            !piano_mutation_size_add(&capacity, (size_t)digits) ||
            (key2 != NULL && (!piano_mutation_size_add(&capacity, strlen(key2)) ||
                !piano_mutation_size_add(&capacity, value2_len)))) {
        return NULL;
    }

    char *json = (char *)malloc(capacity + 1u);
    if (json == NULL) return NULL;
    char *out = piano_copy_literal(json, "{ \"");
    out = piano_copy_literal(out, key1);
    out = piano_copy_literal(out, "\": \"");
    out = piano_json_write_escaped(out, value1);
    if (key2 != NULL) {
        out = piano_copy_literal(out, "\", \"");
        out = piano_copy_literal(out, key2);
        out = piano_copy_literal(out, "\": \"");
        out = piano_json_write_escaped(out, value2);
    }
    out = piano_copy_literal(out, "\", \"userAuthToken\": \"");
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, "\", \"syncTime\": ");
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, " }");
    *out = '\0';
    assert((size_t)(out - json) <= capacity);
    return json;
}

static int piano_mutation_ready(const PianoHandle_t *ph, const PianoRequest_t *req) {
    return ph != NULL && req != NULL && ph->user.authToken != NULL &&
        ph->user.listenerId != NULL && ph->partner.out != NULL;
}

static int32_t piano_mutation_timestamp(const PianoHandle_t *ph) {
    return (int32_t)((int64_t)time(NULL) - (int64_t)ph->timeOffset);
}

static PianoReturn_t piano_request_rename_station(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataRenameStation_t *data = (PianoRequestDataRenameStation_t *)req->data;
    if (data == NULL || data->station == NULL || data->station->id == NULL ||
            data->newName == NULL) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_RENAME_STATION;
    req->secure = false;
    char *json = piano_mutation_json(ph, "stationToken", data->station->id,
        "stationName", data->newName, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "station.renameStation", json);
}

static PianoReturn_t piano_request_delete_station(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoStation_t *station = (PianoStation_t *)req->data;
    if (station == NULL || station->id == NULL) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_DELETE_STATION;
    req->secure = false;
    char *json = piano_mutation_json(ph, "stationToken", station->id,
        NULL, NULL, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "station.deleteStation", json);
}

static PianoReturn_t piano_request_create_station(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataCreateStation_t *data = (PianoRequestDataCreateStation_t *)req->data;
    if (data == NULL || data->token == NULL) return PIANO_RET_ERR;

    const char *key1;
    const char *key2 = NULL;
    const char *value2 = NULL;
    switch (data->type) {
        case PIANO_MUSICTYPE_INVALID:
            key1 = "musicToken";
            break;
        case PIANO_MUSICTYPE_SONG:
            key1 = "trackToken";
            key2 = "musicType";
            value2 = "song";
            break;
        case PIANO_MUSICTYPE_ARTIST:
            key1 = "trackToken";
            key2 = "musicType";
            value2 = "artist";
            break;
        default:
            return PIANO_RET_ERR;
    }
    req->type = PIANO_REQUEST_CREATE_STATION;
    req->secure = false;
    char *json = piano_mutation_json(ph, key1, data->token, key2, value2,
        piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "station.createStation", json);
}

static PianoReturn_t piano_mutation_station_from_json(const MelodicaJsonDoc *doc,
        const size_t object, PianoStation_t **out) {
    *out = NULL;
    if (object == MELODICA_JSON_NONE || object >= doc->count ||
            doc->tokens[object].type != MELODICA_JSON_OBJECT) {
        return PIANO_RET_INVALID_RESPONSE;
    }
    PianoStation_t *station = (PianoStation_t *)calloc(1u, sizeof(*station));
    if (station == NULL) return PIANO_RET_OUT_OF_MEMORY;

    const size_t name_token = melodica_json_object_get(doc, object, "stationName");
    if (name_token != MELODICA_JSON_NONE) {
        if (name_token >= doc->count || melodica_json_is_null(doc, name_token)) {
            free(station);
            return PIANO_RET_INVALID_RESPONSE;
        }
        station->name = melodica_json_string_dup(doc, name_token);
        if (station->name == NULL) { free(station); return PIANO_RET_OUT_OF_MEMORY; }
    }
    const size_t id_token = melodica_json_object_get(doc, object, "stationToken");
    if (id_token != MELODICA_JSON_NONE) {
        if (id_token >= doc->count || melodica_json_is_null(doc, id_token)) {
            destroy_station(station); free(station); return PIANO_RET_INVALID_RESPONSE;
        }
        station->id = melodica_json_string_dup(doc, id_token);
        if (station->id == NULL) {
            destroy_station(station); free(station); return PIANO_RET_OUT_OF_MEMORY;
        }
    }
    station->isCreator = (char)!piano_json_bool_default(doc, object, "isShared", 1);
    station->isQuickMix = (char)piano_json_bool_default(doc, object, "isQuickMix", 0);
    *out = station;
    return PIANO_RET_OK;
}

static PianoReturn_t piano_response_rename_station(PianoRequest_t *req) {
    PianoRequestDataRenameStation_t *data = (PianoRequestDataRenameStation_t *)req->data;
    if (data == NULL || data->station == NULL || data->newName == NULL) {
        return PIANO_RET_INVALID_RESPONSE;
    }
    char *name = piano_dup(data->newName);
    if (name == NULL) return PIANO_RET_OUT_OF_MEMORY;
    free(data->station->name);
    data->station->name = name;
    return PIANO_RET_OK;
}

static PianoReturn_t piano_response_delete_station(PianoHandle_t *ph, PianoRequest_t *req) {
    PianoStation_t *station = (PianoStation_t *)req->data;
    if (station == NULL || ph->stations == NULL) return PIANO_RET_INVALID_RESPONSE;
    ph->stations = (PianoStation_t *)PianoListDelete(&ph->stations->head, &station->head);
    destroy_station(station);
    free(station);
    return PIANO_RET_OK;
}

static PianoReturn_t piano_response_create_station(PianoHandle_t *ph,
        const MelodicaJsonDoc *doc, const size_t result) {
    PianoStation_t *station = NULL;
    PianoReturn_t ret = piano_mutation_station_from_json(doc, result, &station);
    if (ret != PIANO_RET_OK) return ret;

    PianoStation_t *existing = NULL;
    if (ph->stations != NULL && station->id != NULL) {
        existing = PianoFindStationById(ph->stations, station->id);
    }
    if (existing != NULL) {
        ph->stations = (PianoStation_t *)PianoListDelete(&ph->stations->head, &existing->head);
        destroy_station(existing);
        free(existing);
    }
    ph->stations = (PianoStation_t *)PianoListAppend(
        ph->stations == NULL ? NULL : &ph->stations->head, &station->head);
    return PIANO_RET_OK;
}

static PianoReturn_t piano_response_station_mutation(PianoHandle_t *ph, PianoRequest_t *req) {
    if (ph == NULL || req == NULL || req->responseData == NULL) {
        return PIANO_RET_INVALID_RESPONSE;
    }
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
        case PIANO_REQUEST_RENAME_STATION:
            ret = piano_response_rename_station(req);
            break;
        case PIANO_REQUEST_DELETE_STATION:
            ret = piano_response_delete_station(ph, req);
            break;
        case PIANO_REQUEST_CREATE_STATION:
            ret = piano_response_create_station(ph, &doc, result);
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
