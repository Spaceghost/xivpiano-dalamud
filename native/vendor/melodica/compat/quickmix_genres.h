#ifndef MELODICA_PIANO_QUICKMIX_GENRES_INTERNAL_H
#define MELODICA_PIANO_QUICKMIX_GENRES_INTERNAL_H

#include <stdint.h>

static char *piano_quickmix_json(const PianoHandle_t *ph, const int32_t timestamp) {
    static const char prefix[] = "{ \"quickMixStationIds\": [";
    static const char auth_prefix[] = " ], \"userAuthToken\": \"";
    static const char time_prefix[] = "\", \"syncTime\": ";
    static const char suffix[] = " }";
    if (ph == NULL || ph->user.authToken == NULL) return NULL;

    char number[32];
    const int digits = snprintf(number, sizeof(number), "%ld", (long)timestamp);
    if (digits < 0 || (size_t)digits >= sizeof(number)) return NULL;

    size_t length = (sizeof(prefix) - 1u) + (sizeof(auth_prefix) - 1u) +
        piano_json_escaped_size(ph->user.authToken) + (sizeof(time_prefix) - 1u) +
        (size_t)digits + (sizeof(suffix) - 1u);
    int first = 1;
    for (const PianoStation_t *station = ph->stations; station != NULL;
            station = (const PianoStation_t *)station->head.next) {
        if (!station->useQuickMix || station->isQuickMix) continue;
        if (station->id == NULL) return NULL;
        const size_t escaped = piano_json_escaped_size(station->id);
        if (!piano_mutation_size_add(&length, escaped) ||
                !piano_mutation_size_add(&length, first ? 3u : 4u)) return NULL;
        first = 0;
    }

    char *json = (char *)malloc(length + 1u);
    if (json == NULL) return NULL;
    char *out = piano_copy_literal(json, prefix);
    first = 1;
    for (const PianoStation_t *station = ph->stations; station != NULL;
            station = (const PianoStation_t *)station->head.next) {
        if (!station->useQuickMix || station->isQuickMix) continue;
        out = piano_copy_literal(out, first ? " \"" : ", \"");
        out = piano_json_write_escaped(out, station->id);
        *out++ = '"';
        first = 0;
    }
    out = piano_copy_literal(out, auth_prefix);
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, time_prefix);
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, suffix);
    *out = '\0';
    assert((size_t)(out - json) == length);
    return json;
}

static char *piano_auth_only_json(const PianoHandle_t *ph, const int32_t timestamp) {
    static const char prefix[] = "{ \"userAuthToken\": \"";
    static const char time_prefix[] = "\", \"syncTime\": ";
    static const char suffix[] = " }";
    if (ph == NULL || ph->user.authToken == NULL) return NULL;

    char number[32];
    const int digits = snprintf(number, sizeof(number), "%ld", (long)timestamp);
    if (digits < 0 || (size_t)digits >= sizeof(number)) return NULL;
    size_t length = (sizeof(prefix) - 1u) + piano_json_escaped_size(ph->user.authToken) +
        (sizeof(time_prefix) - 1u) + (size_t)digits + (sizeof(suffix) - 1u);
    char *json = (char *)malloc(length + 1u);
    if (json == NULL) return NULL;
    char *out = piano_copy_literal(json, prefix);
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, time_prefix);
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, suffix);
    *out = '\0';
    assert((size_t)(out - json) == length);
    return json;
}

static PianoReturn_t piano_request_set_quickmix(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_SET_QUICKMIX;
    req->secure = false;
    char *json = piano_quickmix_json(ph, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "user.setQuickMix", json);
}

static PianoReturn_t piano_request_get_genre_stations(PianoHandle_t *ph,
        PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_GET_GENRE_STATIONS;
    req->secure = false;
    char *json = piano_auth_only_json(ph, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "station.getGenreStations", json);
}

static PianoReturn_t piano_request_transform_station(PianoHandle_t *ph,
        PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoStation_t *station = (PianoStation_t *)req->data;
    if (station == NULL || station->id == NULL) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_TRANSFORM_STATION;
    req->secure = false;
    char *json = piano_mutation_json(ph, "stationToken", station->id,
        NULL, NULL, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "station.transformSharedStation", json);
}

static void piano_destroy_genre_categories_local(PianoGenreCategory_t *categories) {
    while (categories != NULL) {
        PianoGenreCategory_t *next = (PianoGenreCategory_t *)categories->head.next;
        destroy_genres(categories->genres);
        free(categories->name);
        free(categories);
        categories = next;
    }
}

static PianoReturn_t piano_response_genres_data(PianoHandle_t *ph,
        const MelodicaJsonDoc *doc, const size_t result) {
    /* json-c's object lookup simply reports "not found" for NULL/non-object
     * results, so an otherwise-OK response without categories is still OK. */
    const size_t categories_token = melodica_json_object_get(doc, result, "categories");
    if (categories_token == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (categories_token >= doc->count ||
            doc->tokens[categories_token].type != MELODICA_JSON_ARRAY) {
        return PIANO_RET_INVALID_RESPONSE;
    }

    PianoGenreCategory_t *categories = NULL;
    PianoGenreCategory_t *category_tail = NULL;
    PianoReturn_t ret = PIANO_RET_OK;
    for (size_t item = categories_token + 1u; item < doc->count &&
            doc->tokens[item].start < doc->tokens[categories_token].end; ++item) {
        if (doc->tokens[item].parent != categories_token) continue;
        if (melodica_json_is_null(doc, item)) {
            ret = PIANO_RET_INVALID_RESPONSE;
            goto fail;
        }
        PianoGenreCategory_t *category = (PianoGenreCategory_t *)calloc(1u, sizeof(*category));
        if (category == NULL) { ret = PIANO_RET_OUT_OF_MEMORY; goto fail; }
        if (category_tail == NULL) categories = category;
        else category_tail->head.next = &category->head;
        category_tail = category;

        ret = piano_playlist_string(doc, item, "categoryName", &category->name);
        if (ret != PIANO_RET_OK) goto fail;
        const size_t stations = melodica_json_object_get(doc, item, "stations");
        if (stations == MELODICA_JSON_NONE) continue;
        if (stations >= doc->count || doc->tokens[stations].type != MELODICA_JSON_ARRAY) {
            ret = PIANO_RET_INVALID_RESPONSE;
            goto fail;
        }

        PianoGenre_t *genre_tail = NULL;
        for (size_t station_token = stations + 1u; station_token < doc->count &&
                doc->tokens[station_token].start < doc->tokens[stations].end; ++station_token) {
            if (doc->tokens[station_token].parent != stations) continue;
            if (melodica_json_is_null(doc, station_token)) {
                ret = PIANO_RET_INVALID_RESPONSE;
                goto fail;
            }
            PianoGenre_t *genre = (PianoGenre_t *)calloc(1u, sizeof(*genre));
            if (genre == NULL) { ret = PIANO_RET_OUT_OF_MEMORY; goto fail; }
            if (genre_tail == NULL) category->genres = genre;
            else genre_tail->head.next = &genre->head;
            genre_tail = genre;
            ret = piano_playlist_string(doc, station_token, "stationName", &genre->name);
            if (ret != PIANO_RET_OK) goto fail;
            ret = piano_playlist_string(doc, station_token, "stationToken", &genre->musicId);
            if (ret != PIANO_RET_OK) goto fail;
        }
    }

    if (categories != NULL) {
        if (ph->genreStations == NULL) {
            ph->genreStations = categories;
        } else {
            PianoGenreCategory_t *tail = ph->genreStations;
            while (tail->head.next != NULL) tail = (PianoGenreCategory_t *)tail->head.next;
            tail->head.next = &categories->head;
        }
    }
    return PIANO_RET_OK;
fail:
    piano_destroy_genre_categories_local(categories);
    return ret;
}

static PianoReturn_t piano_response_quickmix_genres(PianoHandle_t *ph,
        PianoRequest_t *req) {
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
        case PIANO_REQUEST_SET_QUICKMIX:
            ret = PIANO_RET_OK;
            break;
        case PIANO_REQUEST_GET_GENRE_STATIONS:
            ret = piano_response_genres_data(ph, &doc, result);
            break;
        case PIANO_REQUEST_TRANSFORM_STATION: {
            PianoStation_t *station = (PianoStation_t *)req->data;
            if (station == NULL) ret = PIANO_RET_INVALID_RESPONSE;
            else { station->isCreator = 1; ret = PIANO_RET_OK; }
            break;
        }
        default:
            ret = PIANO_RET_ERR;
            break;
    }
cleanup:
    melodica_json_destroy(&doc);
    return ret;
}

#endif
