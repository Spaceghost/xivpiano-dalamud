#include "piano.h"
#include "blowfish.h"
#include "crypt.h"
#include "json.h"

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static char *piano_dup(const char *s) {
    if (s == NULL) return NULL;
    const size_t n = strlen(s) + 1u;
    char *copy = (char *)malloc(n);
    if (copy != NULL) memcpy(copy, s, n);
    return copy;
}

static char piano_hex_upper(const unsigned char n) {
    return (char)(n < 10u ? '0' + (int)n : 'A' + (int)n - 10);
}

static int piano_url_unreserved(const unsigned char c) {
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
        (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~';
}

static char *piano_url_encode(const char *s) {
    if (s == NULL) return NULL;
    size_t needed = 0u;
    for (const unsigned char *p = (const unsigned char *)s; *p != '\0'; ++p) {
        needed += piano_url_unreserved(*p) ? 1u : 3u;
    }
    char *out = (char *)malloc(needed + 1u);
    if (out == NULL) return NULL;
    size_t n = 0u;
    for (const unsigned char *p = (const unsigned char *)s; *p != '\0'; ++p) {
        if (piano_url_unreserved(*p)) {
            out[n++] = (char)*p;
        } else {
            out[n++] = '%';
            out[n++] = piano_hex_upper((unsigned char)(*p >> 4u));
            out[n++] = piano_hex_upper((unsigned char)(*p & 0x0fu));
        }
    }
    out[n] = '\0';
    return out;
}

static char piano_hex(const unsigned char n) {
    return (char)(n < 10u ? '0' + (int)n : 'a' + (int)n - 10);
}

/* json-c's historical json_object_to_json_string() uses the SPACED format and
 * escapes '/'. libpiano encrypts that exact byte stream, so formatting is wire
 * behavior here rather than decoration. */
static size_t piano_json_escaped_size(const char *s) {
    size_t n = 0u;
    for (const unsigned char *p = (const unsigned char *)s; *p != '\0'; ++p) {
        switch (*p) {
            case '"': case '\\': case '/':
            case '\b': case '\f': case '\n': case '\r': case '\t':
                n += 2u;
                break;
            default:
                n += *p < 0x20u ? 6u : 1u;
                break;
        }
    }
    return n;
}

static char *piano_json_write_escaped(char *out, const char *s) {
    for (const unsigned char *p = (const unsigned char *)s; *p != '\0'; ++p) {
        const unsigned char c = *p;
        switch (c) {
            case '"': *out++ = '\\'; *out++ = '"'; break;
            case '\\': *out++ = '\\'; *out++ = '\\'; break;
            case '/': *out++ = '\\'; *out++ = '/'; break;
            case '\b': *out++ = '\\'; *out++ = 'b'; break;
            case '\f': *out++ = '\\'; *out++ = 'f'; break;
            case '\n': *out++ = '\\'; *out++ = 'n'; break;
            case '\r': *out++ = '\\'; *out++ = 'r'; break;
            case '\t': *out++ = '\\'; *out++ = 't'; break;
            default:
                if (c < 0x20u) {
                    *out++ = '\\'; *out++ = 'u'; *out++ = '0'; *out++ = '0';
                    *out++ = piano_hex((unsigned char)(c >> 4u));
                    *out++ = piano_hex((unsigned char)(c & 0x0fu));
                } else {
                    *out++ = (char)c;
                }
                break;
        }
    }
    return out;
}

static char *piano_copy_literal(char *out, const char *literal) {
    const size_t n = strlen(literal);
    memcpy(out, literal, n);
    return out + n;
}

static char *piano_partner_login_json(const PianoPartner_t *partner) {
    static const char a[] = "{ \"username\": \"";
    static const char b[] = "\", \"password\": \"";
    static const char c[] = "\", \"deviceModel\": \"";
    static const char d[] = "\", \"version\": \"5\", \"includeUrls\": true }";

    if (partner == NULL || partner->user == NULL ||
            partner->password == NULL || partner->device == NULL) return NULL;

    const size_t len = (sizeof(a) - 1u) + piano_json_escaped_size(partner->user) +
        (sizeof(b) - 1u) + piano_json_escaped_size(partner->password) +
        (sizeof(c) - 1u) + piano_json_escaped_size(partner->device) +
        (sizeof(d) - 1u);
    char *json = (char *)malloc(len + 1u);
    if (json == NULL) return NULL;

    char *out = json;
    out = piano_copy_literal(out, a);
    out = piano_json_write_escaped(out, partner->user);
    out = piano_copy_literal(out, b);
    out = piano_json_write_escaped(out, partner->password);
    out = piano_copy_literal(out, c);
    out = piano_json_write_escaped(out, partner->device);
    out = piano_copy_literal(out, d);
    *out = '\0';
    assert((size_t)(out - json) == len);
    return json;
}

static char *piano_user_login_json(const PianoHandle_t *ph,
        const PianoRequestDataLogin_t *login, const long timestamp) {
    static const char a[] = "{ \"loginType\": \"user\", \"username\": \"";
    static const char b[] = "\", \"password\": \"";
    static const char c[] = "\", \"partnerAuthToken\": \"";
    static const char d[] = "\", \"syncTime\": ";
    static const char e[] = " }";
    if (ph == NULL || login == NULL || login->user == NULL || login->password == NULL ||
            ph->partner.authToken == NULL) return NULL;

    char number[32];
    const int number_len = snprintf(number, sizeof(number), "%ld", (long)(int32_t)timestamp);
    if (number_len < 0 || (size_t)number_len >= sizeof(number)) return NULL;

    const size_t len = (sizeof(a) - 1u) + piano_json_escaped_size(login->user) +
        (sizeof(b) - 1u) + piano_json_escaped_size(login->password) +
        (sizeof(c) - 1u) + piano_json_escaped_size(ph->partner.authToken) +
        (sizeof(d) - 1u) + (size_t)number_len + (sizeof(e) - 1u);
    char *json = (char *)malloc(len + 1u);
    if (json == NULL) return NULL;
    char *out = json;
    out = piano_copy_literal(out, a);
    out = piano_json_write_escaped(out, login->user);
    out = piano_copy_literal(out, b);
    out = piano_json_write_escaped(out, login->password);
    out = piano_copy_literal(out, c);
    out = piano_json_write_escaped(out, ph->partner.authToken);
    out = piano_copy_literal(out, d);
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, e);
    *out = '\0';
    assert((size_t)(out - json) == len);
    return json;
}

static char *piano_station_list_json(const PianoHandle_t *ph, const long timestamp) {
    static const char a[] = "{ \"returnAllStations\": true, \"userAuthToken\": \"";
    static const char b[] = "\", \"syncTime\": ";
    static const char c[] = " }";
    if (ph == NULL || ph->user.authToken == NULL) return NULL;

    char number[32];
    const int number_len = snprintf(number, sizeof(number), "%ld", (long)(int32_t)timestamp);
    if (number_len < 0 || (size_t)number_len >= sizeof(number)) return NULL;

    const size_t len = (sizeof(a) - 1u) + piano_json_escaped_size(ph->user.authToken) +
        (sizeof(b) - 1u) + (size_t)number_len + (sizeof(c) - 1u);
    char *json = (char *)malloc(len + 1u);
    if (json == NULL) return NULL;
    char *out = json;
    out = piano_copy_literal(out, a);
    out = piano_json_write_escaped(out, ph->user.authToken);
    out = piano_copy_literal(out, b);
    out = piano_copy_literal(out, number);
    out = piano_copy_literal(out, c);
    *out = '\0';
    assert((size_t)(out - json) == len);
    return json;
}

static PianoReturn_t piano_authenticated_request(PianoHandle_t *ph,
        PianoRequest_t *req, const char *method, char *json) {
    if (ph == NULL || req == NULL || method == NULL || json == NULL ||
            ph->user.authToken == NULL || ph->user.listenerId == NULL ||
            ph->partner.out == NULL) {
        free(json);
        return PIANO_RET_ERR;
    }

    char *escaped = piano_url_encode(ph->user.authToken);
    if (escaped == NULL) {
        free(json);
        return PIANO_RET_OUT_OF_MEMORY;
    }
    const int written = snprintf(req->urlPath, sizeof(req->urlPath),
            PIANO_RPC_PATH "method=%s&auth_token=%s&partner_id=%i&user_id=%s",
            method, escaped, (int)ph->partner.id, ph->user.listenerId);
    free(escaped);
    if (written < 0) {
        free(json);
        return PIANO_RET_ERR;
    }

    char *encrypted = melodica_piano_encrypt_string(
            (const MelodicaBlowfish *)ph->partner.out, json);
    free(json);
    if (encrypted == NULL) return PIANO_RET_OUT_OF_MEMORY;
    free(req->postData);
    req->postData = encrypted;
    return PIANO_RET_OK;
}

size_t PianoListCount(const PianoListHead_t *l) {
    assert(l != NULL);
    size_t count = 0u;
    for (const PianoListHead_t *cur = l; cur != NULL; cur = cur->next) ++count;
    return count;
}

void *PianoListAppend(PianoListHead_t *l, PianoListHead_t *e) {
    assert(e != NULL);
    assert(e->next == NULL);
    if (l == NULL) return e;
    PianoListHead_t *cur = l;
    while (cur->next != NULL) cur = cur->next;
    cur->next = e;
    return l;
}

void *PianoListPrepend(PianoListHead_t *l, PianoListHead_t *e) {
    assert(e != NULL);
    assert(e->next == NULL);
    e->next = l;
    return e;
}

void *PianoListDelete(PianoListHead_t *l, PianoListHead_t *e) {
    assert(l != NULL);
    assert(e != NULL);
    PianoListHead_t *first = l, *cur = l, *prev = NULL;
    while (cur != NULL) {
        if (cur == e) {
            if (prev != NULL) prev->next = cur->next;
            else first = cur->next;
            break;
        }
        prev = cur;
        cur = cur->next;
    }
    return first;
}

void *PianoListGet(PianoListHead_t *l, size_t n) {
    for (PianoListHead_t *cur = l; cur != NULL; cur = cur->next) {
        if (n == 0u) return cur;
        --n;
    }
    return NULL;
}

static void destroy_artists(PianoArtist_t *artists) {
    while (artists != NULL) {
        PianoArtist_t *next = (PianoArtist_t *)artists->head.next;
        free(artists->name); free(artists->musicId); free(artists->seedId);
        free(artists);
        artists = next;
    }
}

static void destroy_station(PianoStation_t *station) {
    free(station->name); free(station->id); free(station->seedId);
    memset(station, 0, sizeof(*station));
}

static void destroy_stations(PianoStation_t *stations) {
    while (stations != NULL) {
        PianoStation_t *next = (PianoStation_t *)stations->head.next;
        destroy_station(stations);
        free(stations);
        stations = next;
    }
}

static void destroy_genres(PianoGenre_t *genres) {
    while (genres != NULL) {
        PianoGenre_t *next = (PianoGenre_t *)genres->head.next;
        free(genres->name); free(genres->musicId); free(genres);
        genres = next;
    }
}

static void destroy_cipher(PianoCipherHandle_t *handle) {
    if (handle == NULL || *handle == NULL) return;
    MelodicaBlowfish *ctx = (MelodicaBlowfish *)*handle;
    melodica_blowfish_clear(ctx);
    free(ctx);
    *handle = NULL;
}

void PianoDestroyPlaylist(PianoSong_t *playlist) {
    while (playlist != NULL) {
        PianoSong_t *next = (PianoSong_t *)playlist->head.next;
        free(playlist->audioUrl); free(playlist->coverArt); free(playlist->artist);
        free(playlist->musicId); free(playlist->title); free(playlist->stationId);
        free(playlist->album); free(playlist->feedbackId); free(playlist->seedId);
        free(playlist->detailUrl); free(playlist->trackToken);
        free(playlist);
        playlist = next;
    }
}

void PianoDestroySearchResult(PianoSearchResult_t *result) {
    if (result == NULL) return;
    destroy_artists(result->artists);
    PianoDestroyPlaylist(result->songs);
}

void PianoDestroyStationInfo(PianoStationInfo_t *info) {
    if (info == NULL) return;
    PianoDestroyPlaylist(info->feedback);
    PianoDestroyPlaylist(info->songSeeds);
    destroy_artists(info->artistSeeds);
    destroy_stations(info->stationSeeds);
}

void PianoDestroyStationMode(PianoStationMode_t *modes) {
    while (modes != NULL) {
        PianoStationMode_t *next = (PianoStationMode_t *)modes->head.next;
        free(modes->name); free(modes->description); free(modes);
        modes = next;
    }
}

PianoReturn_t PianoInit(PianoHandle_t *ph, const char *partnerUser,
        const char *partnerPassword, const char *device,
        const char *inkey, const char *outkey) {
    if (ph == NULL) return PIANO_RET_ERR;
    memset(ph, 0, sizeof(*ph));
    ph->partner.user = piano_dup(partnerUser);
    ph->partner.password = piano_dup(partnerPassword);
    ph->partner.device = piano_dup(device);
    if ((partnerUser != NULL && ph->partner.user == NULL) ||
        (partnerPassword != NULL && ph->partner.password == NULL) ||
        (device != NULL && ph->partner.device == NULL)) {
        PianoDestroy(ph);
        return PIANO_RET_OUT_OF_MEMORY;
    }
    if (inkey == NULL || outkey == NULL) {
        PianoDestroy(ph);
        return PIANO_RET_GCRY_ERR;
    }

    MelodicaBlowfish *incoming = (MelodicaBlowfish *)malloc(sizeof(*incoming));
    MelodicaBlowfish *outgoing = (MelodicaBlowfish *)malloc(sizeof(*outgoing));
    if (incoming == NULL || outgoing == NULL) {
        free(incoming);
        free(outgoing);
        PianoDestroy(ph);
        return PIANO_RET_OUT_OF_MEMORY;
    }
    if (!melodica_blowfish_init(incoming, inkey, strlen(inkey)) ||
            !melodica_blowfish_init(outgoing, outkey, strlen(outkey))) {
        melodica_blowfish_clear(incoming);
        melodica_blowfish_clear(outgoing);
        free(incoming);
        free(outgoing);
        PianoDestroy(ph);
        return PIANO_RET_GCRY_ERR;
    }
    ph->partner.in = incoming;
    ph->partner.out = outgoing;
    return PIANO_RET_OK;
}

void PianoDestroy(PianoHandle_t *ph) {
    if (ph == NULL) return;
    free(ph->user.authToken); free(ph->user.listenerId);
    destroy_stations(ph->stations);
    free(ph->partner.user); free(ph->partner.password); free(ph->partner.device);
    free(ph->partner.authToken);
    destroy_cipher(&ph->partner.in);
    destroy_cipher(&ph->partner.out);
    PianoGenreCategory_t *category = ph->genreStations;
    while (category != NULL) {
        PianoGenreCategory_t *next = (PianoGenreCategory_t *)category->head.next;
        destroy_genres(category->genres);
        free(category->name);
        free(category);
        category = next;
    }
    memset(ph, 0, sizeof(*ph));
}

void PianoDestroyRequest(PianoRequest_t *req) {
    if (req == NULL) return;
    free(req->postData);
    memset(req, 0, sizeof(*req));
}

#include "playlist.h"
#include "search.h"

PianoReturn_t PianoRequest(PianoHandle_t *ph, PianoRequest_t *req,
        const PianoRequestType_t type) {
    if (req == NULL) return PIANO_RET_ERR;
    req->type = type;
    req->secure = false;

    if (type == PIANO_REQUEST_LOGIN && ph != NULL && req->data != NULL) {
        PianoRequestDataLogin_t *login = (PianoRequestDataLogin_t *)req->data;
        if (login->step == 0u) {
            char *body = piano_partner_login_json(&ph->partner);
            if (body == NULL) {
                return (ph->partner.user == NULL || ph->partner.password == NULL ||
                        ph->partner.device == NULL) ? PIANO_RET_ERR : PIANO_RET_OUT_OF_MEMORY;
            }
            const int written = snprintf(req->urlPath, sizeof(req->urlPath),
                    PIANO_RPC_PATH "method=auth.partnerLogin");
            if (written < 0) {
                free(body);
                return PIANO_RET_ERR;
            }
            free(req->postData);
            req->postData = body;
            req->secure = true;
            return PIANO_RET_OK;
        }
        if (login->step == 1u) {
            const long timestamp = (long)time(NULL) - (long)ph->timeOffset;
            char *json = piano_user_login_json(ph, login, timestamp);
            char *escaped = piano_url_encode(ph->partner.authToken);
            if (json == NULL || escaped == NULL) {
                free(json);
                free(escaped);
                return PIANO_RET_OUT_OF_MEMORY;
            }
            const int written = snprintf(req->urlPath, sizeof(req->urlPath),
                    PIANO_RPC_PATH "method=auth.userLogin&auth_token=%s&partner_id=%i",
                    escaped, (int)ph->partner.id);
            free(escaped);
            if (written < 0) {
                free(json);
                return PIANO_RET_ERR;
            }
            char *encrypted = melodica_piano_encrypt_string(
                    (const MelodicaBlowfish *)ph->partner.out, json);
            free(json);
            if (encrypted == NULL) return PIANO_RET_OUT_OF_MEMORY;
            free(req->postData);
            req->postData = encrypted;
            req->secure = true;
            return PIANO_RET_OK;
        }
        return PIANO_RET_ERR;
    }

    if (type == PIANO_REQUEST_GET_PLAYLIST) {
        return piano_request_playlist(ph, req);
    }

    if (type == PIANO_REQUEST_SEARCH) {
        return piano_request_search(ph, req);
    }

    if (type == PIANO_REQUEST_GET_STATIONS && ph != NULL) {
        const long timestamp = (long)time(NULL) - (long)ph->timeOffset;
        char *json = piano_station_list_json(ph, timestamp);
        if (json == NULL) {
            return ph->user.authToken == NULL ? PIANO_RET_ERR : PIANO_RET_OUT_OF_MEMORY;
        }
        return piano_authenticated_request(ph, req, "user.getStationList", json);
    }

    return PIANO_RET_ERR;
}

static PianoReturn_t piano_response_error(const MelodicaJsonDoc *doc,
        const size_t root, const PianoRequest_t *req) {
    long code = 0;
    const size_t code_token = melodica_json_object_get(doc, root, "code");
    if (!melodica_json_long(doc, code_token, &code)) return PIANO_RET_INVALID_RESPONSE;
    const long mapped = code + PIANO_RET_OFFSET;
    PianoReturn_t ret = (PianoReturn_t)mapped;
    if (ret == PIANO_RET_P_INVALID_PARTNER_LOGIN && req->type == PIANO_REQUEST_LOGIN &&
            req->data != NULL && ((const PianoRequestDataLogin_t *)req->data)->step == 1u) {
        ret = PIANO_RET_INVALID_LOGIN;
    }
    return ret;
}

static PianoReturn_t piano_response_login(PianoHandle_t *ph, PianoRequest_t *req,
        const MelodicaJsonDoc *doc, const size_t result) {
    if (req->data == NULL || result == MELODICA_JSON_NONE || result >= doc->count ||
            melodica_json_is_null(doc, result)) {
        return PIANO_RET_INVALID_RESPONSE;
    }
    PianoRequestDataLogin_t *login = (PianoRequestDataLogin_t *)req->data;

    if (login->step == 0u) {
        const size_t sync_token = melodica_json_object_get(doc, result, "syncTime");
        if (sync_token == MELODICA_JSON_NONE) return PIANO_RET_INVALID_RESPONSE;
        char *crypted_timestamp = melodica_json_string_dup(doc, sync_token);
        if (crypted_timestamp == NULL) return PIANO_RET_INVALID_RESPONSE;
        size_t decrypted_size = 0u;
        char *decrypted = melodica_piano_decrypt_string(
                (const MelodicaBlowfish *)ph->partner.in,
                crypted_timestamp, &decrypted_size);
        free(crypted_timestamp);
        PianoReturn_t ret = PIANO_RET_ERR;
        if (decrypted != NULL && decrypted_size > 4u) {
            const unsigned long timestamp = strtoul(decrypted + 4, NULL, 0);
            ph->timeOffset = (int)((long)time(NULL) - (long)timestamp);
            ret = PIANO_RET_CONTINUE_REQUEST;
        }
        free(decrypted);
        const size_t auth_token = melodica_json_object_get(doc, result, "partnerAuthToken");
        char *partner_auth = melodica_json_string_dup(doc, auth_token);
        if (auth_token != MELODICA_JSON_NONE && partner_auth == NULL) {
            return melodica_json_is_null(doc, auth_token) ?
                PIANO_RET_INVALID_RESPONSE : PIANO_RET_OUT_OF_MEMORY;
        }
        free(ph->partner.authToken);
        ph->partner.authToken = partner_auth;
        const size_t id_token = melodica_json_object_get(doc, result, "partnerId");
        long partner_id;
        if (!melodica_json_long(doc, id_token, &partner_id)) return PIANO_RET_INVALID_RESPONSE;
        ph->partner.id = (unsigned int)partner_id;
        ++login->step;
        return ret;
    }

    if (login->step == 1u) {
        const size_t listener_token = melodica_json_object_get(doc, result, "userId");
        const size_t auth_token = melodica_json_object_get(doc, result, "userAuthToken");
        char *listener = melodica_json_string_dup(doc, listener_token);
        char *auth = melodica_json_string_dup(doc, auth_token);
        if ((listener_token != MELODICA_JSON_NONE && listener == NULL) ||
                (auth_token != MELODICA_JSON_NONE && auth == NULL)) {
            free(listener); free(auth);
            return PIANO_RET_INVALID_RESPONSE;
        }
        free(ph->user.listenerId);
        free(ph->user.authToken);
        ph->user.listenerId = listener;
        ph->user.authToken = auth;
        return PIANO_RET_OK;
    }
    /* Upstream treats an unrecognized login step as an unused response. */
    return PIANO_RET_OK;

}

static int piano_json_bool_default(const MelodicaJsonDoc *doc,
        const size_t object, const char *key, const int fallback) {
    const size_t token = melodica_json_object_get(doc, object, key);
    int value = fallback;
    if (token != MELODICA_JSON_NONE) {
        int parsed = 0;
        if (melodica_json_bool(doc, token, &parsed)) value = parsed;
    }
    return value;
}

static PianoReturn_t piano_response_stations(PianoHandle_t *ph,
        const MelodicaJsonDoc *doc, const size_t result) {
    if (result == MELODICA_JSON_NONE || result >= doc->count ||
            doc->tokens[result].type != MELODICA_JSON_OBJECT) return PIANO_RET_OK;
    const size_t stations = melodica_json_object_get(doc, result, "stations");
    if (stations == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (stations >= doc->count || doc->tokens[stations].type != MELODICA_JSON_ARRAY) {
        return PIANO_RET_INVALID_RESPONSE;
    }

    size_t quickmix_ids = MELODICA_JSON_NONE;
    const size_t count = melodica_json_array_count(doc, stations);
    for (size_t i = 0u; i < count; ++i) {
        const size_t station_token = melodica_json_array_get(doc, stations, i);
        if (station_token == MELODICA_JSON_NONE || station_token >= doc->count ||
                melodica_json_is_null(doc, station_token)) {
            return PIANO_RET_INVALID_RESPONSE;
        }

        PianoStation_t *station = (PianoStation_t *)calloc(1u, sizeof(*station));
        if (station == NULL) return PIANO_RET_OUT_OF_MEMORY;
        station->name = melodica_json_string_dup(doc,
                melodica_json_object_get(doc, station_token, "stationName"));
        station->id = melodica_json_string_dup(doc,
                melodica_json_object_get(doc, station_token, "stationToken"));
        station->isCreator = (char)!piano_json_bool_default(
                doc, station_token, "isShared", 1);
        station->isQuickMix = (char)piano_json_bool_default(
                doc, station_token, "isQuickMix", 0);
        if (station->isQuickMix) {
            quickmix_ids = melodica_json_object_get(doc, station_token, "quickMixStationIds");
        }
        ph->stations = (PianoStation_t *)PianoListAppend(
                ph->stations == NULL ? NULL : &ph->stations->head, &station->head);
    }

    if (quickmix_ids != MELODICA_JSON_NONE && quickmix_ids < doc->count &&
            doc->tokens[quickmix_ids].type == MELODICA_JSON_ARRAY) {
        const size_t mix_count = melodica_json_array_count(doc, quickmix_ids);
        for (PianoStation_t *station = ph->stations; station != NULL;
                station = (PianoStation_t *)station->head.next) {
            if (station->id == NULL) continue;
            for (size_t i = 0u; i < mix_count; ++i) {
                const size_t id = melodica_json_array_get(doc, quickmix_ids, i);
                char *text = melodica_json_string_dup(doc, id);
                const int equal = text != NULL && strcmp(text, station->id) == 0;
                free(text);
                if (equal) { station->useQuickMix = 1; break; }
            }
        }
    }
    return PIANO_RET_OK;
}

PianoReturn_t PianoResponse(PianoHandle_t *ph, PianoRequest_t *req) {
    if (ph == NULL || req == NULL || req->responseData == NULL) {
        return PIANO_RET_INVALID_RESPONSE;
    }

    MelodicaJsonDoc doc;
    if (!melodica_json_parse(&doc, req->responseData)) return PIANO_RET_INVALID_RESPONSE;
    PianoReturn_t ret = PIANO_RET_INVALID_RESPONSE;

    const size_t root = 0u;
    const size_t stat = melodica_json_object_get(&doc, root, "stat");
    if (stat == MELODICA_JSON_NONE || stat >= doc.count ||
            melodica_json_is_null(&doc, stat)) {
        goto cleanup;
    }
    if (!melodica_json_string_is(&doc, stat, "ok")) {
        ret = piano_response_error(&doc, root, req);
        goto cleanup;
    }

    const size_t result = melodica_json_object_get(&doc, root, "result");
    switch (req->type) {
        case PIANO_REQUEST_LOGIN:
            ret = piano_response_login(ph, req, &doc, result);
            break;
        case PIANO_REQUEST_GET_STATIONS:
            ret = piano_response_stations(ph, &doc, result);
            break;
        case PIANO_REQUEST_GET_PLAYLIST:
            ret = piano_response_playlist(req, &doc, result);
            break;
        case PIANO_REQUEST_SEARCH:
            ret = piano_response_search(req, &doc, result);
            break;
        default:
            ret = PIANO_RET_ERR;
            break;
    }

cleanup:
    melodica_json_destroy(&doc);
    return ret;
}

PianoStation_t *PianoFindStationById(PianoStation_t *stations, const char *searchStation) {
    assert(stations != NULL);
    if (searchStation == NULL) return NULL;
    for (PianoStation_t *cur = stations; cur != NULL; cur = (PianoStation_t *)cur->head.next) {
        if (strcmp(cur->id, searchStation) == 0) return cur;
    }
    return NULL;
}

const char *PianoErrorToStr(const PianoReturn_t ret) {
    switch (ret) {
        case PIANO_RET_OK: return "Everything is fine :)";
        case PIANO_RET_ERR: return "Unknown.";
        case PIANO_RET_INVALID_RESPONSE: return "Invalid response.";
        case PIANO_RET_CONTINUE_REQUEST: return "Fix your program.";
        case PIANO_RET_OUT_OF_MEMORY: return "Out of memory.";
        case PIANO_RET_INVALID_LOGIN: return "Wrong email address or password.";
        case PIANO_RET_QUALITY_UNAVAILABLE: return "Selected audio quality is not available.";
        case PIANO_RET_GCRY_ERR: return "libgcrypt initialization failed.";
        case PIANO_RET_P_INTERNAL: return "Internal error.";
        case PIANO_RET_P_CALL_NOT_ALLOWED: return "Call not allowed.";
        case PIANO_RET_P_INVALID_AUTH_TOKEN: return "Invalid auth token.";
        case PIANO_RET_P_MAINTENANCE_MODE: return "Maintenance mode.";
        case PIANO_RET_P_MAX_STATIONS_REACHED: return "Max number of stations reached.";
        case PIANO_RET_P_READ_ONLY_MODE: return "Read only mode. Try again later.";
        case PIANO_RET_P_STATION_DOES_NOT_EXIST: return "Station does not exist.";
        case PIANO_RET_P_INVALID_PARTNER_LOGIN: return "Invalid partner login.";
        case PIANO_RET_P_LICENSING_RESTRICTIONS:
            return "Pandora is not available in your country. Set up a control proxy (see manpage).";
        case PIANO_RET_P_PARTNER_NOT_AUTHORIZED: return "Invalid partner credentials.";
        case PIANO_RET_P_LISTENER_NOT_AUTHORIZED: return "Listener not authorized.";
        case PIANO_RET_P_RATE_LIMIT: return "Access denied. Try again later.";
        default: return "No error message available.";
    }
}
