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

    /// <summary>Say each new song in chat.</summary>
    public bool AnnounceInChat { get; set; }

    /// <summary>Show the song in the server info bar (click: open, right-click: pause).</summary>
    public bool ShowInServerInfoBar { get; set; } = true;

    /// <summary>List stations A to Z (else in Pandora's order, newest first).</summary>
    public bool SortStationsByName { get; set; } = true;

    /// <summary>A toast for each new song, like Pithos' desktop notifications.</summary>
    public bool ToastOnNewSong { get; set; }

    /// <summary>Play/pause, next and stop from the keyboard's media keys while the game has focus.</summary>
    public bool MediaKeys { get; set; } = true;

    public bool ScrobbleToLastFm { get; set; }

    /// <summary>Your own Last.fm API account (last.fm/api): the key, and the secret encrypted like the password.</summary>
    public string LastFmApiKey { get; set; } = "";

    public string ProtectedLastFmSecret { get; set; } = "";

    /// <summary>The session Last.fm issues after you approve XivPiano, encrypted.</summary>
    public string ProtectedLastFmSession { get; set; } = "";

    public bool ScrobbleToListenBrainz { get; set; }

    /// <summary>Your ListenBrainz user token, encrypted.</summary>
    public string ProtectedListenBrainzToken { get; set; } = "";

    public void Clamp() => Volume = Math.Clamp(Volume, 0f, 1f);
}
