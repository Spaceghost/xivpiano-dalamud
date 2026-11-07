using System.Collections.Concurrent;
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
    private XivPiano.Core.Radio? owner;
    private CancellationTokenSource? viewCancellation;
    private Task<StationDetails>? loading;
    private StationDetails? details;
    private Task<IReadOnlyList<StationMode>>? loadingModes;
    private IReadOnlyList<StationMode>? modes;
    private string search = "";
    private Task<IReadOnlyList<SearchResult>>? searching;
    private IReadOnlyList<SearchResult> results = [];
    private string? error, operationError;
    private readonly ConcurrentQueue<(int Generation, Action Apply)> completed = new();
    private int generation;
    private volatile bool disposed;
    private void Post(int expected, Action apply)
    {
        if (!disposed) completed.Enqueue((expected, apply));
    }

    public void Show(Station s)
    {
        if (disposed) return;
        ResetView();
        owner = plugin.Radio;
        if (owner is null) return;
        viewCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        station = s;
        WindowName = $"{s.Name}###XivPianoStation";
        Reload();
        IsOpen = true;
        Size = new Vector2(460, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void ResetView()
    {
        generation++;
        viewCancellation?.Cancel();
        viewCancellation?.Dispose();
        viewCancellation = null;
        station = null; owner = null;
        loading = null; loadingModes = null; details = null; modes = null;
        search = ""; searching = null; results = []; error = operationError = null;
        completed.Clear();
        IsOpen = false;
    }

    private void Reload()
    {
        if (station is not { } s || owner != plugin.Radio || viewCancellation is null)
            return;
        details = null;
        modes = null;
        error = null;
        loading = plugin.Pandora.GetStationDetailsAsync(s, viewCancellation.Token);
        loadingModes = plugin.Pandora.GetStationModesAsync(s, viewCancellation.Token);
    }

    public override void Draw()
    {
        if (disposed) return;
        if (owner != plugin.Radio) { ResetView(); return; }
        while (completed.TryDequeue(out var change))
            if (change.Generation == generation) change.Apply();
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
        if (operationError != null) ImGui.TextColored(ImGuiColors.DalamudOrange, operationError);
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
                searching = plugin.Pandora.SearchAsync(search.Trim(), viewCancellation!.Token);
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
                    operationError = null;
                    var expected = generation;
                    plugin.Run(async ct =>
                    {
                        var own = await radio.AddSeedAsync(s, r.MusicToken, ct).ConfigureAwait(false);
                        var failure = radio.Error;
                        Post(expected, () =>
                        {
                            if (plugin.Radio != radio) return;
                            var current = own ?? radio.ResolveStation(s);
                            if (current != null)
                            {
                                station = current;
                                WindowName = $"{current.Name}###XivPianoStation";
                                Reload();
                            }
                            operationError = own == null ? failure ?? "The seed could not be added." : null;
                        });
                    });
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
                    var expected = generation;
                    plugin.Run(async ct =>
                    {
                        var updated = await plugin.Pandora.SetStationModeAsync(s, mode.Id, ct).ConfigureAwait(false);
                        Post(expected, () => { if (plugin.Radio == radio) modes = updated; });
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
    private void Change(Func<CancellationToken, Task> action)
    {
        operationError = null;
        var expected = generation;
        var currentRadio = plugin.Radio;
        plugin.Run(async ct =>
        {
            try
            {
                await action(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is PandoraException or HttpRequestException)
            {
                var failure = e.Message;
                Post(expected, () => { if (plugin.Radio == currentRadio) operationError = failure; });
                return;
            }

            Post(expected, () => { if (plugin.Radio == currentRadio) Reload(); });
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ResetView();
        cts.Cancel();
        cts.Dispose();
    }
}
