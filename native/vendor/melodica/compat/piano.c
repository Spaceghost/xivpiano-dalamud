#include "piano.h"

#if defined(__GNUC__) || defined(__clang__)
#define PIANO_INTERNAL __attribute__((visibility("hidden")))
#else
#define PIANO_INTERNAL
#endif
PIANO_INTERNAL PianoReturn_t melodica_piano_request_base(PianoHandle_t *,
        PianoRequest_t *, PianoRequestType_t);
PIANO_INTERNAL PianoReturn_t melodica_piano_response_base(PianoHandle_t *, PianoRequest_t *);
#undef PIANO_INTERNAL

#define PianoRequest melodica_piano_request_base
#define PianoResponse melodica_piano_response_base
#include "piano_base.inc"
#undef PianoRequest
#undef PianoResponse

#include "station_mutations.h"
#include "feedback.h"
#include "seeds.h"
#include "quickmix_genres.h"
#include "explain_bookmarks.h"
#include "settings.h"
#include "station_modes.h"

PianoReturn_t PianoRequest(PianoHandle_t *ph, PianoRequest_t *req,
        const PianoRequestType_t type) {
    switch (type) {
        case PIANO_REQUEST_RATE_SONG:
            return piano_request_rate_song(ph, req);
        case PIANO_REQUEST_ADD_FEEDBACK:
            return piano_request_add_feedback(ph, req);
        case PIANO_REQUEST_ADD_TIRED_SONG:
            return piano_request_tired_song(ph, req);
        case PIANO_REQUEST_RENAME_STATION:
            return piano_request_rename_station(ph, req);
        case PIANO_REQUEST_DELETE_STATION:
            return piano_request_delete_station(ph, req);
        case PIANO_REQUEST_CREATE_STATION:
            return piano_request_create_station(ph, req);
        case PIANO_REQUEST_ADD_SEED:
            return piano_request_add_seed(ph, req);
        case PIANO_REQUEST_DELETE_FEEDBACK:
            return piano_request_delete_feedback(ph, req);
        case PIANO_REQUEST_DELETE_SEED:
            return piano_request_delete_seed(ph, req);
        case PIANO_REQUEST_GET_STATION_INFO:
            return piano_request_station_info(ph, req);
        case PIANO_REQUEST_SET_QUICKMIX:
            return piano_request_set_quickmix(ph, req);
        case PIANO_REQUEST_GET_GENRE_STATIONS:
            return piano_request_get_genre_stations(ph, req);
        case PIANO_REQUEST_TRANSFORM_STATION:
            return piano_request_transform_station(ph, req);
        case PIANO_REQUEST_EXPLAIN:
            return piano_request_explain(ph, req);
        case PIANO_REQUEST_BOOKMARK_SONG:
            return piano_request_bookmark(ph, req, type, "bookmark.addSongBookmark");
        case PIANO_REQUEST_BOOKMARK_ARTIST:
            return piano_request_bookmark(ph, req, type, "bookmark.addArtistBookmark");
        case PIANO_REQUEST_GET_SETTINGS:
            return piano_request_get_settings(ph, req);
        case PIANO_REQUEST_CHANGE_SETTINGS:
            return piano_request_change_settings(ph, req);
        case PIANO_REQUEST_GET_STATION_MODES:
            return piano_request_get_station_modes(ph, req);
        case PIANO_REQUEST_SET_STATION_MODE:
            return piano_request_set_station_mode(ph, req);
        default:
            return melodica_piano_request_base(ph, req, type);
    }
}

PianoReturn_t PianoResponse(PianoHandle_t *ph, PianoRequest_t *req) {
    if (req != NULL) {
        switch (req->type) {
            case PIANO_REQUEST_RATE_SONG:
            case PIANO_REQUEST_ADD_FEEDBACK:
            case PIANO_REQUEST_ADD_TIRED_SONG:
                return piano_response_feedback(ph, req);
            case PIANO_REQUEST_RENAME_STATION:
            case PIANO_REQUEST_DELETE_STATION:
            case PIANO_REQUEST_CREATE_STATION:
                return piano_response_station_mutation(ph, req);
            case PIANO_REQUEST_ADD_SEED:
            case PIANO_REQUEST_DELETE_FEEDBACK:
            case PIANO_REQUEST_DELETE_SEED:
                return piano_response_seed_mutation(ph, req);
            case PIANO_REQUEST_GET_STATION_INFO:
                return piano_response_station_info(ph, req);
            case PIANO_REQUEST_SET_QUICKMIX:
            case PIANO_REQUEST_GET_GENRE_STATIONS:
            case PIANO_REQUEST_TRANSFORM_STATION:
                return piano_response_quickmix_genres(ph, req);
            case PIANO_REQUEST_EXPLAIN:
            case PIANO_REQUEST_BOOKMARK_SONG:
            case PIANO_REQUEST_BOOKMARK_ARTIST:
                return piano_response_explain_bookmarks(ph, req);
            case PIANO_REQUEST_GET_SETTINGS:
            case PIANO_REQUEST_CHANGE_SETTINGS:
                return piano_response_settings(ph, req);
            case PIANO_REQUEST_GET_STATION_MODES:
            case PIANO_REQUEST_SET_STATION_MODE:
                return piano_response_station_modes(ph, req);
            default:
                break;
        }
    }
    return melodica_piano_response_base(ph, req);
}
