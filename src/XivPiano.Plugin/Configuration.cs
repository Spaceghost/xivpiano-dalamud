using Dalamud.Configuration;

namespace XivPiano.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string Email { get; set; } = "";

    /// <summary>The Pandora password, encrypted for this Windows (or Wine) user with DPAPI; empty = not kept.</summary>
    public string ProtectedPassword { get; set; } = "";

    public bool RememberPassword { get; set; } = true;

    /// <summary>Sign in and resume the last station when the plugin starts.</summary>
    public bool AutoStart { get; set; }

    public string LastStationId { get; set; } = "";

    /// <summary>Best = 192 kbit/s MP3 on a paid account (Plus, Premium), 128 otherwise; Standard = always 128.</summary>
    public XivPiano.Core.Pandora.AudioQuality Quality { get; set; } = XivPiano.Core.Pandora.AudioQuality.Best;

    /// <summary>Which Pandora client to sign in as (Automatic moves a paid account to Pandora One when needed).</summary>
    public XivPiano.Core.Pandora.ClientChoice Client { get; set; } = XivPiano.Core.Pandora.ClientChoice.Automatic;

    /// <summary>0..1, on top of the song's own replay gain.</summary>
    public float Volume { get; set; } = 0.6f;

    /// <summary>Mute the game's background music while a song plays (restored when it stops).</summary>
    public bool MuteGameMusic { get; set; } = true;

    /// <summary>Pause during cutscenes and resume after.</summary>
    public bool PauseInCutscenes { get; set; } = true;

    /// <summary>Duty content types whose game music should take priority; empty means disabled.</summary>
    public HashSet<uint> GameMusicDutyTypes { get; set; } = [];

    /// <summary>Opt-in volume attenuation while a Talk window is open.</summary>
    public bool DuckDialogue { get; set; }

    public float DuckTo { get; set; } = 0.25f;

    /// <summary>Print each new song locally in the chat log; never sends a game-chat message.</summary>
    public bool AnnounceInChat { get; set; }

    /// <summary>Show the song in the server info bar (click: open, right-click: pause).</summary>
    public bool ShowInServerInfoBar { get; set; } = true;

    /// <summary>List stations A to Z (else in Pandora's order, newest first).</summary>
    public bool SortStationsByName { get; set; } = true;

    /// <summary>A toast for each new song, like Pithos' desktop notifications.</summary>
    public bool ToastOnNewSong { get; set; }

    /// <summary>Play/pause, next and stop from the keyboard's media keys while the game has focus.</summary>
    public bool MediaKeys { get; set; } = true;

    /// <summary>Opt-in saving of downloaded songs; the library is private until explicitly shared.</summary>
    public bool SaveRecordings { get; set; }

    public int RecordingStorageGiB { get; set; } = 2;

    public bool ScrobbleToLastFm { get; set; }

    /// <summary>Your own Last.fm API account (last.fm/api): the key, and the secret encrypted like the password.</summary>
    public string LastFmApiKey { get; set; } = "";

    public string ProtectedLastFmSecret { get; set; } = "";

    /// <summary>The session Last.fm issues after you approve XivPiano, encrypted.</summary>
    public string ProtectedLastFmSession { get; set; } = "";

    public bool ScrobbleToListenBrainz { get; set; }

    /// <summary>Your ListenBrainz user token, encrypted.</summary>
    public string ProtectedListenBrainzToken { get; set; } = "";

    public bool ScrobbleToSpaceghost { get; set; }

    public string SpaceghostScrobblesEndpoint { get; set; } = XivPiano.Core.SpaceghostScrobbles.DefaultEndpoint;

    /// <summary>The hosted scrobbling client token, encrypted for this Windows/Wine user.</summary>
    public string ProtectedSpaceghostScrobblesToken { get; set; } = "";

    /// <summary>Send the zone and a map cell with listens and now-playing to Spaceghost Scrobbles.</summary>
    public bool SendLocation { get; set; } = true;

    /// <summary>Tint the in-game map with where people listen (needs Spaceghost Scrobbles).</summary>
    public bool ShowMapOverlay { get; set; } = true;

    /// <summary>Show a note over nearby characters who are listening and share it (needs SharePresence).</summary>
    public bool ShowWorldIndicators { get; set; } = true;

    /// <summary>Consent, off by default: crew members with this on see what you play above your character.</summary>
    public bool SharePresence { get; set; }

    /// <summary>Calibration for the map overlay: nudge in pixels, scale of the detected map area.</summary>
    public float MapNudgeX { get; set; }

    public float MapNudgeY { get; set; }

    public float MapScale { get; set; } = 1f;

    /// <summary>Outline the map area the overlay detected, for calibration.</summary>
    public bool ShowMapArea { get; set; }

    public void Clamp()
    {
        GameMusicDutyTypes ??= [];
        RecordingStorageGiB = Math.Clamp(RecordingStorageGiB, 1, 100);
        SpaceghostScrobblesEndpoint = XivPiano.Core.SpaceghostScrobbles.Current(SpaceghostScrobblesEndpoint);
        ProtectedSpaceghostScrobblesToken ??= "";
        MapNudgeX = float.IsFinite(MapNudgeX) ? Math.Clamp(MapNudgeX, -200f, 200f) : 0f;
        MapNudgeY = float.IsFinite(MapNudgeY) ? Math.Clamp(MapNudgeY, -200f, 200f) : 0f;
        MapScale = float.IsFinite(MapScale) ? Math.Clamp(MapScale, 0.5f, 1.5f) : 1f;
        Volume = float.IsFinite(Volume) ? Math.Clamp(Volume, 0f, 1f) : 0.6f;
        DuckTo = float.IsFinite(DuckTo) ? Math.Clamp(DuckTo, 0f, 1f) : 0.25f;
    }
}
