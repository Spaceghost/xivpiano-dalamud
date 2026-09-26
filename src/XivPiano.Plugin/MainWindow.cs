using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using XivPiano.Core;
using XivPiano.Core.Pandora;

namespace XivPiano.Plugin;

/// <summary>
/// The player: sign in, stations (with their management menu), and tabs for what is playing, the history, what
/// comes next, new stations and settings, covering what pianobar, Pithos and Elpis offer.
/// </summary>
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
    private Task<IReadOnlyList<GenreCategory>>? loadingGenres;
    private IReadOnlyList<GenreCategory>? genres;
    private string? explanation;
    private string? explainedToken;

    private Station? renaming;
    private string newName = "";
    private Station? deleting;
    private bool choosingShuffle;
    private HashSet<string> shuffleChoice = [];

    private string lastFmSecret = "";
    private string listenBrainzToken = "";
    private string? lastFmToken;
    private string? lastFmMessage;

    private string? artUrl;
    private IDalamudTextureWrap? art;
    private Task<IDalamudTextureWrap?>? loadingArt;

    public MainWindow(Plugin plugin, ITextureProvider textures, HttpClient http)
        : base("XivPiano###XivPiano")
    {
        this.plugin = plugin;
        this.textures = textures;
        this.http = http;
        Size = new Vector2(680, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(460, 280), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
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
        DrawAccountLine();
        var sidebar = 210 * ImGuiHelpers.GlobalScale;
        if (ImGui.BeginChild("##stations", new Vector2(sidebar, 0), true))
            DrawStations(radio);
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##right", Vector2.Zero, false))
        {
            if (ImGui.BeginTabBar("##tabs"))
            {
                if (ImGui.BeginTabItem("Now playing"))
                {
                    DrawNowPlaying(radio);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("History"))
                {
                    DrawHistory(radio);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Up next"))
                {
                    DrawUpcoming(radio);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("New station"))
                {
                    DrawNewStation(radio);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Settings"))
                {
                    DrawSettings();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        ImGui.EndChild();
        DrawPopups(radio);
    }

    // ---- sign in -------------------------------------------------------------------------------

    private void DrawLogin()
    {
        ImGui.TextWrapped("Sign in with your Pandora account (free, Plus or Premium). Pandora only serves listeners in the United States.");
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
        ImGui.TextDisabled("XivPiano talks to Pandora the way pianobar, Pithos and Elpis do. Not affiliated with Pandora.");
    }

    private void DrawAccountLine()
    {
        if (plugin.Pandora.Account is not { } a)
            return;
        var quality = a.IsPaid && plugin.Config.Quality == AudioQuality.Best ? 192 : 128;
        if (a.IsPaid)
            ImGui.TextColored(ImGuiColors.ParsedGold, "Pandora Plus / Premium");
        else
            ImGui.TextDisabled("Pandora (free)");
        ImGui.SameLine();
        ImGui.TextDisabled($"· {quality} kbit/s MP3 · {a.Client} client · {plugin.Config.Email}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Sign out"))
            plugin.SignOut();
    }

    // ---- stations ------------------------------------------------------------------------------

    private void DrawStations(Radio radio)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter stations", ref filter, 128);
        IEnumerable<Station> list = radio.Stations;
        if (plugin.Config.SortStationsByName)
            list = list.OrderByDescending(s => s.IsQuickMix).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);
        var shuffle = radio.Stations.FirstOrDefault(s => s.IsQuickMix);
        foreach (var s in list)
        {
            if (filter.Length > 0 && !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var mark = !s.IsQuickMix && shuffle?.QuickMixIds.Contains(s.Id) == true ? " ·" : "";
            var label = s.IsQuickMix ? $"{s.Name} (shuffle)" : s.Name + mark;
            if (ImGui.Selectable($"{label}##{s.Id}", radio.Station?.Id == s.Id))
                plugin.Run(ct => radio.PlayStationAsync(s, ct));
            if (ImGui.IsItemHovered() && mark.Length > 0)
                ImGui.SetTooltip("In Shuffle");
            if (ImGui.BeginPopupContextItem($"##menu{s.Id}"))
            {
                DrawStationMenu(radio, s, shuffle);
                ImGui.EndPopup();
            }
        }

        if (radio.Stations.Count == 0)
            ImGui.TextDisabled(radio.Busy ?? "No stations yet. Make one under New station.");
        ImGui.TextDisabled("Right-click a station for more.");
    }

    private void DrawStationMenu(Radio radio, Station s, Station? shuffle)
    {
        if (ImGui.MenuItem("Play"))
            plugin.Run(ct => radio.PlayStationAsync(s, ct));
        if (s.IsQuickMix)
        {
            if (ImGui.MenuItem("Choose the stations Shuffle plays…"))
            {
                shuffleChoice = [.. s.QuickMixIds];
                choosingShuffle = true;
            }

            return;
        }

        if (ImGui.MenuItem("Seeds, thumbs and modes…"))
            plugin.StationWindow.Show(s);
        if (shuffle != null)
        {
            var inShuffle = shuffle.QuickMixIds.Contains(s.Id);
            if (ImGui.MenuItem("In Shuffle", "", inShuffle))
            {
                var ids = inShuffle ? shuffle.QuickMixIds.Where(id => id != s.Id).ToList() : [.. shuffle.QuickMixIds, s.Id];
                plugin.Run(ct => radio.SetShuffleAsync(ids, ct));
            }
        }

        if (ImGui.MenuItem("Rename…"))
        {
            renaming = s;
            newName = s.Name;
        }

        if (ImGui.MenuItem("Delete…"))
            deleting = s;
    }

    private void DrawPopups(Radio radio)
    {
        if (renaming != null)
            ImGui.OpenPopup("Rename station###rename");
        if (ImGui.BeginPopupModal("Rename station###rename", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
            var enter = ImGui.InputText("##newname", ref newName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            if ((ImGui.Button("Rename") || enter) && renaming is { } r && newName.Trim().Length > 0)
            {
                var name = newName.Trim();
                plugin.Run(ct => radio.RenameStationAsync(r, name, ct));
                renaming = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                renaming = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (deleting != null)
            ImGui.OpenPopup("Delete station###delete");
        if (ImGui.BeginPopupModal("Delete station###delete", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Delete \"{deleting?.Name}\" from your Pandora account? This cannot be undone.");
            if (ImGui.Button("Delete") && deleting is { } d)
            {
                plugin.Run(ct => radio.DeleteStationAsync(d, ct));
                deleting = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Keep it"))
            {
                deleting = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (choosingShuffle)
            ImGui.OpenPopup("Shuffle stations###shuffle");
        if (ImGui.BeginPopupModal("Shuffle stations###shuffle", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Shuffle plays from these stations:");
            if (ImGui.BeginChild("##shufflelist", new Vector2(300 * ImGuiHelpers.GlobalScale, 240 * ImGuiHelpers.GlobalScale), true))
            {
                foreach (var s in radio.Stations.Where(s => !s.IsQuickMix).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var on = shuffleChoice.Contains(s.Id);
                    if (ImGui.Checkbox($"{s.Name}##q{s.Id}", ref on))
                    {
                        if (on)
                            shuffleChoice.Add(s.Id);
                        else
                            shuffleChoice.Remove(s.Id);
                    }
                }
            }

            ImGui.EndChild();
            if (ImGui.Button("Save"))
            {
                var ids = shuffleChoice.ToList();
                plugin.Run(ct => radio.SetShuffleAsync(ids, ct));
                choosingShuffle = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                choosingShuffle = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
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
            {
                var from = radio.Stations.FirstOrDefault(s => s.Id == t.StationId);
                ImGui.TextDisabled(st.IsQuickMix && from != null ? $"on Shuffle, from {from.Name}" : $"on {st.Name}");
            }

            var pos = plugin.Audio.Position;
            var len = t.Length > TimeSpan.Zero ? t.Length : plugin.Audio.Duration;
            var fraction = len > TimeSpan.Zero ? (float)Math.Clamp(pos / len, 0, 1) : 0f;
            ImGui.ProgressBar(fraction, new Vector2(260 * ImGuiHelpers.GlobalScale, 0), $"{pos:m\\:ss} / {len:m\\:ss}");
            ImGui.SameLine();
            ImGui.TextDisabled($"{t.Bitrate} kbit/s");
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
            plugin.Run(ct => radio.NextAsync(ct));
        Tip("Next song");
        ImGui.SameLine();
        SongActions(radio, t, "now");
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.QuestionCircle) && t != null)
        {
            explainedToken = t.Token;
            explanation = "Asking Pandora…";
            plugin.Run(async ct => explanation = await radio.ExplainAsync(ct).ConfigureAwait(false));
        }

        Tip("Why this song?");
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.ExternalLinkAlt) && t?.DetailUrl is { } url)
            Util.OpenLink(url);
        Tip("Open on Pandora");
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

    /// <summary>Thumbs, tired and bookmark for a song, playing or past.</summary>
    private void SongActions(Radio radio, Track? t, string id)
    {
        var past = t != null && t.Token != radio.Current?.Token;
        var loved = t?.Loved == true;
        ImGui.PushID(id);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.ThumbsUp, loved ? ImGuiColors.HealerGreen : null) && t != null)
            plugin.Run(ct => radio.LoveAsync(ct, t));
        Tip(loved ? "You love this one" : "Love it: more like this");
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.ThumbsDown, t?.Banned == true ? ImGuiColors.DalamudRed : null) && t != null)
            plugin.Run(ct => radio.BanAsync(ct, t));
        Tip(past ? "Ban it: never on its station again" : "Ban it: never on this station again (and skip)");
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Moon) && t != null)
            plugin.Run(ct => radio.TiredAsync(ct, t));
        Tip(past ? "Tired of it: not for a month" : "Tired of it: not for a month (and skip)");
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Bookmark) && t != null)
            plugin.Run(ct => radio.BookmarkAsync(false, ct, t));
        Tip("Bookmark the song (right-click: the artist)");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && t != null)
            plugin.Run(ct => radio.BookmarkAsync(true, ct, t));
        ImGui.PopID();
    }

    private void DrawHistory(Radio radio)
    {
        if (radio.History.Count == 0)
        {
            ImGui.TextDisabled("Songs you have heard this session show here, to rate or bookmark later.");
            return;
        }

        foreach (var t in radio.History)
        {
            SongActions(radio, t, "h" + t.Token);
            ImGui.SameLine();
            ImGui.TextUnformatted(t.Title);
            ImGui.SameLine();
            ImGui.TextDisabled($"— {t.Artist}");
            if (t.DetailUrl is { } url)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Pandora##l{t.Token}"))
                    Util.OpenLink(url);
            }
        }
    }

    private static void DrawUpcoming(Radio radio)
    {
        if (radio.Upcoming.Count == 0)
        {
            ImGui.TextDisabled("Pandora hands out a few songs at a time; the next ones show here.");
            return;
        }

        foreach (var t in radio.Upcoming)
        {
            ImGui.TextUnformatted(t.Title);
            ImGui.SameLine();
            ImGui.TextDisabled($"— {t.Artist}{(t.Album.Length > 0 ? " · " + t.Album : "")}");
        }
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
            if (ImGui.Selectable($"{r.Name}  ({Describe(r)})##{r.MusicToken}"))
            {
                results = [];
                plugin.Run(ct => radio.CreateAndPlayAsync(() => plugin.Pandora.CreateStationAsync(r.MusicToken, ct), ct));
            }
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Pandora's genre stations"))
        {
            loadingGenres ??= plugin.Pandora.GetGenreStationsAsync(cts.Token);
            if (loadingGenres is { IsCompleted: true } g)
                genres ??= g.IsCompletedSuccessfully ? g.Result : [];
            if (genres == null)
                ImGui.TextDisabled("Loading…");
            foreach (var category in genres ?? [])
            {
                if (!ImGui.TreeNode($"{category.Name}##g{category.Name}"))
                    continue;
                foreach (var s in category.Stations)
                {
                    if (ImGui.Selectable($"{s.Name}##gs{s.Token}"))
                        plugin.Run(ct => radio.CreateAndPlayAsync(() => plugin.Pandora.CreateStationAsync(s.Token, ct), ct));
                }

                ImGui.TreePop();
            }
        }
    }

    internal static string Describe(SearchResult r) => r.Kind switch
    {
        SearchKind.Artist => "artist",
        SearchKind.Song => $"song by {r.Artist}",
        _ => "genre",
    };

    // ---- settings ------------------------------------------------------------------------------

    private void DrawSettings()
    {
        var c = plugin.Config;
        var changed = false;
        var quality = (int)c.Quality;
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("Audio quality", ref quality, ["Best my account gets (192 kbit/s on Plus and Premium)", "128 kbit/s (less data)"]))
        {
            c.Quality = (AudioQuality)quality;
            plugin.Pandora.Quality = c.Quality;
            changed = true;
        }

        var client = (int)c.Client;
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("Pandora client", ref client, ["Automatic", "Android (as Elpis and pianobar)", "Pandora One (as Pithos; paid accounts)"]))
        {
            c.Client = (ClientChoice)client;
            changed = true;
        }

        ImGui.TextDisabled("A different client takes effect at the next sign-in.");
        if (plugin.ExplicitFilter is { } explicitFilter)
        {
            if (ImGui.Checkbox("Pandora's explicit content filter (for the whole account)", ref explicitFilter))
                plugin.SetExplicitFilter(explicitFilter);
        }

        ImGui.Separator();
        changed |= Check("List stations A to Z", c.SortStationsByName, v => c.SortStationsByName = v);
        changed |= Check("Mute the game's music while a song plays", c.MuteGameMusic, v => c.MuteGameMusic = v);
        changed |= Check("Pause during cutscenes", c.PauseInCutscenes, v => c.PauseInCutscenes = v);
        changed |= Check("Say each new song in chat", c.AnnounceInChat, v => c.AnnounceInChat = v);
        changed |= Check("A toast for each new song", c.ToastOnNewSong, v => c.ToastOnNewSong = v);
        changed |= Check("Show the song in the server info bar", c.ShowInServerInfoBar, v => c.ShowInServerInfoBar = v);
        changed |= Check("Media keys: play/pause, next, stop", c.MediaKeys, v => c.MediaKeys = v);
        changed |= Check("Sign in and resume my last station when the game starts", c.AutoStart, v => c.AutoStart = v);

        ImGui.Separator();
        ImGui.TextUnformatted("Scrobbling");
        changed |= Check("Last.fm", c.ScrobbleToLastFm, v => c.ScrobbleToLastFm = v);
        if (c.ScrobbleToLastFm)
            changed |= DrawLastFm(c);
        changed |= Check("ListenBrainz", c.ScrobbleToListenBrainz, v => c.ScrobbleToListenBrainz = v);
        if (c.ScrobbleToListenBrainz)
        {
            ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputTextWithHint("##lbtoken", Secrets.Unprotect(c.ProtectedListenBrainzToken) != null ? "token saved" : "your user token (listenbrainz.org/settings)", ref listenBrainzToken, 64, ImGuiInputTextFlags.Password)
                && listenBrainzToken.Trim().Length > 0)
            {
                c.ProtectedListenBrainzToken = Secrets.Protect(listenBrainzToken.Trim());
                changed = true;
            }
        }

        if (changed)
            plugin.SaveConfig();
    }

    private bool DrawLastFm(Configuration c)
    {
        var changed = false;
        ImGui.TextDisabled("Use your own free Last.fm API account (last.fm/api/account/create).");
        var key = c.LastFmApiKey;
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("API key", ref key, 64))
        {
            c.LastFmApiKey = key.Trim();
            changed = true;
        }

        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("Shared secret", Secrets.Unprotect(c.ProtectedLastFmSecret) != null ? "saved" : "", ref lastFmSecret, 64, ImGuiInputTextFlags.Password)
            && lastFmSecret.Trim().Length > 0)
        {
            c.ProtectedLastFmSecret = Secrets.Protect(lastFmSecret.Trim());
            changed = true;
        }

        var secret = Secrets.Unprotect(c.ProtectedLastFmSecret);
        var connected = Secrets.Unprotect(c.ProtectedLastFmSession) != null;
        ImGui.BeginDisabled(c.LastFmApiKey.Length == 0 || secret == null);
        if (lastFmToken == null && ImGui.Button(connected ? "Connect again" : "Connect Last.fm"))
        {
            var fm = new LastFm(plugin.Http, c.LastFmApiKey, secret!);
            plugin.Run(async ct =>
            {
                try
                {
                    var (token, url) = await fm.BeginAuthAsync(ct).ConfigureAwait(false);
                    lastFmToken = token;
                    lastFmMessage = "Approve XivPiano in the page that opened, then press Done.";
                    Util.OpenLink(url);
                }
                catch (Exception e) when (e is HttpRequestException or FormatException)
                {
                    lastFmMessage = e.Message;
                }
            });
        }

        if (lastFmToken is { } pending && ImGui.Button("Done"))
        {
            var fm = new LastFm(plugin.Http, c.LastFmApiKey, secret!);
            plugin.Run(async ct =>
            {
                try
                {
                    c.ProtectedLastFmSession = Secrets.Protect(await fm.FinishAuthAsync(pending, ct).ConfigureAwait(false));
                    plugin.SaveConfig();
                    lastFmMessage = "Connected.";
                }
                catch (Exception e) when (e is HttpRequestException or FormatException)
                {
                    lastFmMessage = e.Message;
                }
                finally
                {
                    lastFmToken = null;
                }
            });
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(lastFmMessage ?? (connected ? "Connected." : "Not connected."));
        return changed;
    }

    private static bool Check(string label, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value))
            return false;
        set(value);
        return true;
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
