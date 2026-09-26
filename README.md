# XivPiano

<img src="images/icon.png" width="96" height="96" align="right" alt="XivPiano icon">

**[Install](https://spacegho.st/mods/ffxiv/plugins/) · [Changelog](CHANGELOG.md)**

**Your Pandora stations inside FINAL FANTASY XIV.**

XivPiano is a Dalamud plugin that plays Pandora radio in the game, in the tradition of
[pianobar](https://github.com/PromyLOPh/pianobar), [Pithos](https://github.com/pithos/pithos) and
[Elpis](https://github.com/adammhaile/Elpis): sign in, pick a station, and it plays while you do everything else.
Thumbs up and down teach the station, *tired of it* shelves a song for a month, and a search makes a new station
from any artist, song or genre.

> **Status: experimental, not heard in game yet.** Host tests cover the Blowfish cipher against its published test
> vectors, Pandora's request wrapping, the login sequence, playlists, feedback, search, the radio queue (skipping
> expired and broken links, refilling before it runs dry) against a fake Pandora, and the partner login has been
> checked against Pandora itself. Sound, the window and the game-side behaviour are implemented intent until they
> have been seen working.

> **Pandora only serves the United States**, and you need a Pandora account (free or paid). XivPiano is not
> affiliated with Pandora or Sirius XM.

## What you get

- **Your stations**, with a filter, and **Shuffle** where your account has it.
- **Now playing**: album art, title, artist, album, a progress bar and the station.
- **Play / pause, next song, thumbs up, thumbs down** (also skips), **tired of it** (not for a month, on any
  station), **bookmark** the song (right-click: the artist), and **why this song?** in Pandora's own words.
- **New stations** from a search (artist, song or genre), or from the song or artist that is playing.
- **Manage stations** (right-click one): rename, delete, in or out of **Shuffle**, and its **seeds, thumbs and
  modes**: add variety, remove a seed, take back a thumb, switch Pandora's station modes. Sort A to Z.
- **History** of the last 20 songs, each still rateable and bookmarkable; **Up next**; **Open on Pandora**; Pandora's
  **genre stations** to browse.
- **Scrobbling** to **Last.fm** (your own free API key) and **ListenBrainz** (your user token).
- **Toasts** for new songs, the keyboard's **media keys**, and Pandora's **explicit content filter**.
- **The song in the server info bar**: click opens XivPiano, right-click pauses.
- **Plays nicely with the game**: mutes the game's background music while a song plays and puts it back as it was,
  pauses during cutscenes and resumes after, and can say each new song in chat.
- **Stays signed in if you want**: the password is encrypted for your Windows (or Wine) user with DPAPI before it
  is saved, and XivPiano can resume your last station when the game starts.

## Pandora Plus and Premium

A paid account streams **192 kbit/s MP3** (free accounts get 128), as it does in Elpis, pianobar and Pithos.
XivPiano recognises a paid account from Pandora's own flags (subscriber, or no audio ads) and asks for the
192 kbit/s stream through the ordinary Android client, the way Elpis does. When Pandora does not offer it there,
XivPiano signs in again as the Pandora One client, the way Pithos does for subscribers, and uses that client's
192 kbit/s stream. In *Settings*:

- **Audio quality**: *best my account gets*, or *128 kbit/s* to save data.
- **Pandora client**: *automatic* (the above), *Android* (as Elpis and pianobar), or *Pandora One* (as Pithos;
  a free account falls back to Android).

The window shows your plan, the client in use and each song's bitrate.

## Requirements

- FFXIV with Dalamud (API 15), on Windows or on Linux under Wine. Songs are MP3 (128 kbit/s, or 192 on Plus and Premium), decoded in managed
  code ([NLayer](https://github.com/naudio/NLayer)) and played through winmm ([NAudio](https://github.com/naudio/NAudio)),
  so nothing depends on Windows codecs.
- A Pandora account (free, Plus or Premium), in the United States.

## Commands

| Command | Does |
|---|---|
| `/piano` | open or close XivPiano |
| `/piano play`, `/piano pause` | play or pause |
| `/piano next` | next song |
| `/piano love`, `/piano ban` | thumbs up; thumbs down (and skip) |
| `/piano tired` | not for a month (and skip) |
| `/piano stop` | stop |
| `/piano station <name>` | tune in to the first station whose name contains `<name>` |

They work in macros too.

## For other plugins

| IPC gate | Type | Does |
|---|---|---|
| `XivPiano.NowPlaying` | `string ()` | JSON: `playing`, `station`, `title`, `artist`, `album`, `loved`, `positionSeconds`, `lengthSeconds` |
| `XivPiano.Control` | `bool (string action)` | `play`, `pause`, `toggle`, `next`, `love`, `ban`, `tired`, `stop`; false when unknown or not signed in |

## How it talks to Pandora

XivPiano speaks Pandora's JSON API the way pianobar, Pithos and pydora do (documented at
[6xq.net/pandora-apidoc](https://6xq.net/pandora-apidoc/json/)): an Android partner login, then your user login,
with request bodies wrapped in Blowfish. Its code is its own, written from that documentation and those clients'
behaviour. The Blowfish tables are generated from the digits of pi (`tools/blowfish-tables.py`) and checked against
the published test vectors. Ads in a playlist are left out, as those clients do.

## Building

```sh
tools/fetch-dalamud.sh
dotnet test tests/XivPiano.Core.Tests
dotnet build src/XivPiano.Plugin/XivPiano.Plugin.csproj -c Release
tools/install-dev.sh
```

`XIVPIANO_LIVE=1 dotnet test tests/XivPiano.Core.Tests` also checks the partner login against Pandora itself.

## License

MIT. NAudio and NLayer are MIT as well.
