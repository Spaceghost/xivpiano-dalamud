using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using XivPiano.Core.Pandora;

namespace XivPiano.Plugin;

/// <summary>
/// One station's make-up, as pianobar's station info and Pithos' station properties show it: its seeds (add
/// variety, remove), the thumbs you gave on it (take one back) and Pandora's station modes.
/// </summary>
public sealed class StationWindow(Plugin plugin) : Window("Station###XivPianoStation"), IDisposable
{
    private readonly CancellationTokenSource cts = new();
    private Station? station;
    private Task<StationDetails>? loading;
    private StationDetails? details;
    private Task<IReadOnlyList<StationMode>>? loadingModes;
    private IReadOnlyList<StationMode>? modes;
    private string search = "";
    private Task<IReadOnlyList<SearchResult>>? searching;
    private IReadOnlyList<SearchResult> results = [];
    private string? error;

    public void Show(Station s)
    {
        station = s;
        WindowName = $"{s.Name}###XivPianoStation";
        Reload();
        IsOpen = true;
        Size = new Vector2(460, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private void Reload()
    {
        if (station is not { } s)
            return;
        details = null;
        modes = null;
        error = null;
        loading = plugin.Pandora.GetStationDetailsAsync(s, cts.Token);
        loadingModes = plugin.Pandora.GetStationModesAsync(s, cts.Token);
    }

    public override void Draw()
    {
        if (station is not { } s || plugin.Radio is not { } radio)
        {
            ImGui.TextDisabled("Sign in first.");
            return;
        }

        if (loading is { IsCompleted: true } l)
        {
            details = l.IsCompletedSuccessfully ? l.Result : null;
            error = l.Exception?.InnerException?.Message;
            loading = null;
        }

        if (loadingModes is { IsCompleted: true } m)
        {
            modes = m.IsCompletedSuccessfully ? m.Result : [];
            loadingModes = null;
        }

        if (error != null)
            ImGui.TextColored(ImGuiColors.DalamudOrange, error);
        if (s.IsShared)
            ImGui.TextDisabled("Shared with you: it becomes your own copy the first time you change it.");

        if (ImGui.CollapsingHeader("Seeds", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (details == null)
                ImGui.TextDisabled("Loading…");
            foreach (var seed in details?.Seeds ?? [])
            {
                if (ImGui.SmallButton($"Remove##{seed.SeedId}"))
                    Change(ct => plugin.Pandora.DeleteSeedAsync(seed.SeedId, ct));
                ImGui.SameLine();
                ImGui.TextUnformatted(seed.Artist is { } a ? $"{seed.Name} — {a}" : seed.Name);
                ImGui.SameLine();
                ImGui.TextDisabled(seed.Kind.ToString().ToLowerInvariant());
            }

            ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
            var go = ImGui.InputTextWithHint("##variety", "Add variety: an artist or a song", ref search, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Search") || go) && search.Trim().Length > 0)
                searching = plugin.Pandora.SearchAsync(search.Trim(), cts.Token);
            if (searching is { IsCompleted: true } done)
            {
                results = done.IsCompletedSuccessfully ? done.Result.Where(r => r.Kind != SearchKind.Genre).ToList() : [];
                searching = null;
            }

            foreach (var r in results.Take(8))
            {
                if (ImGui.Selectable($"Add {r.Name} ({MainWindow.Describe(r)})##{r.MusicToken}"))
                {
                    results = [];
                    Change(ct => plugin.Pandora.AddSeedAsync(s, r.MusicToken, ct));
                }
            }
        }

        if (ImGui.CollapsingHeader("Thumbs"))
        {
            foreach (var f in details?.Feedback ?? [])
            {
                if (ImGui.SmallButton($"Take back##{f.FeedbackId}"))
                    Change(ct => plugin.Pandora.DeleteFeedbackAsync(f.FeedbackId, ct));
                ImGui.SameLine();
                ImGui.TextColored(f.Positive ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed, f.Positive ? "up" : "down");
                ImGui.SameLine();
                ImGui.TextUnformatted($"{f.Title} — {f.Artist}");
            }

            if (details?.Feedback.Count == 0)
                ImGui.TextDisabled("No thumbs on this station yet.");
        }

        if (ImGui.CollapsingHeader("Modes"))
        {
            if (modes == null)
                ImGui.TextDisabled("Loading…");
            else if (modes.Count == 0)
                ImGui.TextDisabled("Pandora offers no modes for this station on this account.");
            foreach (var mode in modes ?? [])
            {
                if (ImGui.RadioButton($"{mode.Name}##m{mode.Id}", mode.Active) && !mode.Active)
                {
                    plugin.Run(async ct =>
                    {
                        modes = await plugin.Pandora.SetStationModeAsync(s, mode.Id, ct).ConfigureAwait(false);
                    });
                }

                if (mode.Description.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(mode.Description);
                }
            }
        }

        _ = radio;
    }

    /// <summary>A change to the station, then the station read again so the window shows what Pandora has.</summary>
    private void Change(Func<CancellationToken, Task> action) =>
        plugin.Run(async ct =>
        {
            try
            {
                await action(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is PandoraException or HttpRequestException)
            {
                error = e.Message;
                return;
            }

            Reload();
        });

    public void Dispose()
    {
        cts.Cancel();
        cts.Dispose();
    }
}
