using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Config;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using XivPiano.Core;
using XivPiano.Core.Pandora;

namespace XivPiano.Plugin;

/// <summary>
/// XivPiano: a Pandora radio player inside FFXIV, after pianobar, Pithos and Elpis. The Pandora protocol and the
/// queue live in XivPiano.Core; this wires them to sound, a window, chat commands, the server info bar and IPC.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string Command = "/piano";

    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly ICondition condition;
    private readonly IGameConfig gameConfig;
    private readonly IFramework framework;
    private readonly IDtrBar dtrBar;
    private readonly WindowSystem windows = new("XivPiano");
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly CancellationTokenSource cts = new();
    private readonly MainWindow window;
    private readonly ICallGateProvider<string> nowPlayingGate;
    private readonly ICallGateProvider<string, bool> controlGate;
    private IDtrBarEntry? dtr;

    private bool? musicWasMuted; // the game's own BGM setting before we muted it; null = we did not
    private bool pausedForCutscene;
    private string? announcedToken;

    public Plugin(IDalamudPluginInterface pi, IPluginLog log, ICommandManager commands, IChatGui chat, ICondition condition,
        IGameConfig gameConfig, IFramework framework, IDtrBar dtrBar, ITextureProvider textures)
    {
        this.pi = pi;
        this.log = log;
        this.commands = commands;
        this.chat = chat;
        this.condition = condition;
        this.gameConfig = gameConfig;
        this.framework = framework;
        this.dtrBar = dtrBar;

        Config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Clamp();
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"XivPiano/{typeof(Plugin).Assembly.GetName().Version?.ToString(3)}");
        Pandora = new PandoraClient(http, Partner.Android);
        Audio = new AudioOut(http) { Volume = Config.Volume };

        window = new MainWindow(this, textures, http);
        windows.AddWindow(window);
        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenMainUi += ToggleWindow;
        pi.UiBuilder.OpenConfigUi += ToggleWindow;
        commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open XivPiano. /piano play|pause|next|love|ban|tired|stop, /piano station <name>",
        });
        framework.Update += OnUpdate;

        nowPlayingGate = pi.GetIpcProvider<string>("XivPiano.NowPlaying");
        nowPlayingGate.RegisterFunc(NowPlayingJson);
        controlGate = pi.GetIpcProvider<string, bool>("XivPiano.Control");
        controlGate.RegisterFunc(action => Control(action));

        if (Config.AutoStart && Config.Email.Length > 0 && Secrets.Unprotect(Config.ProtectedPassword) is { } saved)
            SignIn(Config.Email, saved, resume: true);
    }

    public Configuration Config { get; }

    public PandoraClient Pandora { get; }

    public AudioOut Audio { get; }

    public Radio? Radio { get; private set; }

    public bool SigningIn { get; private set; }

    public string? SignInError { get; private set; }

    public void SaveConfig() => pi.SavePluginConfig(Config);

    private void ToggleWindow() => window.Toggle();

    /// <summary>Runs a radio action off the draw thread; its errors land in Radio.Error.</summary>
    public void Run(Func<CancellationToken, Task> action) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await action(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                log.Error(e, "XivPiano action failed");
            }
        });

    public void SignIn(string email, string password, bool resume = false)
    {
        SigningIn = true;
        SignInError = null;
        Run(async ct =>
        {
            try
            {
                var account = await Pandora.LoginAsync(email, password, ct).ConfigureAwait(false);
                Config.Email = email;
                Config.ProtectedPassword = Config.RememberPassword ? Secrets.Protect(password) : "";
                SaveConfig();
                Radio?.Dispose();
                Radio = new Radio(Pandora, Audio);
                Radio.Changed += OnRadioChanged;
                await Radio.LoadStationsAsync(ct).ConfigureAwait(false);
                log.Information("Signed in to Pandora ({Kind}); {Count} stations", account.IsSubscriber ? "subscriber" : "free", Radio.Stations.Count);
                if (resume && Radio.Stations.FirstOrDefault(s => s.Id == Config.LastStationId) is { } last)
                    await Radio.PlayStationAsync(last, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is PandoraException or HttpRequestException or TaskCanceledException or FormatException)
            {
                SignInError = e is TaskCanceledException ? "Pandora did not answer in time." : e.Message;
            }
            finally
            {
                SigningIn = false;
            }
        });
    }

    private void OnRadioChanged()
    {
        if (Radio?.Station is { } s && s.Id != Config.LastStationId)
        {
            Config.LastStationId = s.Id;
            SaveConfig();
        }
    }

    // ---- every frame: server info bar, cutscenes, game music, announcements -----------------------

    private void OnUpdate(IFramework _)
    {
        var radio = Radio;
        var track = radio?.Current;

        if (Config.PauseInCutscenes && radio != null)
        {
            var cutscene = condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78] || condition[ConditionFlag.OccupiedInCutSceneEvent];
            if (cutscene && radio.Playing)
            {
                radio.TogglePause();
                pausedForCutscene = true;
            }
            else if (!cutscene && pausedForCutscene)
            {
                pausedForCutscene = false;
                if (!radio.Playing)
                    radio.TogglePause();
            }
        }

        // The game's music is muted only while a song actually plays, and put back as it was.
        var playing = radio?.Playing == true;
        if (Config.MuteGameMusic && playing && musicWasMuted == null && gameConfig.TryGet(SystemConfigOption.IsSndBgm, out bool muted))
        {
            musicWasMuted = muted;
            if (!muted)
                gameConfig.Set(SystemConfigOption.IsSndBgm, true);
        }
        else if ((!playing || !Config.MuteGameMusic) && musicWasMuted is { } was)
        {
            gameConfig.Set(SystemConfigOption.IsSndBgm, was);
            musicWasMuted = null;
        }

        if (track != null && track.Token != announcedToken)
        {
            announcedToken = track.Token;
            if (Config.AnnounceInChat)
                chat.Print(new SeStringBuilder().AddUiForeground($"♪ {track.Title}", 45).AddText($" by {track.Artist}").Build(), "XivPiano");
        }

        UpdateDtr(track, playing);
    }

    private void UpdateDtr(Track? track, bool playing)
    {
        if (!Config.ShowInServerInfoBar || track == null)
        {
            if (dtr != null)
                dtr.Shown = false;
            return;
        }

        if (dtr == null)
        {
            dtr = dtrBar.Get("XivPiano");
            dtr.OnClick = e =>
            {
                if (e.ClickType == MouseClickType.Right)
                    Radio?.TogglePause();
                else
                    ToggleWindow();
            };
        }

        dtr.Shown = true;
        var text = $"{(playing ? "♪" : "‖")} {Shorten(track.Title, 28)}";
        if (dtr.Text?.TextValue != text)
        {
            dtr.Text = text;
            dtr.Tooltip = $"{track.Title} by {track.Artist}\nClick: XivPiano. Right-click: {(playing ? "pause" : "play")}.";
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---- commands and IPC ---------------------------------------------------------------------------

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            ToggleWindow();
            return;
        }

        if (parts[0].Equals("station", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
        {
            var match = Radio?.Stations.FirstOrDefault(s => s.Name.Contains(parts[1], StringComparison.OrdinalIgnoreCase));
            if (match == null || Radio == null)
                chat.PrintError($"No station matches \"{parts[1]}\".", "XivPiano");
            else
                Run(ct => Radio.PlayStationAsync(match, ct));
            return;
        }

        if (!Control(parts[0]))
            chat.PrintError($"Unknown or unavailable: {parts[0]}. Try /piano play|pause|next|love|ban|tired|stop|station <name>.", "XivPiano");
    }

    /// <summary>play, pause, toggle, next, love, ban, tired, stop. False when unknown or not signed in.</summary>
    public bool Control(string action)
    {
        if (Radio is not { } radio)
            return false;
        switch (action.ToLowerInvariant())
        {
            case "play" when !radio.Playing && radio.Current != null:
            case "pause" when radio.Playing:
            case "toggle":
                radio.TogglePause();
                return true;
            case "play" when radio.Current == null && radio.Station is { } s:
                Run(ct => radio.PlayStationAsync(s, ct));
                return true;
            case "play" or "pause":
                return true; // already so
            case "next" or "skip":
                Run(radio.NextAsync);
                return true;
            case "love" or "like":
                Run(radio.LoveAsync);
                return true;
            case "ban":
                Run(radio.BanAsync);
                return true;
            case "tired":
                Run(radio.TiredAsync);
                return true;
            case "stop":
                radio.Stop();
                return true;
            default:
                return false;
        }
    }

    private string NowPlayingJson()
    {
        var t = Radio?.Current;
        return JsonSerializer.Serialize(new
        {
            playing = Radio?.Playing == true,
            station = Radio?.Station?.Name,
            title = t?.Title,
            artist = t?.Artist,
            album = t?.Album,
            loved = t?.Loved,
            positionSeconds = (int)Audio.Position.TotalSeconds,
            lengthSeconds = (int)(t?.Length.TotalSeconds ?? 0),
        });
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        nowPlayingGate.UnregisterFunc();
        controlGate.UnregisterFunc();
        commands.RemoveHandler(Command);
        pi.UiBuilder.Draw -= windows.Draw;
        pi.UiBuilder.OpenMainUi -= ToggleWindow;
        pi.UiBuilder.OpenConfigUi -= ToggleWindow;
        cts.Cancel();
        if (musicWasMuted is { } was)
            gameConfig.Set(SystemConfigOption.IsSndBgm, was);
        dtr?.Remove();
        Radio?.Dispose();
        Audio.Dispose();
        windows.RemoveAllWindows();
        window.Dispose();
        http.Dispose();
        cts.Dispose();
    }
}
