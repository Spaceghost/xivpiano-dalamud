# Saved recordings

## Save and replay

1. Open `/piano`, choose **Recordings**, and enable **Save songs I play**.
2. Play a station. Each successfully downloaded song is saved once as an MP3.
3. Use **Play** in the library to replay it, including without signing into Pandora.

The default local limit is 2 GiB, adjustable from 1 to 100 GiB. The library holds
at most 5,000 unique recordings; an individual recording may be at most 64 MiB.
A full library stops new saves without deleting existing songs or stopping radio
playback. The error remains visible in the Recordings tab.

**Open recordings folder** shows the files. In the remote Fedora launcher's Wine
profile this is normally `/home/player/.xlcore/pluginConfigs/XivPiano/recordings`.
Each MP3 is named by its SHA256, with a JSON sidecar containing title, artist,
album, duration, bitrate, replay gain, size and save time. No Pandora password,
account token, playlist token or expiring audio URL is written into the sidecar.
Writes use temporary files and atomic replacement; playback checks the MP3 hash.
Repeated downloads of identical audio reuse the saved recording.

Saved playback has ordinary pause, stop, volume, automatic duty/cutscene pause
and dialogue controls. Next ends single-recording playback. Pandora rating,
bookmark and explanation actions are unavailable for saved files because their
account tokens are deliberately not retained. Replays can scrobble to enabled
destinations after the usual listening threshold.

## Private links for friends

1. Save a Spaceghost Scrobbles client token in **Settings → Scrobbling**.
2. Choose **Upload & share** beside a recording, then **Copy private link**.
3. Send that link to a friend. Its browser player has playback, seeking and MP3 download.

Uploading is explicit, never automatic. Hosted storage is limited to 2 GiB per
account and 20 GiB across this service. Recordings remain until deleted; links
expire after 30 days. Creating a new link invalidates the previous link for that
song. **Revoke link** invalidates existing friend playback grants on their next
request. Revoking a client token stops that client's API access, and does not
revoke previously created listening links.

The private R2 bucket has no public object URL. A link's random capability is in
its URL fragment, so it is excluded from HTTP URLs. The browser exchanges it for
a one-hour HttpOnly playback cookie scoped to that recording. The host stores
only capability hashes and checks expiry, revocation and account bans before
serving audio. Range requests support seeking without loading the entire MP3.
Already downloaded audio stays with its recipient.

The [dashboard](https://spacegho.st/mods/ffxiv/scrobble/) lists hosted recordings,
plays them, creates/revokes links and deletes hosted MP3s. Local deletion and
hosted deletion are independent. Removing listen history does not remove recordings.
Failed uploads keep the local MP3 for retry; unfinished quota reservations expire
after 15 minutes. Bounded scheduled cleanup retries deletions and sweeps old orphan
objects left by an interrupted write.

## Evidence and scope

The feature is implemented in the plugin and host. The Fedora Incus build checks
compilation; it does not establish actual game output, user-account upload or
friend-browser playback acceptance. Release notes keep that status explicit.
