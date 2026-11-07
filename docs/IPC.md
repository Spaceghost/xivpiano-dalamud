# XivPiano IPC

## Anonymous played-audio features (v1)

`GetIpcSubscriber<string>("XivPiano.AudioFeatures.v1").InvokeFunc()` polls JSON;
it is read-only and neither controls playback nor performs network requests.
Recommended polling is at most 20Hz. No assembly reference to XivPiano is needed.

```json
{"version":1,"streamId":1,"sequence":20,"playing":true,"fresh":true,"ageSeconds":0.01,"positionSeconds":0.41,"rms":0.2,"peak":0.5,"low":0.1,"mid":0.15,"high":0.05,"onset":0.3}
```

`streamId` is an increasing signed 64-bit generation within this plugin instance;
`sequence` is a signed 64-bit counter of 20ms PCM windows within that stream.
Counters can restart after plugin reload. Deduplicate onset impulses by both.
Features are finite 0..1 amplitudes after replay gain, user volume and clipping,
not estimated BPM, notes, MIDI or fabricated rhythm. Low/mid/high are broad
first-order crossover energy bands at 250Hz/2000Hz, not a calibrated spectrum.
Stereo channel energies are averaged without cancelling opposite-phase channels.

Fresh snapshots also include the backward-compatible optional field
`"spectrum":{"minimumHz":40,"maximumHz":16000,"bands":[/* 32 amplitudes */]}`.
`maximumHz` is capped at the source Nyquist frequency (`sampleRate/2`). The 32
bands run low-to-high with equal logarithmic edges
`minimumHz * (maximumHz/minimumHz)^(i/32)` for `i=0..32`. Each finite 0..1 value
is linear RMS amplitude from integrated FFT power of the actual post-volume PCM,
not interpolation of the three crossover bands. The trailing periodic Hann FFT
uses the next power of two covering at least 40ms (512..8192 samples), updated
every 20ms. Its finite `sampleRate/FFTLength` resolution means neighboring low
bands can share spectral resolution; it is not 32 independently resolved notes.
Channel powers are averaged, so opposite-phase stereo remains audible. Fresh
silence becomes a zero spectrum after this short trailing window clears. Paused,
stale, missing or invalid playback omits `spectrum`; older v1 clients can ignore
the added field. The IPC name, version and original fields are unchanged.

Analysis is tapped from PCM actually submitted to winmm; polling selects only
the last completed window at/before `WaveOut.GetPosition()` (played bytes), not
the decoder cursor, which can be several buffers ahead. A 128-entry fixed history
holds roughly 2.56s. Windows hardware/Wine may have additional device latency;
this is not a promise of sample-accurate speaker timing. A stalled cursor beyond
0.2s or missing/old history gives `fresh:false` and zero features. Pause/stop,
invalid cursor, unavailable device, or analyzer failure also give zero features;
pause/stop/device failure report `playing:false`. Inactive age is capped at 1s.
An initial/resumed window suppresses an old onset rather than inventing a beat.

The audio callback uses preallocated state/history and no IPC/serialization/IO;
normal work is bounded to at most one second of 1..8-channel PCM per call. FFT
work is bounded by 50 windows times `channels*N*log2(N)/2` butterflies, plus band
integration; all audio scratch and the 128 spectrum-history entries are fixed
storage. Published spectra are immutable value copies, not borrowed arrays.
Unsupported/malformed analysis is disabled
or dropped without interrupting music. JSON allocates only on the polling thread.
Consumers must stop/damp visuals on missing IPC, wrong version, `fresh:false`,
pause, malformed values, or their own receipt timeout. There are no track names,
stream URLs, account data, credentials or remote control in this snapshot.

Synthetic PCM tests cover frequency separation, energy normalization, volume,
anti-phase audio, bounded formats, immutable history, playback-cursor causality,
inactive resets, backward-compatible JSON and allocation-free Push/Read.
An API15 build checks the producer; live audio/visual
alignment and real subscriber delivery still need in-game acceptance.

## Existing song/control gates

`XivPiano.NowPlaying` is an `ICallGateSubscriber<string>` polling function. `XivPiano.Control` accepts a string and returns bool; its existing controls affect playback. Receiving events does not invoke controls.

`XivPiano.SongChanged` is a message gate: obtain `GetIpcSubscriber<string, object>("XivPiano.SongChanged")`, subscribe an `Action<string>` with `Subscribe`, and call `Unsubscribe` with the same delegate when your plugin unloads. There is no registered function on this message gate. Subscribe first, then poll NowPlaying if an initial snapshot is needed.

The message is the same JSON produced by NowPlaying:

```json
{"playing":true,"station":"My Station","title":"Song","artist":"Artist","album":"Album","loved":false,"positionSeconds":0,"lengthSeconds":210}
```

Station/title/artist/album/loved may be null when no track exists in the polling result. Subscribers should tolerate unknown future fields. No stream URL, account credentials or private playlist token is included.

Events run synchronously on the Dalamud framework thread when the existing update loop detects a new track token. Return quickly: copy/queue the payload and do expensive processing elsewhere. Do not block on IO or invoke playback controls from the callback. Subscriber exceptions are caught and logged to keep XivPiano's update running, but this cannot prevent a slow subscriber from stalling the game or guarantee that every subscriber receives a message after another fails.

There is no replay queue or initial event. Pause/resume, progress, ratings and station-only changes do not generate SongChanged. A frame with no track resets deduplication so a subsequently observed play can emit again. A same-token transition entirely between frames may be missed. Disposal removes the framework update handler; consumers must unsubscribe their own delegates.

The API15 Release build verifies the message gate type/call, not live subscriber delivery. Actual in-game event ordering, initial poll/subscribe races and consumer integration remain unverified. No game-chat commands or real listener accounts were used for this implementation.
