# XivPiano melodica bridge

`melodica_bridge.c` adapts the vendored melodica C libpiano implementation to
XivPiano's asynchronous managed client. It is an adapter, not a second Pandora
protocol implementation. Every supported operation calls `PianoRequest` and
`PianoResponse`; Blowfish and JSON handling come from that same vendored code.
See `vendor/melodica/SOURCE.json` for the pinned source and included notices.

## Ownership and transport

1. Create a session with the selected partner's host, identity and cipher keys.
2. Call `xp_prepare` with the Pandora method and managed JSON options.
3. Copy `xp_request_url` and `xp_request_body`, then send them with `HttpClient`.
4. Call `xp_accept` with the response. It consumes the prepared operation.
5. Call `xp_abort` if transport, cancellation or managed parsing prevents step 4.
6. Free the session on logout, replacement or disposal.

The caller serializes the entire operation, including its asynchronous HTTP
interval. There is at most one pending operation. URL and body pointers are
borrowed until acceptance or abort. No native pointer enters a managed domain
model. The native handle retains authentication and cipher state; native
catalogs and returned lists are freed after each transaction.

All URLs use HTTPS, including historical libpiano operations whose `secure`
flag was false. Changing between Android and Pandora One creates a new session.
`xp_server_time` returns the exact timestamp decrypted during the most recent
accepted partner login. It does not tick; the managed clock maintains elapsed
time and can supply `syncTime` for deterministic host tests.

## Preserving application behavior

The bridge decrypts the actual generated native request, merges the caller's
top-level options and reencrypts through the native outgoing cipher. This
preserves MP3 format requests, subscriber flags, genre search, nested options
and separate station IDs/tokens. Native authentication tokens and partner
identity cannot be replaced with different caller values.

The original response remains with the managed client for account information,
audio bitrate selection, timestamps, genre results and other fields absent
from historical libpiano's structs. For playlists, the managed adapter explicitly
projects each playable track's chosen MP3 into `audioUrlMap.highQuality` before
native acceptance. The original JSON remains the source for managed models.
Unplayable tracks are omitted from this validation view. Native parser errors
are still errors; there is no successful-response bypass.

`station.addFeedback` maps to libpiano's public `RATE_SONG` operation. Its
low-level `ADD_FEEDBACK` response intentionally has no successful contract.

## Return values

| Value | Meaning |
|---|---|
| `1` | Native `PIANO_RET_OK` |
| `3` | Native partner login completed; user login continues separately |
| Other nonnegative values | Actual `PianoReturn_t`, including Pandora errors starting at 1024 |
| Negative values | Bridge argument, state, unsupported-method, allocation or identity errors |

Diagnostics contain fixed descriptions and never incorporate credentials,
tokens, request bodies or server messages. The managed client preserves
Pandora's original error code and message separately.

## Platforms and checks

The game process needs `runtimes/win-x64/native/xivpiano_melodica.dll` on both
Windows and Wine. The Linux `.so` is for host-side managed tests. Both expose
the same ten `xp_*` C ABI functions. The C protocol backend requires no json-c
or libgcrypt DLLs.

Dalamud loads managed dependencies from memory, so `Assembly.Location` is not
an installation path. The plugin supplies the DLL's absolute path using
`IDalamudPluginInterface.AssemblyLocation`. A collectible load context uses its
own private native shadow copy, allowing the installed DLL to be replaced.
The resolver caches one module handle per Core load context. Session leases
keep it alive through asynchronous requests and SafeHandle cleanup; an unload
request releases the module after its last session closes. Ordinary sign-out
releases a session while the active context keeps its cached module.

`tests/bridge_smoke.c` dynamically links the actual bridge. It covers partner
and user login, every RPC mapping, retained options, repeated output ownership,
identity mismatch, pending-operation rejection, abort, malformed responses and
native playlist/mode validation. It also runs under Wine against the Windows
DLL. The same test can be linked with AddressSanitizer and UndefinedBehaviorSanitizer
on the Fedora build host.
