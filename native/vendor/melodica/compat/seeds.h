#ifndef MELODICA_PIANO_SEEDS_INTERNAL_H
#define MELODICA_PIANO_SEEDS_INTERNAL_H

#include <stdint.h>

static PianoReturn_t piano_request_add_seed(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataAddSeed_t *data = (PianoRequestDataAddSeed_t *)req->data;
    if (data == NULL || data->station == NULL || data->station->id == NULL ||
            data->musicId == NULL) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_ADD_SEED;
    req->secure = false;
    char *json = piano_mutation_json(ph, "musicToken", data->musicId,
        "stationToken", data->station->id, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "station.addMusic", json);
}

static PianoReturn_t piano_request_delete_feedback(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoSong_t *song = (PianoSong_t *)req->data;
    if (song == NULL || song->feedbackId == NULL) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_DELETE_FEEDBACK;
    req->secure = false;
    char *json = piano_mutation_json(ph, "feedbackId", song->feedbackId,
        NULL, NULL, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "station.deleteFeedback", json);
}

static PianoReturn_t piano_request_delete_seed(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataDeleteSeed_t *data = (PianoRequestDataDeleteSeed_t *)req->data;
    if (data == NULL) return PIANO_RET_ERR;
    const char *seed_id = NULL;
    if (data->song != NULL) seed_id = data->song->seedId;
    else if (data->artist != NULL) seed_id = data->artist->seedId;
    else if (data->station != NULL) seed_id = data->station->seedId;
    if (seed_id == NULL) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_DELETE_SEED;
    req->secure = false;
    char *json = piano_mutation_json(ph, "seedId", seed_id,
        NULL, NULL, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "station.deleteMusic", json);
}

static PianoReturn_t piano_response_seed_mutation(PianoHandle_t *ph, PianoRequest_t *req) {
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
    switch (req->type) {
        case PIANO_REQUEST_ADD_SEED:
        case PIANO_REQUEST_DELETE_FEEDBACK:
        case PIANO_REQUEST_DELETE_SEED:
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

static PianoReturn_t piano_request_station_info(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataGetStationInfo_t *data = (PianoRequestDataGetStationInfo_t *)req->data;
    if (data == NULL || data->station == NULL || data->station->id == NULL) {
        return PIANO_RET_ERR;
    }

    static const char a[] = "{ \"stationToken\": \"";
    static const char b[] = "\", \"includeExtendedAttributes\": true, \"includeExtraParams\": true, \"userAuthToken\": \"";
    static const char c[] = "\", \"syncTime\": ";
    static const char d[] = " }";
    const int32_t timestamp = piano_mutation_timestamp(ph);
    char number[32];
    const int digits = snprintf(number, sizeof(number), "%ld", (long)timestamp);
    if (digits < 0 || (size_t)digits >= sizeof(number)) return PIANO_RET_ERR;

    size_t length = sizeof(a) + sizeof(b) + sizeof(c) + sizeof(d) - 4u + (size_t)digits;
    const size_t station_len = piano_json_escaped_size(data->station->id);
    const size_t auth_len = piano_json_escaped_size(ph->user.authToken);
    if (station_len > SIZE_MAX - length - 1u) return PIANO_RET_OUT_OF_MEMORY;
    length += station_len;
    if (auth_len > SIZE_MAX - length - 1u) return PIANO_RET_OUT_OF_MEMORY;
    length += auth_len;

    char *json = (char *)malloc(length + 1u);
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    char *out = piano_copy_literal(json, a);
    out = piano_json_write_escaped(out, data->station->id);
    out = piano_copy_literal(out, b);
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, c);
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, d);
    *out = '\0';
    assert((size_t)(out - json) == length);

    req->type = PIANO_REQUEST_GET_STATION_INFO;
    req->secure = false;
    return piano_authenticated_request(ph, req, "station.getStation", json);
}

static void piano_station_info_append_songs(PianoSong_t **dst, PianoSong_t *src) {
    if (src == NULL) return;
    if (*dst == NULL) {
        *dst = src;
        return;
    }
    PianoSong_t *tail = *dst;
    while (tail->head.next != NULL) tail = (PianoSong_t *)tail->head.next;
    tail->head.next = &src->head;
}

static void piano_station_info_append_artists(PianoArtist_t **dst, PianoArtist_t *src) {
    if (src == NULL) return;
    if (*dst == NULL) {
        *dst = src;
        return;
    }
    PianoArtist_t *tail = *dst;
    while (tail->head.next != NULL) tail = (PianoArtist_t *)tail->head.next;
    tail->head.next = &src->head;
}

static PianoReturn_t piano_station_info_song_seeds(const MelodicaJsonDoc *doc,
        const size_t music, PianoSong_t **out) {
    *out = NULL;
    const size_t songs = melodica_json_object_get(doc, music, "songs");
    if (songs == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (songs >= doc->count || doc->tokens[songs].type != MELODICA_JSON_ARRAY) {
        return PIANO_RET_INVALID_RESPONSE;
    }

    PianoSong_t *tail = NULL;
    PianoReturn_t ret = PIANO_RET_OK;
    for (size_t item = songs + 1u; item < doc->count &&
            doc->tokens[item].start < doc->tokens[songs].end; ++item) {
        if (doc->tokens[item].parent != songs) continue;
        if (melodica_json_is_null(doc, item)) {
            ret = PIANO_RET_INVALID_RESPONSE;
            goto fail;
        }
        PianoSong_t *song = (PianoSong_t *)calloc(1u, sizeof(*song));
        if (song == NULL) { ret = PIANO_RET_OUT_OF_MEMORY; goto fail; }
        if (tail == NULL) *out = song;
        else tail->head.next = &song->head;
        tail = song;

        ret = piano_playlist_string(doc, item, "songName", &song->title);
        if (ret != PIANO_RET_OK) goto fail;
        ret = piano_playlist_string(doc, item, "artistName", &song->artist);
        if (ret != PIANO_RET_OK) goto fail;
        ret = piano_playlist_string(doc, item, "seedId", &song->seedId);
        if (ret != PIANO_RET_OK) goto fail;
    }
    return PIANO_RET_OK;
fail:
    PianoDestroyPlaylist(*out);
    *out = NULL;
    return ret;
}

static PianoReturn_t piano_station_info_artist_seeds(const MelodicaJsonDoc *doc,
        const size_t music, PianoArtist_t **out) {
    *out = NULL;
    const size_t artists = melodica_json_object_get(doc, music, "artists");
    if (artists == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (artists >= doc->count || doc->tokens[artists].type != MELODICA_JSON_ARRAY) {
        return PIANO_RET_INVALID_RESPONSE;
    }

    PianoArtist_t *tail = NULL;
    PianoReturn_t ret = PIANO_RET_OK;
    for (size_t item = artists + 1u; item < doc->count &&
            doc->tokens[item].start < doc->tokens[artists].end; ++item) {
        if (doc->tokens[item].parent != artists) continue;
        if (melodica_json_is_null(doc, item)) {
            ret = PIANO_RET_INVALID_RESPONSE;
            goto fail;
        }
        PianoArtist_t *artist = (PianoArtist_t *)calloc(1u, sizeof(*artist));
        if (artist == NULL) { ret = PIANO_RET_OUT_OF_MEMORY; goto fail; }
        if (tail == NULL) *out = artist;
        else tail->head.next = &artist->head;
        tail = artist;

        ret = piano_playlist_string(doc, item, "artistName", &artist->name);
        if (ret != PIANO_RET_OK) goto fail;
        ret = piano_playlist_string(doc, item, "seedId", &artist->seedId);
        if (ret != PIANO_RET_OK) goto fail;
    }
    return PIANO_RET_OK;
fail:
    destroy_artists(*out);
    *out = NULL;
    return ret;
}

static PianoReturn_t piano_station_info_feedback(const MelodicaJsonDoc *doc,
        const size_t feedback, PianoSong_t **out) {
    *out = NULL;
    static const char *const keys[] = {"thumbsUp", "thumbsDown"};
    PianoSong_t *tail = NULL;
    PianoReturn_t ret = PIANO_RET_OK;

    for (size_t key = 0u; key < sizeof(keys) / sizeof(keys[0]); ++key) {
        const size_t array = melodica_json_object_get(doc, feedback, keys[key]);
        if (array == MELODICA_JSON_NONE) continue;
        if (array >= doc->count || doc->tokens[array].type != MELODICA_JSON_ARRAY) {
            ret = PIANO_RET_INVALID_RESPONSE;
            goto fail;
        }
        for (size_t item = array + 1u; item < doc->count &&
                doc->tokens[item].start < doc->tokens[array].end; ++item) {
            if (doc->tokens[item].parent != array) continue;
            if (melodica_json_is_null(doc, item)) {
                ret = PIANO_RET_INVALID_RESPONSE;
                goto fail;
            }
            PianoSong_t *song = (PianoSong_t *)calloc(1u, sizeof(*song));
            if (song == NULL) { ret = PIANO_RET_OUT_OF_MEMORY; goto fail; }
            if (tail == NULL) *out = song;
            else tail->head.next = &song->head;
            tail = song;

            ret = piano_playlist_string(doc, item, "songName", &song->title);
            if (ret != PIANO_RET_OK) goto fail;
            ret = piano_playlist_string(doc, item, "artistName", &song->artist);
            if (ret != PIANO_RET_OK) goto fail;
            ret = piano_playlist_string(doc, item, "feedbackId", &song->feedbackId);
            if (ret != PIANO_RET_OK) goto fail;
            song->rating = piano_json_bool_default(doc, item, "isPositive", 0)
                ? PIANO_RATE_LOVE : PIANO_RATE_BAN;
            double number;
            ret = piano_playlist_number(doc, item, "trackLength", 1, &number);
            if (ret != PIANO_RET_OK) goto fail;
            song->length = (unsigned int)(int)number;
        }
    }
    return PIANO_RET_OK;
fail:
    PianoDestroyPlaylist(*out);
    *out = NULL;
    return ret;
}

static PianoReturn_t piano_response_station_info_data(PianoRequest_t *req,
        const MelodicaJsonDoc *doc, const size_t result) {
    PianoRequestDataGetStationInfo_t *data = (PianoRequestDataGetStationInfo_t *)req->data;
    if (data == NULL) return PIANO_RET_INVALID_RESPONSE;
    if (result == MELODICA_JSON_NONE) return PIANO_RET_OK;

    PianoStationInfo_t parsed;
    memset(&parsed, 0, sizeof(parsed));
    PianoReturn_t ret = PIANO_RET_OK;
    const size_t music = melodica_json_object_get(doc, result, "music");
    if (music != MELODICA_JSON_NONE) {
        ret = piano_station_info_song_seeds(doc, music, &parsed.songSeeds);
        if (ret != PIANO_RET_OK) goto fail;
        ret = piano_station_info_artist_seeds(doc, music, &parsed.artistSeeds);
        if (ret != PIANO_RET_OK) goto fail;
    }

    const size_t feedback = melodica_json_object_get(doc, result, "feedback");
    if (feedback != MELODICA_JSON_NONE) {
        ret = piano_station_info_feedback(doc, feedback, &parsed.feedback);
        if (ret != PIANO_RET_OK) goto fail;
    }

    piano_station_info_append_songs(&data->info.songSeeds, parsed.songSeeds);
    parsed.songSeeds = NULL;
    piano_station_info_append_artists(&data->info.artistSeeds, parsed.artistSeeds);
    parsed.artistSeeds = NULL;
    piano_station_info_append_songs(&data->info.feedback, parsed.feedback);
    parsed.feedback = NULL;
    return PIANO_RET_OK;
fail:
    PianoDestroyStationInfo(&parsed);
    return ret;
}

static PianoReturn_t piano_response_station_info(PianoHandle_t *ph, PianoRequest_t *req) {
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
    ret = piano_response_station_info_data(req, &doc,
        melodica_json_object_get(&doc, root, "result"));
cleanup:
    melodica_json_destroy(&doc);
    return ret;
}

#endif
