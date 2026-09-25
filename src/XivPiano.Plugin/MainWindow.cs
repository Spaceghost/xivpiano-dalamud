using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XivPiano.Core;
using XivPiano.Core.Pandora;

namespace XivPiano.Plugin;

/// <summary>The player: sign in, stations, what is playing, and new stations from a search.</summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ITextureProvider textures;
    private readonly HttpClient http;
    private readonly CancellationTokenSource cts = new();

    private string email = "";
    private string password = "";
    private string filter = "";
    private string search = "";
    private Task<IReadOnlyList<SearchResult>>? searching;
    private IReadOnlyList<SearchResult> results = [];
    private string? explanation;
    private string? explainedToken;

    private string? artUrl;
    private IDalamudTextureWrap? art;
    private Task<IDalamudTextureWrap?>? loadingArt;

    public MainWindow(Plugin plugin, ITextureProvider textures, HttpClient http)
        : base("XivPiano###XivPiano")
    {
        this.plugin = plugin;
        this.textures = textures;
        this.http = http;
        Size = new Vector2(620, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 260), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        email = plugin.Config.Email;
    }

    public override void Draw()
    {
        if (plugin.Radio == null || !plugin.Pandora.LoggedIn)
        {
            DrawLogin();
            return;
        }

        var radio = plugin.Radio;
        var sidebar = 200 * ImGuiHelpers.GlobalScale;
        if (ImGui.BeginChild("##stations", new Vector2(sidebar, 0), true))
            DrawStations(radio);
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##playing", Vector2.Zero, false))
        {
            DrawNowPlaying(radio);
            ImGui.Separator();
            DrawNewStation(radio);
        }

        ImGui.EndChild();
    }

    // ---- sign in -------------------------------------------------------------------------------

    private void DrawLogin()
    {
        ImGui.TextWrapped("Sign in with your Pandora account. Pandora only serves listeners in the United States.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Email", ref email, 256);
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        var enter = ImGui.InputText("Password", ref password, 256, ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
        var remember = plugin.Config.RememberPassword;
        if (ImGui.Checkbox("Remember the password on this PC (encrypted for your Windows user)", ref remember))
        {
            plugin.Config.RememberPassword = remember;
            plugin.SaveConfig();
        }

        var busy = plugin.SigningIn;
        ImGui.BeginDisabled(busy || email.Length == 0 || password.Length == 0);
        if (ImGui.Button(busy ? "Signing in…" : "Sign in") || (enter && !busy))
        {
            plugin.SignIn(email.Trim(), password);
            password = "";
        }

        ImGui.EndDisabled();
        if (plugin.SignInError is { } error)
            ImGui.TextColored(ImGuiColors.DalamudRed, error);
        ImGui.Spacing();
        ImGui.TextDisabled("XivPiano talks to Pandora the way pianobar and Pithos do. Not affiliated with Pandora.");
    }

    // ---- stations ------------------------------------------------------------------------------

    private void DrawStations(Radio radio)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter stations", ref filter, 128);
        foreach (var s in radio.Stations)
        {
            if (filter.Length > 0 && !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var label = s.IsQuickMix ? $"{s.Name} (shuffle)" : s.Name;
            if (ImGui.Selectable($"{label}##{s.Id}", radio.Station?.Id == s.Id))
                plugin.Run(ct => radio.PlayStationAsync(s, ct));
        }

        if (radio.Stations.Count == 0)
            ImGui.TextDisabled(radio.Busy ?? "No stations yet. Make one on the right.");
    }

    // ---- now playing ---------------------------------------------------------------------------

    private void DrawNowPlaying(Radio radio)
    {
        var t = radio.Current;
        var artSize = 120 * ImGuiHelpers.GlobalScale;
        if (t != null)
        {
            DrawArt(t, artSize);
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        if (t == null)
        {
            ImGui.TextDisabled(radio.Busy ?? (radio.Station == null ? "Pick a station." : "Nothing playing."));
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudViolet, t.Title);
            ImGui.TextUnformatted(t.Artist);
            if (t.Album.Length > 0)
                ImGui.TextDisabled(t.Album);
            if (radio.Station is { } st)
                ImGui.TextDisabled($"on {st.Name}");
            var pos = plugin.Audio.Position;
            var len = t.Length > TimeSpan.Zero ? t.Length : plugin.Audio.Duration;
            var fraction = len > TimeSpan.Zero ? (float)Math.Clamp(pos / len, 0, 1) : 0f;
            ImGui.ProgressBar(fraction, new Vector2(260 * ImGuiHelpers.GlobalScale, 0), $"{pos:m\\:ss} / {len:m\\:ss}");
        }

        DrawControls(radio, t);
        ImGui.EndGroup();

        if (radio.Busy is { } busy && t != null)
            ImGui.TextDisabled(busy + "…");
        if (radio.Error is { } error)
            ImGui.TextColored(ImGuiColors.DalamudOrange, error);
        if (t != null && explainedToken == t.Token && explanation != null)
            ImGui.TextWrapped(explanation);
    }

    private void DrawControls(Radio radio, Track? t)
    {
        ImGui.BeginDisabled(t == null);
        if (ImGuiComponents.IconButton(radio.Playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play))
            radio.TogglePause();
        Tip(radio.Playing ? "Pause" : "Play");
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.StepForward))
            plugin.Run(radio.NextAsync);
        Tip("Next song");
        ImGui.SameLine();
        var loved = t?.Loved == true;
        if (ImGuiComponents.IconButton(FontAwesomeIcon.ThumbsUp, loved ? ImGuiColors.HealerGreen : null))
            plugin.Run(radio.LoveAsync);
        Tip(loved ? "You love this one" : "Love it: more like this");
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.ThumbsDown))
            plugin.Run(radio.BanAsync);
        Tip("Ban it: never on this station again");
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Moon))
            plugin.Run(radio.TiredAsync);
        Tip("Tired of it: not for a month");
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Bookmark))
            plugin.Run(ct => radio.BookmarkAsync(false, ct));
        Tip("Bookmark the song (right-click: the artist)");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            plugin.Run(ct => radio.BookmarkAsync(true, ct));
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.QuestionCircle) && t != null)
        {
            explainedToken = t.Token;
            explanation = "Asking Pandora…";
            plugin.Run(async ct => explanation = await radio.ExplainAsync(ct).ConfigureAwait(false));
        }

        Tip("Why this song?");
        ImGui.EndDisabled();

        var volume = plugin.Config.Volume;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f, $"{volume * 100:0}%%"))
        {
            plugin.Config.Volume = volume;
            plugin.Audio.Volume = volume;
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
            plugin.SaveConfig();
    }

    private void DrawArt(Track t, float size)
    {
        if (t.ArtUrl != artUrl)
        {
            artUrl = t.ArtUrl;
            art?.Dispose();
            art = null;
            loadingArt = t.ArtUrl == null ? null : LoadArtAsync(t.ArtUrl);
        }

        if (loadingArt is { IsCompleted: true } done)
        {
            art = done.IsCompletedSuccessfully ? done.Result : null;
            loadingArt = null;
        }

        if (art != null)
            ImGui.Image(art.Handle, new Vector2(size, size));
        else
            ImGui.Dummy(new Vector2(size, size));
    }

    private async Task<IDalamudTextureWrap?> LoadArtAsync(string url)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
            return await textures.CreateFromImageAsync(bytes, "XivPiano album art", cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or NotSupportedException)
        {
            return null; // no art is fine
        }
    }

    // ---- new station ---------------------------------------------------------------------------

    private void DrawNewStation(Radio radio)
    {
        ImGui.TextUnformatted("New station");
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        var go = ImGui.InputTextWithHint("##search", "An artist, a song or a genre", ref search, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Search") || go) && search.Trim().Length > 0 && searching is not { IsCompleted: false })
            searching = plugin.Pandora.SearchAsync(search.Trim(), cts.Token);
        if (radio.Current is { } t)
        {
            if (ImGui.SmallButton("From this song"))
                plugin.Run(ct => radio.CreateAndPlayAsync(() => plugin.Pandora.CreateStationFromTrackAsync(t.Token, false, ct), ct));
            ImGui.SameLine();
            if (ImGui.SmallButton("From this artist"))
                plugin.Run(ct => radio.CreateAndPlayAsync(() => plugin.Pandora.CreateStationFromTrackAsync(t.Token, true, ct), ct));
        }

        if (searching is { IsCompleted: true } done)
        {
            results = done.IsCompletedSuccessfully ? done.Result : [];
            searching = null;
        }

        if (searching != null)
            ImGui.TextDisabled("Searching…");
        foreach (var r in results.Take(12))
        {
            var what = r.Kind switch
            {
                SearchKind.Artist => "artist",
                SearchKind.Song => $"song by {r.Artist}",
                _ => "genre",
            };
            if (ImGui.Selectable($"{r.Name}  ({what})##{r.MusicToken}"))
            {
                results = [];
                plugin.Run(ct => radio.CreateAndPlayAsync(() => plugin.Pandora.CreateStationAsync(r.MusicToken, ct), ct));
            }
        }
    }

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    public void Dispose()
    {
        cts.Cancel();
        art?.Dispose();
        cts.Dispose();
    }
}
