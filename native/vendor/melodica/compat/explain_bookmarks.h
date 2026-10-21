#ifndef MELODICA_PIANO_EXPLAIN_BOOKMARKS_INTERNAL_H
#define MELODICA_PIANO_EXPLAIN_BOOKMARKS_INTERNAL_H

#define PIANO_EXPLAIN_SIZE 768u

static PianoReturn_t piano_request_explain(PianoHandle_t *ph, PianoRequest_t *req) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoRequestDataExplain_t *data = (PianoRequestDataExplain_t *)req->data;
    if (data == NULL || data->song == NULL || data->song->trackToken == NULL) {
        return PIANO_RET_ERR;
    }
    req->type = PIANO_REQUEST_EXPLAIN;
    req->secure = false;
    char *json = piano_mutation_json(ph, "trackToken", data->song->trackToken,
        NULL, NULL, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, "track.explainTrack", json);
}

static PianoReturn_t piano_request_bookmark(PianoHandle_t *ph, PianoRequest_t *req,
        const PianoRequestType_t type, const char *method) {
    if (!piano_mutation_ready(ph, req)) return PIANO_RET_ERR;
    PianoSong_t *song = (PianoSong_t *)req->data;
    if (song == NULL || song->trackToken == NULL) return PIANO_RET_ERR;
    req->type = type;
    req->secure = false;
    char *json = piano_mutation_json(ph, "trackToken", song->trackToken,
        NULL, NULL, piano_mutation_timestamp(ph));
    if (json == NULL) return PIANO_RET_OUT_OF_MEMORY;
    return piano_authenticated_request(ph, req, method, json);
}

static void piano_explain_cat(char *dest, const char *src, size_t len) {
    while (*dest != '\0' && len > 1u) {
        ++dest;
        --len;
    }
    while (*src != '\0' && len > 1u) {
        *dest++ = *src++;
        --len;
    }
    *dest = '\0';
}

static PianoReturn_t piano_response_explain_data(PianoRequest_t *req,
        const MelodicaJsonDoc *doc, const size_t result) {
    static const char prefix[] = "We're playing this track because it features ";
    PianoRequestDataExplain_t *data = (PianoRequestDataExplain_t *)req->data;
    if (data == NULL) return PIANO_RET_INVALID_RESPONSE;

    const size_t explanations = melodica_json_object_get(doc, result, "explanations");
    if (explanations == MELODICA_JSON_NONE) return PIANO_RET_OK;
    if (explanations >= doc->count || doc->tokens[explanations].type != MELODICA_JSON_ARRAY) {
        return PIANO_RET_INVALID_RESPONSE;
    }
    const size_t count = melodica_json_array_count(doc, explanations);
    if (count == 0u) return PIANO_RET_OK;

    char *text = (char *)malloc(PIANO_EXPLAIN_SIZE);
    if (text == NULL) return PIANO_RET_OUT_OF_MEMORY;
    text[0] = '\0';
    piano_explain_cat(text, prefix, PIANO_EXPLAIN_SIZE);

    for (size_t i = 0u; i < count; ++i) {
        const size_t item = melodica_json_array_get(doc, explanations, i);
        const size_t focus = melodica_json_object_get(doc, item, "focusTraitName");
        if (focus == MELODICA_JSON_NONE) continue;
        if (focus >= doc->count || melodica_json_is_null(doc, focus)) {
            free(text);
            return PIANO_RET_INVALID_RESPONSE;
        }
        char *trait = melodica_json_string_dup(doc, focus);
        if (trait == NULL) {
            free(text);
            return PIANO_RET_OUT_OF_MEMORY;
        }
        piano_explain_cat(text, trait, PIANO_EXPLAIN_SIZE);
        free(trait);
        /* Preserve upstream's index-based punctuation exactly, including the
         * one-element unsigned underflow quirk in the first comparison. */
        if (i < count - 2u) {
            piano_explain_cat(text, ", ", PIANO_EXPLAIN_SIZE);
        } else if (i == count - 2u) {
            piano_explain_cat(text, " and ", PIANO_EXPLAIN_SIZE);
        } else {
            piano_explain_cat(text, ".", PIANO_EXPLAIN_SIZE);
        }
    }

    data->retExplain = text;
    return PIANO_RET_OK;
}

static PianoReturn_t piano_response_explain_bookmarks(PianoHandle_t *ph,
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

    switch (req->type) {
        case PIANO_REQUEST_EXPLAIN:
            ret = piano_response_explain_data(req, &doc,
                melodica_json_object_get(&doc, root, "result"));
            break;
        case PIANO_REQUEST_BOOKMARK_SONG:
        case PIANO_REQUEST_BOOKMARK_ARTIST:
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

#undef PIANO_EXPLAIN_SIZE

#endif
