#include "melodica_bridge.h"
#include "piano.h"
#include "crypt.h"
#include "json.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static unsigned checks;
#define CHECK(condition) do { ++checks; if (!(condition)) { \
    fprintf(stderr, "native bridge check failed at line %d: %s\n", __LINE__, #condition); \
    exit(1); } } while (0)

static const char incoming_key[] = "incoming-key";
static const char outgoing_key[] = "outgoing-key";
static const char ok[] = "{\"stat\":\"ok\",\"result\":{}}";

static xp_session *session(void) {
    xp_session *s = xp_session_create("tuner.pandora.com", "partner", "partner-password",
        "device", incoming_key, outgoing_key);
    CHECK(s != NULL);
    return s;
}

static char *request_json(xp_session *s) {
    MelodicaBlowfish outgoing;
    CHECK(melodica_blowfish_init(&outgoing, outgoing_key, strlen(outgoing_key)));
    size_t size = 0;
    char *plain = melodica_piano_decrypt_string(&outgoing, xp_request_body(s), &size);
    melodica_blowfish_clear(&outgoing);
    CHECK(plain != NULL);
    CHECK(size > 0);
    return plain;
}

static void string_field(const char *json, const char *key, const char *expected) {
    MelodicaJsonDoc doc;
    CHECK(melodica_json_parse(&doc, json));
    CHECK(melodica_json_string_is(&doc, melodica_json_object_get(&doc, 0u, key), expected));
    melodica_json_destroy(&doc);
}

static void partner_login(xp_session *s, const char *timestamp, const int expected) {
    CHECK(xp_prepare(s, "auth.partnerLogin", "{\"includeUrls\":true}") == PIANO_RET_OK);
    CHECK(strcmp(xp_request_url(s), "https://tuner.pandora.com/services/json/?method=auth.partnerLogin") == 0);
    string_field(xp_request_body(s), "username", "partner");
    string_field(xp_request_body(s), "deviceModel", "device");
    MelodicaBlowfish incoming;
    CHECK(melodica_blowfish_init(&incoming, incoming_key, strlen(incoming_key)));
    char *cipher = melodica_piano_encrypt_string(&incoming, timestamp);
    melodica_blowfish_clear(&incoming);
    CHECK(cipher != NULL);
    char response[512];
    CHECK(snprintf(response, sizeof(response),
        "{\"stat\":\"ok\",\"result\":{\"partnerId\":\"42\",\"partnerAuthToken\":\"partner+token/=\",\"syncTime\":\"%s\"}}", cipher) > 0);
    free(cipher);
    CHECK(xp_accept(s, response) == expected);
    CHECK(xp_request_url(s) == NULL);
    CHECK(xp_request_body(s) == NULL);
}

static void login(xp_session *s) {
    partner_login(s, "abcd1790223231", PIANO_RET_CONTINUE_REQUEST);
    CHECK(xp_server_time(s) == INT64_C(1790223231));
    CHECK(xp_prepare(s, "auth.userLogin",
        "{\"username\":\"listener\",\"password\":\"user-password\",\"returnIsSubscriber\":true,\"syncTime\":1790223231}") == PIANO_RET_OK);
    CHECK(strstr(xp_request_url(s), "auth_token=partner%2Btoken%2F%3D") != NULL);
    CHECK(strstr(xp_request_url(s), "partner_id=42") != NULL);
    char *plain = request_json(s);
    string_field(plain, "username", "listener");
    string_field(plain, "partnerAuthToken", "partner+token/=");
    CHECK(strstr(plain, "\"returnIsSubscriber\":true") != NULL);
    CHECK(strstr(plain, "\"syncTime\":1790223231") != NULL);
    free(plain);
    CHECK(xp_accept(s,
        "{\"stat\":\"ok\",\"result\":{\"userId\":\"user-id\",\"userAuthToken\":\"user+token\",\"isSubscriber\":true,\"hasAudioAds\":false}}") == PIANO_RET_OK);
}

typedef struct rpc_case { const char *method, *body, *response; } rpc_case;
static const rpc_case methods[] = {
    {"user.getStationList", "{\"includeStationArtUrl\":false}",
     "{\"stat\":\"ok\",\"result\":{\"stations\":[{\"stationToken\":\"token\",\"stationId\":\"id\",\"stationName\":\"Station\"}]}}"},
    {"station.getPlaylist", "{\"stationToken\":\"token\",\"includeTrackLength\":true,\"additionalAudioUrl\":\"HTTP_128_MP3,HTTP_192_MP3\"}",
     "{\"stat\":\"ok\",\"result\":{\"items\":[{\"artistName\":\"Artist\",\"songName\":\"Song\",\"trackToken\":\"track\",\"stationId\":\"id\",\"audioUrlMap\":{\"highQuality\":{\"encoding\":\"mp3\",\"audioUrl\":\"https://audio/song.mp3\"}}}]}}"},
    {"station.addFeedback", "{\"stationToken\":\"token\",\"trackToken\":\"track\",\"isPositive\":true}", NULL},
    {"station.renameStation", "{\"stationToken\":\"token\",\"stationName\":\"New \\\"name\\\"\\n\"}", NULL},
    {"station.deleteStation", "{\"stationToken\":\"token\"}", NULL},
    {"music.search", "{\"searchText\":\"Uematsu\",\"includeGenreStations\":true}",
     "{\"stat\":\"ok\",\"result\":{\"artists\":[{\"artistName\":\"Artist\",\"musicToken\":\"artist-token\"}],\"songs\":[{\"songName\":\"Song\",\"artistName\":\"Artist\",\"musicToken\":\"song-token\"}],\"genreStations\":[{\"stationName\":\"Genre\",\"musicToken\":\"genre-token\"}]}}"},
    {"station.createStation", "{\"musicToken\":\"genre-token\"}",
     "{\"stat\":\"ok\",\"result\":{\"stationToken\":\"created-token\",\"stationName\":\"Created\"}}"},
    {"station.createStation", "{\"trackToken\":\"track\",\"musicType\":\"artist\"}",
     "{\"stat\":\"ok\",\"result\":{\"stationToken\":\"from-track\",\"stationName\":\"From track\"}}"},
    {"station.addMusic", "{\"stationToken\":\"token\",\"musicToken\":\"music\"}", NULL},
    {"station.deleteMusic", "{\"seedId\":\"seed\"}", NULL},
    {"station.deleteFeedback", "{\"feedbackId\":\"feedback\"}", NULL},
    {"user.sleepSong", "{\"trackToken\":\"track\"}", NULL},
    {"bookmark.addSongBookmark", "{\"trackToken\":\"track\"}", NULL},
    {"bookmark.addArtistBookmark", "{\"trackToken\":\"track\"}", NULL},
    {"track.explainTrack", "{\"trackToken\":\"track\"}",
     "{\"stat\":\"ok\",\"result\":{\"explanations\":[{\"focusTraitName\":\"piano\"},{\"focusTraitName\":\"strings\"}]}}"},
    {"station.getStation", "{\"stationToken\":\"token\",\"includeExtendedAttributes\":true}",
     "{\"stat\":\"ok\",\"result\":{\"music\":{\"songs\":[{\"songName\":\"Seed\",\"artistName\":\"Artist\",\"seedId\":\"seed\",\"musicToken\":\"music\"}],\"artists\":[]},\"feedback\":{\"thumbsUp\":[],\"thumbsDown\":[]}}}"},
    {"station.getGenreStations", "{}",
     "{\"stat\":\"ok\",\"result\":{\"categories\":[{\"categoryName\":\"Music\",\"stations\":[{\"stationName\":\"Genre\",\"stationToken\":\"genre\"}]}]}}"},
    {"user.setQuickMix", "{\"quickMixStationIds\":[\"id-one\",\"id-two\"]}", NULL},
    {"station.transformSharedStation", "{\"stationToken\":\"token\"}", NULL},
    {"user.getSettings", "{}", "{\"stat\":\"ok\",\"result\":{\"username\":\"listener\",\"isExplicitContentFilterEnabled\":true}}"},
    {"user.changeSettings", "{\"currentUsername\":\"listener\",\"currentPassword\":\"user-password\",\"isExplicitContentFilterEnabled\":false}", NULL},
    {"interactiveradio.v1.getAvailableModesSimple", "{\"stationId\":\"actual-id\"}",
     "{\"stat\":\"ok\",\"result\":{\"currentModeId\":2,\"availableModes\":[{\"modeId\":2,\"modeName\":\"Discovery\",\"modeDescription\":\"New songs\"}]}}"},
    {"interactiveradio.v1.setAndGetAvailableModes", "{\"stationId\":\"actual-id\",\"modeId\":2}",
     "{\"stat\":\"ok\",\"result\":{\"currentModeId\":2,\"availableModes\":[]}}"},
};

static void all_methods(xp_session *s) {
    for (size_t round = 0; round < 3u; ++round) {
        for (size_t i = 0; i < sizeof(methods) / sizeof(*methods); ++i) {
            const rpc_case *item = &methods[i];
            CHECK(xp_prepare(s, item->method, item->body) == PIANO_RET_OK);
            CHECK(strncmp(xp_request_url(s), "https://", 8u) == 0);
            CHECK(strstr(xp_request_url(s), item->method) != NULL);
            CHECK(strstr(xp_request_url(s), "user_id=user-id") != NULL);
            char *plain = request_json(s);
            string_field(plain, "userAuthToken", "user+token");
            MelodicaJsonDoc native, extra;
            CHECK(melodica_json_parse(&native, plain));
            CHECK(melodica_json_parse(&extra, item->body));
            /* Every application option survives the actual native request. */
            size_t key = MELODICA_JSON_NONE;
            for (size_t token = 1; token < extra.count; ++token) {
                if (extra.tokens[token].parent != 0) continue;
                if (key == MELODICA_JSON_NONE) { key = token; continue; }
                char *name = melodica_json_string_dup(&extra, key);
                CHECK(name != NULL);
                const size_t got = melodica_json_object_get(&native, 0, name);
                CHECK(got != MELODICA_JSON_NONE);
                char *a = melodica_json_string_dup(&native, got);
                char *b = melodica_json_string_dup(&extra, token);
                CHECK(a != NULL && b != NULL && strcmp(a, b) == 0);
                free(name); free(a); free(b); key = MELODICA_JSON_NONE;
            }
            melodica_json_destroy(&native); melodica_json_destroy(&extra); free(plain);
            CHECK(xp_accept(s, item->response == NULL ? ok : item->response) == PIANO_RET_OK);
            CHECK(xp_request_url(s) == NULL && xp_request_body(s) == NULL);
        }
    }
}

static void lifecycle_and_errors(xp_session *s) {
    CHECK(xp_accept(s, ok) == XP_ERROR_STATE);
    CHECK(xp_prepare(s, "unknown.method", "{}") == XP_ERROR_METHOD);
    CHECK(xp_prepare(s, "user.getStationList", "[]") == XP_ERROR_ARGUMENT);
    CHECK(xp_prepare(s, "station.renameStation", "{\"stationToken\":\"x\"}") == XP_ERROR_ARGUMENT);
    CHECK(xp_prepare(s, "music.search", "{\"searchText\":\"a\\u0000b\"}") == XP_ERROR_ARGUMENT);
    CHECK(xp_prepare(s, "user.getStationList", "{\"userAuthToken\":\"wrong\"}") == XP_ERROR_AUTH);
    CHECK(xp_prepare(s, "user.getStationList", "{}") == PIANO_RET_OK);
    const char *url = xp_request_url(s), *body = xp_request_body(s);
    CHECK(xp_prepare(s, "user.getSettings", "{}") == XP_ERROR_STATE);
    CHECK(xp_request_url(s) == url && xp_request_body(s) == body);
    xp_abort(s);
    xp_abort(s);
    CHECK(xp_request_url(s) == NULL && xp_request_body(s) == NULL);
    CHECK(xp_prepare(s, "user.getStationList", "{}") == PIANO_RET_OK);
    CHECK(xp_accept(s, "not JSON") == PIANO_RET_INVALID_RESPONSE);
    CHECK(xp_request_url(s) == NULL);
    CHECK(xp_prepare(s, "user.getStationList", "{}") == PIANO_RET_OK);
    CHECK(xp_accept(s, "{\"stat\":\"fail\",\"code\":1001,\"message\":\"INVALID_AUTH_TOKEN\"}") == PIANO_RET_P_INVALID_AUTH_TOKEN);
    CHECK(strstr(xp_error(s), "user-password") == NULL);
    CHECK(xp_prepare(s, "station.getPlaylist", "{\"stationToken\":\"token\"}") == PIANO_RET_OK);
    CHECK(xp_accept(s, "{\"stat\":\"ok\",\"result\":{\"items\":[{\"artistName\":\"Artist\",\"audioUrlMap\":{\"lowQuality\":{\"encoding\":\"mp3\",\"audioUrl\":\"https://audio/low.mp3\"}}}]}}") == PIANO_RET_QUALITY_UNAVAILABLE);
    /* Managed code explicitly projects its chosen stream before acceptance.
     * A projected stream is still parsed and validated by PianoResponse. */
    CHECK(xp_prepare(s, "station.getPlaylist", "{\"stationToken\":\"token\"}") == PIANO_RET_OK);
    CHECK(xp_accept(s, methods[1].response) == PIANO_RET_OK);
    CHECK(xp_prepare(s, "interactiveradio.v1.setAndGetAvailableModes", "{\"stationId\":\"id\",\"modeId\":2}") == PIANO_RET_OK);
    CHECK(xp_accept(s, ok) == PIANO_RET_ERR); /* No matching currentModeId. */
    CHECK(xp_prepare(s, "music.search", "{\"searchText\":\"abort me\"}") == PIANO_RET_OK);
    /* Destruction must also safely abort a pending operation. */
}

int main(void) {
    CHECK(xp_bridge_abi_version() == XP_BRIDGE_ABI_VERSION);
    CHECK(xp_session_create("https://bad/host", "a", "b", "c", "d", "e") == NULL);
    xp_session_free(NULL); xp_abort(NULL);
    CHECK(xp_prepare(NULL, "x", "{}") == XP_ERROR_ARGUMENT);
    xp_session *s = session();
    CHECK(xp_server_time(s) == 0);
    CHECK(xp_prepare(s, "user.getStationList", "{}") == PIANO_RET_ERR);
    CHECK(xp_request_body(s) == NULL);
    CHECK(xp_prepare(s, "auth.userLogin", "{\"username\":\"listener\",\"password\":\"user-password\"}") == XP_ERROR_STATE);
    CHECK(xp_request_body(s) == NULL);
    login(s); all_methods(s); lifecycle_and_errors(s); xp_session_free(s);
    s = session();
    partner_login(s, "abcdnot-a-number", PIANO_RET_INVALID_RESPONSE);
    CHECK(xp_server_time(s) == 0);
    partner_login(s, "abcd1790223231", PIANO_RET_CONTINUE_REQUEST);
    CHECK(xp_server_time(s) == INT64_C(1790223231));
    xp_session_free(s);
    printf("native bridge: %u checks, login, all RPCs, options, validation, abort and ownership: ok\n", checks);
    return 0;
}
