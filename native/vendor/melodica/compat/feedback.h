#ifndef MELODICA_PIANO_FEEDBACK_INTERNAL_H
#define MELODICA_PIANO_FEEDBACK_INTERNAL_H

#include <stdint.h>

static char *piano_feedback_json(const PianoHandle_t *ph,
        const PianoRequestDataAddFeedback_t *data, const int32_t timestamp) {
    static const char a[] = "{ \"stationToken\": \"";
    static const char b[] = "\", \"trackToken\": \"";
    static const char c[] = "\", \"isPositive\": ";
    static const char d[] = ", \"userAuthToken\": \"";
    static const char e[] = "\", \"syncTime\": ";
    static const char f[] = " }";
    if (ph == NULL || data == NULL || data->stationId == NULL ||
            data->trackToken == NULL || ph->user.authToken == NULL ||
            (data->rating != PIANO_RATE_LOVE && data->rating != PIANO_RATE_BAN)) {
        return NULL;
    }

    char number[32];
    const int digits = snprintf(number, sizeof(number), "%ld", (long)timestamp);
    if (digits < 0 || (size_t)digits >= sizeof(number)) return NULL;
    const char *positive = data->rating == PIANO_RATE_LOVE ? "true" : "false";

    const size_t len = (sizeof(a) - 1u) + piano_json_escaped_size(data->stationId) +
        (sizeof(b) - 1u) + piano_json_escaped_size(data->trackToken) +
        (sizeof(c) - 1u) + strlen(positive) + (sizeof(d) - 1u) +
        piano_json_escaped_size(ph->user.authToken) + (sizeof(e) - 1u) +
        (size_t)digits + (sizeof(f) - 1u);
    char *json = (char *)malloc(len + 1u);
    if (json == NULL) return NULL;

    char *out = piano_copy_literal(json, a);
    out = piano_json_write_escaped(out, data->stationId);
    out = piano_copy_literal(out, b);
    out = piano_json_write_escaped(out, data->trackToken);
    out = piano_copy_literal(out, c);
    out = piano_copy_literal(out, positive);
    out = piano_copy_literal(out, d);
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, e);
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, f);
    *out = '\0';
    assert((size_t)(out - json) == len);
    return json;
}

static int piano_feedback_ready(const PianoHandle_t *ph, const PianoRequest_t *req) {
    return ph != NULL && req != NULL && ph->user.authToken != NULL &&
        ph->user.listenerId != NULL && ph->partner.out != NULL;
}

static int32_t piano_feedback_timestamp(const PianoHandle_t *ph) {
    return (int32_t)((int64_t)time(NULL) - (int64_t)ph->timeOffset);
}

static PianoReturn_t piano_request_add_feedback(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_feedback_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataAddFeedback_t *data = (PianoRequestDataAddFeedback_t *)req->data;
    if (data == NULL || data->stationId == NULL || data->trackToken == NULL ||
            (data->rating != PIANO_RATE_LOVE && data->rating != PIANO_RATE_BAN)) {
        return PIANO_RET_ERR;
    }
    req->type = PIANO_REQUEST_ADD_FEEDBACK;
    req->secure = false;
    char *json = piano_feedback_json(ph, data, piano_feedback_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "station.addFeedback", json);
}

static PianoReturn_t piano_request_rate_song(PianoHandle_t *ph, PianoRequest_t *req) {
    if (req == NULL) return PIANO_RET_ERR;
    PianoRequestDataRateSong_t *data = (PianoRequestDataRateSong_t *)req->data;
    if (data == NULL || data->song == NULL || data->song->stationId == NULL ||
            data->song->trackToken == NULL ||
            (data->rating != PIANO_RATE_LOVE && data->rating != PIANO_RATE_BAN)) {
        return PIANO_RET_ERR;
    }

    PianoRequestDataAddFeedback_t transformed;
    transformed.stationId = data->song->stationId;
    transformed.trackToken = data->song->trackToken;
    transformed.rating = data->rating;
    void *const original_data = req->data;
    req->data = &transformed;
    const PianoReturn_t ret = piano_request_add_feedback(ph, req);
    req->type = PIANO_REQUEST_RATE_SONG;
    req->data = original_data;
    return ret;
}

static PianoReturn_t piano_request_tired_song(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_feedback_ready(ph, req)) return PIANO_RET_ERR;
    PianoSong_t *song = (PianoSong_t *)req->data;
    if (song == NULL || song->trackToken == NULL) return PIANO_RET_ERR;
    req->type = PIANO_REQUEST_ADD_TIRED_SONG;
    req->secure = false;
    char *json = piano_mutation_json(ph, "trackToken", song->trackToken,
        NULL, NULL, piano_feedback_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "user.sleepSong", json);
}

static PianoReturn_t piano_response_feedback(PianoHandle_t *ph, PianoRequest_t *req) {
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
        case PIANO_REQUEST_RATE_SONG: {
            PianoRequestDataRateSong_t *data = (PianoRequestDataRateSong_t *)req->data;
            if (data == NULL || data->song == NULL ||
                    (data->rating != PIANO_RATE_LOVE && data->rating != PIANO_RATE_BAN)) {
                ret = PIANO_RET_INVALID_RESPONSE;
                break;
            }
            data->song->rating = data->rating;
            ret = PIANO_RET_OK;
            break;
        }
        case PIANO_REQUEST_ADD_TIRED_SONG: {
            PianoSong_t *song = (PianoSong_t *)req->data;
            if (song == NULL) {
                ret = PIANO_RET_INVALID_RESPONSE;
                break;
            }
            song->rating = PIANO_RATE_TIRED;
            ret = PIANO_RET_OK;
            break;
        }
        case PIANO_REQUEST_ADD_FEEDBACK:
            /* Upstream asserts here: this low-level request has no public success response contract. */
            ret = PIANO_RET_ERR;
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
