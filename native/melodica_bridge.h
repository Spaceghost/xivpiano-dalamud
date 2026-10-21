#ifndef XIVPIANO_MELODICA_BRIDGE_H
#define XIVPIANO_MELODICA_BRIDGE_H

#include <stdint.h>

#if defined(_WIN32)
#if defined(XIVPIANO_MELODICA_BUILD)
#define XP_EXPORT __declspec(dllexport)
#else
#define XP_EXPORT __declspec(dllimport)
#endif
#define XP_CALL __cdecl
#elif defined(__GNUC__) || defined(__clang__)
#define XP_EXPORT __attribute__((visibility("default")))
#define XP_CALL
#else
#define XP_EXPORT
#define XP_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct xp_session xp_session;

enum {
    XP_BRIDGE_ABI_VERSION = 1,
    XP_ERROR_ARGUMENT = -1,
    XP_ERROR_STATE = -2,
    XP_ERROR_METHOD = -3,
    XP_ERROR_MEMORY = -4,
    XP_ERROR_AUTH = -5
};

/* All strings are NUL-terminated UTF-8. No credentials enter diagnostics.
 * Session access must be serialized, including the asynchronous HTTP interval.
 * Use a new session when changing Android/Pandora One partner identities. */
XP_EXPORT uint32_t XP_CALL xp_bridge_abi_version(void);
XP_EXPORT xp_session *XP_CALL xp_session_create(const char *host,
        const char *partner_user, const char *partner_password,
        const char *device, const char *decrypt_key, const char *encrypt_key);
XP_EXPORT void XP_CALL xp_session_free(xp_session *session);

/* prepare calls the real PianoRequest, then merges application options into
 * its generated JSON and encrypts using the same native session cipher.
 * Existing native authentication fields cannot be overridden. syncTime may be
 * supplied by the managed clock abstraction. Returns PianoReturn_t or a
 * negative XP_ERROR_*. At most one prepared request may exist per session. */
XP_EXPORT int XP_CALL xp_prepare(xp_session *session,
        const char *method, const char *body_json);
XP_EXPORT const char *XP_CALL xp_request_url(const xp_session *session);
XP_EXPORT const char *XP_CALL xp_request_body(const xp_session *session);

/* accept invokes the real PianoResponse and consumes the prepared request.
 * The caller retains the original JSON for its richer managed models.
 * OK is 1, partner-login CONTINUE_REQUEST is 3, API errors are >=1024.
 * A successful JSON envelope does not excuse a native parser error. */
XP_EXPORT int XP_CALL xp_accept(xp_session *session, const char *response_json);
XP_EXPORT void XP_CALL xp_abort(xp_session *session);

/* Exact decrypted timestamp from the latest accepted partner login, rather
 * than a ticking wall clock. Zero before partner login. */
XP_EXPORT int64_t XP_CALL xp_server_time(const xp_session *session);
XP_EXPORT const char *XP_CALL xp_error(const xp_session *session);

#ifdef __cplusplus
}
#endif
#endif
