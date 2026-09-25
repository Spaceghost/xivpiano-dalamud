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

    public void Clamp() => Volume = Math.Clamp(Volume, 0f, 1f);
}
