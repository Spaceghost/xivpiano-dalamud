#include "melodica_bridge.h"
#include "piano.h"
#include "json.h"
#include "crypt.h"

#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef union xp_request_data {
    PianoRequestDataLogin_t login;
    PianoRequestDataGetPlaylist_t playlist;
    PianoRequestDataRateSong_t rate;
    PianoRequestDataRenameStation_t rename;
    PianoRequestDataSearch_t search;
    PianoRequestDataCreateStation_t create;
    PianoRequestDataAddSeed_t seed;
    PianoRequestDataDeleteSeed_t delete_seed;
    PianoRequestDataExplain_t explain;
    PianoRequestDataGetStationInfo_t info;
    PianoRequestDataChangeSettings_t change;
    PianoRequestDataGetStationModes_t modes;
    PianoRequestDataSetStationMode_t mode;
    PianoSettings_t settings;
} xp_request_data;

struct xp_session {
    PianoHandle_t piano;
    PianoRequest_t request;
    xp_request_data data;
    PianoSong_t song;
    PianoStation_t mode_station;
    char *host;
    char *url;
    char *strings[16];
    size_t string_count;
    int pending;
    int input_error;
    int64_t server_time;
    const char *error;
};

static char *xp_dup(const char *text) {
    if (text == NULL) return NULL;
    const size_t length = strlen(text);
    char *copy = (char *)malloc(length + 1u);
    if (copy != NULL) memcpy(copy, text, length + 1u);
    return copy;
}

static int xp_fail(xp_session *s, const int code, const char *message) {
    if (s != NULL) s->error = message;
    return code;
}

static int xp_host_valid(const char *host) {
    if (host == NULL || *host == '\0') return 0;
    for (const unsigned char *p = (const unsigned char *)host; *p != 0; ++p) {
        if (!((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') ||
                (*p >= '0' && *p <= '9') || *p == '-' || *p == '.')) return 0;
    }
    return 1;
}

static char *xp_field(xp_session *s, const MelodicaJsonDoc *doc,
        const char *key, const int required) {
    const size_t token = melodica_json_object_get(doc, 0u, key);
    if (token == MELODICA_JSON_NONE) {
        if (required) s->input_error = XP_ERROR_ARGUMENT;
        return NULL;
    }
    if (doc->tokens[token].type != MELODICA_JSON_STRING ||
            memchr(doc->text + doc->tokens[token].start, '\0',
                doc->tokens[token].end - doc->tokens[token].start) != NULL) {
        s->input_error = XP_ERROR_ARGUMENT;
        return NULL;
    }
    char *text = melodica_json_string_dup(doc, token);
    if (text == NULL || s->string_count == sizeof(s->strings) / sizeof(*s->strings)) {
        free(text);
        s->input_error = XP_ERROR_MEMORY;
        return NULL;
    }
    /* C API strings cannot represent embedded NULs. Reject their escaped form
     * instead of silently selecting another identity before the merge. */
    const char *raw = doc->text + doc->tokens[token].start;
    for (size_t i = 0; i + 5u < doc->tokens[token].end - doc->tokens[token].start; ++i) {
        if (raw[i] == '\\') {
            if (memcmp(raw + i, "\\u0000", 6u) == 0) s->input_error = XP_ERROR_ARGUMENT;
            ++i;
        }
    }
    s->strings[s->string_count++] = text;
    return text;
}

static PianoStation_t *xp_station(xp_session *s, const char *token) {
    if (token == NULL) return NULL;
    PianoStation_t *station = s->piano.stations == NULL ? NULL :
        PianoFindStationById(s->piano.stations, token);
    if (station != NULL) return station;
    station = (PianoStation_t *)calloc(1u, sizeof(*station));
    if (station == NULL) { s->input_error = XP_ERROR_MEMORY; return NULL; }
    station->id = xp_dup(token);
    if (station->id == NULL) {
        free(station); s->input_error = XP_ERROR_MEMORY; return NULL;
    }
    s->piano.stations = (PianoStation_t *)PianoListAppend(
        s->piano.stations == NULL ? NULL : &s->piano.stations->head, &station->head);
    return station;
}

static PianoRequestType_t xp_map_request(xp_session *s, const char *method,
        const MelodicaJsonDoc *doc) {
    PianoStation_t *station = NULL;
    const int modes = strcmp(method, "interactiveradio.v1.getAvailableModesSimple") == 0 ||
        strcmp(method, "interactiveradio.v1.setAndGetAvailableModes") == 0;
    if (modes) {
        s->mode_station.id = xp_field(s, doc, "stationId", 1);
        station = &s->mode_station;
    } else if (melodica_json_object_get(doc, 0u, "stationToken") != MELODICA_JSON_NONE) {
        station = xp_station(s, xp_field(s, doc, "stationToken", 1));
    }

    if (strcmp(method, "auth.partnerLogin") == 0) {
        s->data.login.step = 0u;
        s->request.data = &s->data.login;
        return PIANO_REQUEST_LOGIN;
    }
    if (strcmp(method, "auth.userLogin") == 0) {
        s->data.login.step = 1u;
        s->data.login.user = xp_field(s, doc, "username", 1);
        s->data.login.password = xp_field(s, doc, "password", 1);
        s->request.data = &s->data.login;
        return PIANO_REQUEST_LOGIN;
    }
    if (strcmp(method, "user.getStationList") == 0) return PIANO_REQUEST_GET_STATIONS;
    if (strcmp(method, "station.getPlaylist") == 0) {
        s->data.playlist.station = station;
        s->data.playlist.quality = PIANO_AQ_HIGH;
        s->request.data = &s->data.playlist;
        return PIANO_REQUEST_GET_PLAYLIST;
    }
    if (strcmp(method, "station.addFeedback") == 0) {
        int positive = 0;
        const size_t value = melodica_json_object_get(doc, 0u, "isPositive");
        if (value == MELODICA_JSON_NONE || !melodica_json_bool(doc, value, &positive))
            s->input_error = XP_ERROR_ARGUMENT;
        s->song.stationId = station == NULL ? NULL : station->id;
        s->song.trackToken = xp_field(s, doc, "trackToken", 1);
        s->data.rate.song = &s->song;
        s->data.rate.rating = positive ? PIANO_RATE_LOVE : PIANO_RATE_BAN;
        s->request.data = &s->data.rate;
        return PIANO_REQUEST_RATE_SONG;
    }
    if (strcmp(method, "station.renameStation") == 0) {
        s->data.rename.station = station;
        s->data.rename.newName = xp_field(s, doc, "stationName", 1);
        s->request.data = &s->data.rename;
        return PIANO_REQUEST_RENAME_STATION;
    }
    if (strcmp(method, "station.deleteStation") == 0 ||
            strcmp(method, "station.transformSharedStation") == 0) {
        s->request.data = station;
        return strcmp(method, "station.deleteStation") == 0 ?
            PIANO_REQUEST_DELETE_STATION : PIANO_REQUEST_TRANSFORM_STATION;
    }
    if (strcmp(method, "music.search") == 0) {
        s->data.search.searchStr = xp_field(s, doc, "searchText", 1);
        s->request.data = &s->data.search;
        return PIANO_REQUEST_SEARCH;
    }
    if (strcmp(method, "station.createStation") == 0) {
        s->data.create.token = xp_field(s, doc, "musicToken", 0);
        if (s->data.create.token == NULL) {
            s->data.create.token = xp_field(s, doc, "trackToken", 1);
            char *kind = xp_field(s, doc, "musicType", 1);
            if (kind != NULL && strcmp(kind, "song") == 0) s->data.create.type = PIANO_MUSICTYPE_SONG;
            else if (kind != NULL && strcmp(kind, "artist") == 0) s->data.create.type = PIANO_MUSICTYPE_ARTIST;
            else s->input_error = XP_ERROR_ARGUMENT;
        }
        s->request.data = &s->data.create;
        return PIANO_REQUEST_CREATE_STATION;
    }
    if (strcmp(method, "station.addMusic") == 0) {
        s->data.seed.station = station;
        s->data.seed.musicId = xp_field(s, doc, "musicToken", 1);
        s->request.data = &s->data.seed;
        return PIANO_REQUEST_ADD_SEED;
    }
    if (strcmp(method, "station.deleteMusic") == 0) {
        s->song.seedId = xp_field(s, doc, "seedId", 1);
        s->data.delete_seed.song = &s->song;
        s->request.data = &s->data.delete_seed;
        return PIANO_REQUEST_DELETE_SEED;
    }
    if (strcmp(method, "station.deleteFeedback") == 0) {
        s->song.feedbackId = xp_field(s, doc, "feedbackId", 1);
        s->request.data = &s->song;
        return PIANO_REQUEST_DELETE_FEEDBACK;
    }
    if (strcmp(method, "user.sleepSong") == 0 ||
            strcmp(method, "bookmark.addSongBookmark") == 0 ||
            strcmp(method, "bookmark.addArtistBookmark") == 0 ||
            strcmp(method, "track.explainTrack") == 0) {
        s->song.trackToken = xp_field(s, doc, "trackToken", 1);
        s->request.data = &s->song;
        if (strcmp(method, "user.sleepSong") == 0) return PIANO_REQUEST_ADD_TIRED_SONG;
        if (strcmp(method, "bookmark.addSongBookmark") == 0) return PIANO_REQUEST_BOOKMARK_SONG;
        if (strcmp(method, "bookmark.addArtistBookmark") == 0) return PIANO_REQUEST_BOOKMARK_ARTIST;
        s->data.explain.song = &s->song;
        s->request.data = &s->data.explain;
        return PIANO_REQUEST_EXPLAIN;
    }
    if (strcmp(method, "station.getStation") == 0) {
        s->data.info.station = station;
        s->request.data = &s->data.info;
        return PIANO_REQUEST_GET_STATION_INFO;
    }
    if (strcmp(method, "station.getGenreStations") == 0) return PIANO_REQUEST_GET_GENRE_STATIONS;
    if (strcmp(method, "user.setQuickMix") == 0) {
        for (PianoStation_t *p = s->piano.stations; p != NULL; p = (PianoStation_t *)p->head.next)
            p->useQuickMix = 0;
        const size_t array = melodica_json_object_get(doc, 0u, "quickMixStationIds");
        if (array == MELODICA_JSON_NONE || doc->tokens[array].type != MELODICA_JSON_ARRAY)
            s->input_error = XP_ERROR_ARGUMENT;
        for (size_t i = 0; i < melodica_json_array_count(doc, array); ++i) {
            const size_t item = melodica_json_array_get(doc, array, i);
            char *id = melodica_json_string_dup(doc, item);
            if (id == NULL) s->input_error = XP_ERROR_MEMORY;
            PianoStation_t *p = xp_station(s, id);
            if (p != NULL) p->useQuickMix = 1;
            free(id);
        }
        return PIANO_REQUEST_SET_QUICKMIX;
    }
    if (strcmp(method, "user.getSettings") == 0) {
        s->request.data = &s->data.settings;
        return PIANO_REQUEST_GET_SETTINGS;
    }
    if (strcmp(method, "user.changeSettings") == 0) {
        s->data.change.currentUsername = xp_field(s, doc, "currentUsername", 1);
        s->data.change.currentPassword = xp_field(s, doc, "currentPassword", 1);
        s->data.change.newUsername = xp_field(s, doc, "newUsername", 0);
        s->data.change.newPassword = xp_field(s, doc, "newPassword", 0);
        const size_t filter = melodica_json_object_get(doc, 0u, "isExplicitContentFilterEnabled");
        if (filter != MELODICA_JSON_NONE) {
            int value = 0;
            if (!melodica_json_bool(doc, filter, &value)) s->input_error = XP_ERROR_ARGUMENT;
            s->data.change.explicitContentFilter = value ? PIANO_TRUE : PIANO_FALSE;
        }
        s->request.data = &s->data.change;
        return PIANO_REQUEST_CHANGE_SETTINGS;
    }
    if (modes) {
        if (strcmp(method, "interactiveradio.v1.getAvailableModesSimple") == 0) {
            s->data.modes.station = station;
            s->request.data = &s->data.modes;
            return PIANO_REQUEST_GET_STATION_MODES;
        }
        long id = 0;
        const size_t token = melodica_json_object_get(doc, 0u, "modeId");
        if (token == MELODICA_JSON_NONE || !melodica_json_long(doc, token, &id))
            s->input_error = XP_ERROR_ARGUMENT;
        s->data.mode.station = station;
        s->data.mode.id = (unsigned int)id;
        s->request.data = &s->data.mode;
        return PIANO_REQUEST_SET_STATION_MODE;
    }
    return (PianoRequestType_t)0;
}

static void xp_cleanup(xp_session *s) {
    if (s == NULL) return;
    switch (s->request.type) {
        case PIANO_REQUEST_GET_PLAYLIST: PianoDestroyPlaylist(s->data.playlist.retPlaylist); break;
        case PIANO_REQUEST_SEARCH: PianoDestroySearchResult(&s->data.search.searchResult); break;
        case PIANO_REQUEST_GET_STATION_INFO: PianoDestroyStationInfo(&s->data.info.info); break;
        case PIANO_REQUEST_EXPLAIN: free(s->data.explain.retExplain); break;
        case PIANO_REQUEST_GET_SETTINGS: free(s->data.settings.username); break;
        case PIANO_REQUEST_GET_STATION_MODES: PianoDestroyStationMode(s->data.modes.retModes); break;
        default: break;
    }
    PianoDestroyRequest(&s->request);
    /* Managed models own the application's catalog. Native lists are the
     * current transaction's validation view, and must not accumulate when a
     * user repeatedly refreshes stations, genres or creates stations. */
    PianoHandle_t view;
    memset(&view, 0, sizeof(view));
    view.stations = s->piano.stations;
    view.genreStations = s->piano.genreStations;
    s->piano.stations = NULL;
    s->piano.genreStations = NULL;
    PianoDestroy(&view);
    for (size_t i = 0; i < s->string_count; ++i) free(s->strings[i]);
    s->string_count = 0u;
    memset(&s->data, 0, sizeof(s->data));
    memset(&s->song, 0, sizeof(s->song));
    memset(&s->mode_station, 0, sizeof(s->mode_station));
    free(s->url); s->url = NULL;
    s->pending = 0;
    s->input_error = 0;
}

static int xp_auth_matches(const MelodicaJsonDoc *doc, const char *key, const char *expected) {
    const size_t token = melodica_json_object_get(doc, 0u, key);
    if (token == MELODICA_JSON_NONE) return 1;
    return expected != NULL && melodica_json_string_is(doc, token, expected);
}

static void xp_raw_span(const MelodicaJsonDoc *doc, const size_t token,
        const char **text, size_t *length) {
    const MelodicaJsonToken *t = &doc->tokens[token];
    const int quote = t->type == MELODICA_JSON_STRING;
    *text = doc->text + t->start - (size_t)quote;
    *length = t->end - t->start + (quote ? 2u : 0u);
}

/* Emit top-level members, retaining exact raw nested option values. Generated
 * native members survive unless the caller explicitly supplies that key. */
static int xp_members(char **out, int *first, const MelodicaJsonDoc *source,
        const MelodicaJsonDoc *override) {
    size_t key = MELODICA_JSON_NONE;
    for (size_t i = 1u; i < source->count && source->tokens[i].start < source->tokens[0].end; ++i) {
        if (source->tokens[i].parent != 0u) continue;
        if (key == MELODICA_JSON_NONE) { key = i; continue; }
        char *name = melodica_json_string_dup(source, key);
        if (name == NULL) return 0;
        const int replaced = override != NULL &&
            melodica_json_object_get(override, 0u, name) != MELODICA_JSON_NONE;
        free(name);
        if (!replaced) {
            const char *text; size_t length;
            if (!*first) *(*out)++ = ',';
            *first = 0;
            xp_raw_span(source, key, &text, &length); memcpy(*out, text, length); *out += length;
            *(*out)++ = ':';
            xp_raw_span(source, i, &text, &length); memcpy(*out, text, length); *out += length;
        }
        key = MELODICA_JSON_NONE;
    }
    return key == MELODICA_JSON_NONE;
}

static int xp_merge_request(xp_session *s, const MelodicaJsonDoc *extra, const int encrypted) {
    size_t plain_length = 0u;
    char *plain = encrypted ? melodica_piano_decrypt_string(
        (const MelodicaBlowfish *)s->piano.partner.out, s->request.postData, &plain_length) :
        xp_dup(s->request.postData);
    (void)plain_length;
    if (plain == NULL) return xp_fail(s, XP_ERROR_MEMORY, "Cannot read native request body.");
    MelodicaJsonDoc generated;
    if (!melodica_json_parse(&generated, plain)) {
        free(plain); return xp_fail(s, XP_ERROR_STATE, "Native request was not a JSON object.");
    }
    if (generated.tokens[0].type != MELODICA_JSON_OBJECT) {
        melodica_json_destroy(&generated); free(plain);
        return xp_fail(s, XP_ERROR_STATE, "Native request was not a JSON object.");
    }
    const size_t a = strlen(plain), b = extra->length;
    if (a > SIZE_MAX - b - 4u) {
        melodica_json_destroy(&generated); free(plain);
        return xp_fail(s, XP_ERROR_MEMORY, "Request body is too large.");
    }
    char *merged = (char *)malloc(a + b + 4u);
    if (merged == NULL) {
        melodica_json_destroy(&generated); free(plain);
        return xp_fail(s, XP_ERROR_MEMORY, "Cannot allocate merged request.");
    }
    char *out = merged; *out++ = '{'; int first = 1;
    const int ok = xp_members(&out, &first, &generated, extra) && xp_members(&out, &first, extra, NULL);
    *out++ = '}'; *out = '\0';
    melodica_json_destroy(&generated); free(plain);
    if (!ok) { free(merged); return xp_fail(s, XP_ERROR_MEMORY, "Cannot merge request options."); }
    char *body = encrypted ? melodica_piano_encrypt_string(
        (const MelodicaBlowfish *)s->piano.partner.out, merged) : xp_dup(merged);
    free(merged);
    if (body == NULL) return xp_fail(s, XP_ERROR_MEMORY, "Cannot encrypt merged request.");
    free(s->request.postData); s->request.postData = body;
    return PIANO_RET_OK;
}

uint32_t XP_CALL xp_bridge_abi_version(void) { return XP_BRIDGE_ABI_VERSION; }

xp_session *XP_CALL xp_session_create(const char *host, const char *user,
        const char *password, const char *device, const char *in, const char *out) {
    if (!xp_host_valid(host) || user == NULL || password == NULL || device == NULL || in == NULL || out == NULL)
        return NULL;
    xp_session *s = (xp_session *)calloc(1u, sizeof(*s));
    if (s == NULL) return NULL;
    s->host = xp_dup(host); s->error = "";
    if (s->host == NULL || PianoInit(&s->piano, user, password, device, in, out) != PIANO_RET_OK) {
        xp_session_free(s); return NULL;
    }
    return s;
}

void XP_CALL xp_session_free(xp_session *s) {
    if (s == NULL) return;
    xp_cleanup(s); PianoDestroy(&s->piano); free(s->host); free(s);
}

int XP_CALL xp_prepare(xp_session *s, const char *method, const char *body_json) {
    if (s == NULL || method == NULL || body_json == NULL) return xp_fail(s, XP_ERROR_ARGUMENT, "Missing request argument.");
    if (s->pending) return xp_fail(s, XP_ERROR_STATE, "A request is already pending.");
    s->error = "";
    MelodicaJsonDoc doc;
    if (!melodica_json_parse(&doc, body_json)) return xp_fail(s, XP_ERROR_ARGUMENT, "Request options are not JSON.");
    if (doc.tokens[0].type != MELODICA_JSON_OBJECT) {
        melodica_json_destroy(&doc); return xp_fail(s, XP_ERROR_ARGUMENT, "Request options must be an object.");
    }
    const int partner_login = strcmp(method, "auth.partnerLogin") == 0;
    const int user_login = strcmp(method, "auth.userLogin") == 0;
    if (!xp_auth_matches(&doc, "userAuthToken", s->piano.user.authToken) ||
            !xp_auth_matches(&doc, "partnerAuthToken", s->piano.partner.authToken) ||
            (partner_login && (!xp_auth_matches(&doc, "username", s->piano.partner.user) ||
                !xp_auth_matches(&doc, "password", s->piano.partner.password) ||
                !xp_auth_matches(&doc, "deviceModel", s->piano.partner.device)))) {
        melodica_json_destroy(&doc); return xp_fail(s, XP_ERROR_AUTH, "Request identity does not match native session.");
    }
    const PianoRequestType_t type = xp_map_request(s, method, &doc);
    int ret = PIANO_RET_OK;
    if (type == 0) ret = xp_fail(s, XP_ERROR_METHOD, "Unsupported Pandora method.");
    else if (s->input_error != 0) ret = xp_fail(s, s->input_error, "Missing or invalid request field.");
    else if (user_login && s->piano.partner.authToken == NULL) ret = xp_fail(s, XP_ERROR_STATE, "Partner login is required.");
    else {
        ret = (int)PianoRequest(&s->piano, &s->request, type);
        if (ret != PIANO_RET_OK) s->error = PianoErrorToStr((PianoReturn_t)ret);
    }
    if (ret == PIANO_RET_OK) ret = xp_merge_request(s, &doc, !partner_login);
    melodica_json_destroy(&doc);
    if (ret == PIANO_RET_OK) {
        const size_t host_length = strlen(s->host), path_length = strlen(s->request.urlPath);
        if (host_length > SIZE_MAX - path_length - 9u) ret = xp_fail(s, XP_ERROR_MEMORY, "Request URL is too large.");
        else {
            s->url = (char *)malloc(host_length + path_length + 9u);
            if (s->url == NULL) ret = xp_fail(s, XP_ERROR_MEMORY, "Cannot allocate request URL.");
            else (void)snprintf(s->url, host_length + path_length + 9u, "https://%s%s", s->host, s->request.urlPath);
        }
    }
    if (ret != PIANO_RET_OK) xp_cleanup(s);
    else s->pending = 1;
    return ret;
}

const char *XP_CALL xp_request_url(const xp_session *s) { return s != NULL && s->pending ? s->url : NULL; }
const char *XP_CALL xp_request_body(const xp_session *s) { return s != NULL && s->pending ? s->request.postData : NULL; }

static int xp_login_timestamp(xp_session *s, const char *response, int64_t *timestamp) {
    MelodicaJsonDoc doc;
    if (!melodica_json_parse(&doc, response)) return 0;
    const size_t result = melodica_json_object_get(&doc, 0u, "result");
    const size_t value = melodica_json_object_get(&doc, result, "syncTime");
    char *hex = melodica_json_string_dup(&doc, value);
    melodica_json_destroy(&doc);
    if (hex == NULL) return 0;
    size_t length = 0u;
    char *plain = melodica_piano_decrypt_string((const MelodicaBlowfish *)s->piano.partner.in, hex, &length);
    free(hex);
    if (plain == NULL || length <= 4u || plain[4] < '0' || plain[4] > '9') { free(plain); return 0; }
    errno = 0; char *end = NULL;
    const long long number = strtoll(plain + 4, &end, 10);
    const int valid = errno != ERANGE && end != plain + 4 && number >= 0;
    free(plain);
    if (valid) *timestamp = (int64_t)number;
    return valid;
}

int XP_CALL xp_accept(xp_session *s, const char *response_json) {
    if (s == NULL || response_json == NULL) return xp_fail(s, XP_ERROR_ARGUMENT, "Missing response argument.");
    if (!s->pending) return xp_fail(s, XP_ERROR_STATE, "No request is pending.");
    const int partner = s->request.type == PIANO_REQUEST_LOGIN && s->data.login.step == 0u;
    s->request.responseData = (char *)response_json;
    int ret = (int)PianoResponse(&s->piano, &s->request);
    if (partner && (ret == PIANO_RET_OK || ret == PIANO_RET_CONTINUE_REQUEST)) {
        int64_t timestamp = 0;
        if (!xp_login_timestamp(s, response_json, &timestamp)) ret = PIANO_RET_INVALID_RESPONSE;
        else s->server_time = timestamp;
    }
    if (ret != PIANO_RET_OK && ret != PIANO_RET_CONTINUE_REQUEST) s->error = PianoErrorToStr((PianoReturn_t)ret);
    else s->error = "";
    xp_cleanup(s);
    return ret;
}

void XP_CALL xp_abort(xp_session *s) { xp_cleanup(s); }
int64_t XP_CALL xp_server_time(const xp_session *s) { return s == NULL ? 0 : s->server_time; }
const char *XP_CALL xp_error(const xp_session *s) { return s == NULL ? "Invalid native session." : s->error; }
