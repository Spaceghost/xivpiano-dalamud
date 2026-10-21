#ifndef MELODICA_PIANO_SEARCH_INTERNAL_H
#define MELODICA_PIANO_SEARCH_INTERNAL_H

/* Internal implementation included by piano.c after its wire/ownership helpers.
 * This is not an installed header and does not add public ABI symbols. */
#include <stdint.h>

static PianoReturn_t piano_request_search(PianoHandle_t *ph, PianoRequest_t *req) {
    PianoRequestDataSearch_t *data = (PianoRequestDataSearch_t *)req->data;
    if (ph == NULL || data == NULL || data->searchStr == NULL ||
            ph->user.authToken == NULL || ph->user.listenerId == NULL ||
            ph->partner.out == NULL) return PIANO_RET_ERR;

    static const char a[] = "{ \"searchText\": \"";
    static const char b[] = "\", \"userAuthToken\": \"";
    static const char c[] = "\", \"syncTime\": ";
    static const char d[] = " }";
    const int32_t timestamp = (int32_t)((int64_t)time(NULL) - ph->timeOffset);
    char number[32];
    const int digits = snprintf(number, sizeof(number), "%ld", (long)timestamp);
    if (digits < 0 || (size_t)digits >= sizeof(number)) return PIANO_RET_ERR;

    size_t length = sizeof(a) + sizeof(b) + sizeof(c) + sizeof(d) - 4u + (size_t)digits;
    const size_t search_len = piano_json_escaped_size(data->searchStr);
    const size_t auth_len = piano_json_escaped_size(ph->user.authToken);
    if (search_len > SIZE_MAX - length - 1u) return PIANO_RET_OUT_OF_MEMORY;
    length += search_len;
    if (auth_len > SIZE_MAX - length - 1u) return PIANO_RET_OUT_OF_MEMORY;
    length += auth_len;

    char *json = (char *)malloc(length + 1u);
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    char *out = piano_copy_literal(json, a);
    out = piano_json_write_escaped(out, data->searchStr);
    out = piano_copy_literal(out, b);
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, c);
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, d);
    *out = '\0';
    assert((size_t)(out - json) == length);
    return piano_authenticated_request(ph, req, "music.search", json);
}

static PianoReturn_t piano_search_string(const MelodicaJsonDoc *doc,
        const size_t object, const char *key, char **out) {
    const size_t token = melodica_json_object_get(doc, object, key);
    *out = NULL;
    if (token == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (token >= doc->count || melodica_json_is_null(doc, token)) {
        return PIANO_RET_INVALID_RESPONSE;
    }
    *out = melodica_json_string_dup(doc, token);
    return *out == NULL ? PIANO_RET_OUT_OF_MEMORY : PIANO_RET_OK;
}

static PianoReturn_t piano_response_search(PianoRequest_t *req,
        const MelodicaJsonDoc *doc, const size_t result) {
    PianoRequestDataSearch_t *data = (PianoRequestDataSearch_t *)req->data;
    if (data == NULL) return PIANO_RET_INVALID_RESPONSE;

    PianoSearchResult_t *search = &data->searchResult;
    /* Upstream clears the caller's result before looking up artists/songs.
     * json-c object lookup on missing/null/non-object result simply finds no
     * members, so those shapes are successful empty searches. */
    memset(search, 0, sizeof(*search));
    if (result == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (result >= doc->count) return PIANO_RET_INVALID_RESPONSE;
    if (doc->tokens[result].type != MELODICA_JSON_OBJECT) return PIANO_RET_OK;

    PianoArtist_t *artist_tail = NULL;
    PianoSong_t *song_tail = NULL;
    PianoReturn_t ret = PIANO_RET_OK;

    const size_t artists = melodica_json_object_get(doc, result, "artists");
    if (artists != MELODICA_JSON_NONE) {
        if (artists >= doc->count || doc->tokens[artists].type != MELODICA_JSON_ARRAY) {
            return PIANO_RET_INVALID_RESPONSE;
        }
        for (size_t item = artists + 1u; item < doc->count &&
                doc->tokens[item].start < doc->tokens[artists].end; ++item) {
            if (doc->tokens[item].parent != artists) continue;
            if (melodica_json_is_null(doc, item)) {
                ret = PIANO_RET_INVALID_RESPONSE;
                goto fail;
            }
            PianoArtist_t *artist = (PianoArtist_t *)calloc(1u, sizeof(*artist));
            if (artist == NULL) { ret = PIANO_RET_OUT_OF_MEMORY; goto fail; }
            if (artist_tail == NULL) search->artists = artist;
            else artist_tail->head.next = &artist->head;
            artist_tail = artist;

            ret = piano_search_string(doc, item, "artistName", &artist->name);
            if (ret != PIANO_RET_OK) goto fail;
            ret = piano_search_string(doc, item, "musicToken", &artist->musicId);
            if (ret != PIANO_RET_OK) goto fail;
        }
    }

    const size_t songs = melodica_json_object_get(doc, result, "songs");
    if (songs != MELODICA_JSON_NONE) {
        if (songs >= doc->count || doc->tokens[songs].type != MELODICA_JSON_ARRAY) {
            ret = PIANO_RET_INVALID_RESPONSE;
            goto fail;
        }
        for (size_t item = songs + 1u; item < doc->count &&
                doc->tokens[item].start < doc->tokens[songs].end; ++item) {
            if (doc->tokens[item].parent != songs) continue;
            if (melodica_json_is_null(doc, item)) {
                ret = PIANO_RET_INVALID_RESPONSE;
                goto fail;
            }
            PianoSong_t *song = (PianoSong_t *)calloc(1u, sizeof(*song));
            if (song == NULL) { ret = PIANO_RET_OUT_OF_MEMORY; goto fail; }
            if (song_tail == NULL) search->songs = song;
            else song_tail->head.next = &song->head;
            song_tail = song;

            ret = piano_search_string(doc, item, "songName", &song->title);
            if (ret != PIANO_RET_OK) goto fail;
            ret = piano_search_string(doc, item, "artistName", &song->artist);
            if (ret != PIANO_RET_OK) goto fail;
            ret = piano_search_string(doc, item, "musicToken", &song->musicId);
            if (ret != PIANO_RET_OK) goto fail;
        }
    }
    return PIANO_RET_OK;

fail:
    PianoDestroySearchResult(search);
    memset(search, 0, sizeof(*search));
    return ret;
}

#endif
