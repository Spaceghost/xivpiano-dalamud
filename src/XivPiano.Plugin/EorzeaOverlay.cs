using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivPiano.Core;

namespace XivPiano.Plugin;

/// <summary>
/// Cosmetic overlays drawn with ImGui over the game: listening activity on the in-game map, and a small note
/// over nearby characters whose players switched on in-world presence. It reads game state and draws; it never
/// sends input or touches packets. Data comes from Spaceghost Scrobbles (see docs/VIEWS.md in that service).
/// </summary>
internal sealed class EorzeaOverlay : IDisposable
{
    private static readonly TimeSpan MapEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PresenceEvery = TimeSpan.FromSeconds(20);

    private readonly Plugin plugin;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, int> sizeFactors = [];
    private readonly Dictionary<ulong, (string Name, uint World, string Hash)> hashes = [];
    private readonly CancellationTokenSource cts = new();

    private EorzeaMap? map;
    private IReadOnlyList<PresenceMember> presence = [];
    private DateTimeOffset mapAt = DateTimeOffset.MinValue;
    private DateTimeOffset presenceAt = DateTimeOffset.MinValue;
    private Task? mapTask;
    private Task? presenceTask;
    private Task? consentTask;
    private string? pushedHash;
    private bool pushedOn;

    public EorzeaOverlay(Plugin plugin, IGameGui gameGui, IClientState clientState, IObjectTable objects, IDataManager data, IPluginLog log)
    {
        this.plugin = plugin;
        this.gameGui = gameGui;
        this.clientState = clientState;
        this.objects = objects;
        this.data = data;
        this.log = log;
    }

    /// <summary>A line for the settings window: what the overlay last learned or why it is idle.</summary>
    public string Status { get; private set; } = "";

    public string PresenceStatus { get; private set; } = "";

    public bool MapAreaFound { get; private set; }

    public int CellsOnThisMap { get; private set; }

    public void Dispose() => cts.Cancel();

    /// <summary>The player's zone, map and map position now. Framework thread only.</summary>
    public XivLocation? CurrentLocation()
    {
        try
        {
            if (!plugin.Config.SendLocation || !clientState.IsLoggedIn || clientState.TerritoryType == 0)
                return null;
            Vector2? position = null;
            if (objects.LocalPlayer is { } me)
            {
                var at = Dalamud.Utility.MapUtil.GetMapCoordinates(me);
                position = new Vector2(at.X, at.Y);
            }
            return new XivLocation((int)clientState.TerritoryType, (int)clientState.MapId, position);
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not read the player's location");
            return null;
        }
    }

    public void Draw()
    {
        var c = plugin.Config;
        if (!c.ScrobbleToSpaceghost || !clientState.IsLoggedIn)
            return;
        var now = DateTimeOffset.UtcNow;
        if (c.ShowMapOverlay && now - mapAt > MapEvery && mapTask is not { IsCompleted: false })
        {
            mapAt = now;
            mapTask = Fetch(async sg => map = await sg.GetEorzeaMapAsync(cts.Token).ConfigureAwait(false),
                () => Status = "Map updated " + DateTime.Now.ToString("HH:mm"));
        }

        SyncConsent(c);
        if (c.SharePresence && c.ShowWorldIndicators && now - presenceAt > PresenceEvery && presenceTask is not { IsCompleted: false })
        {
            presenceAt = now;
            presenceTask = Fetch(async sg => presence = await sg.GetPresenceAsync(cts.Token).ConfigureAwait(false),
                () => PresenceStatus = $"{presence.Count} listening now");
        }

        if (!c.SharePresence && presence.Count > 0)
            presence = [];
        try
        {
            if (c.ShowMapOverlay && map is not null)
                DrawMap(map, c);
            if (c.SharePresence && c.ShowWorldIndicators && presence.Count > 0)
                DrawPeople(c);
        }
        catch (Exception e)
        {
            log.Warning(e, "Eorzea overlay draw failed; turning the overlays off for this session");
            c.ShowMapOverlay = false;
            c.ShowWorldIndicators = false;
            Status = "Overlays stopped after an error; see the Dalamud log.";
        }
    }

    private Task Fetch(Func<SpaceghostScrobbles, Task> call, Action ok)
    {
        var sg = plugin.Spaceghost();
        if (sg is null)
            return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                await call(sg).ConfigureAwait(false);
                ok();
            }
            catch (Exception e) when (e is HttpRequestException or FormatException or TaskCanceledException or System.Text.Json.JsonException)
            {
                log.Warning("Eorzea overlay: {Message}", e.Message);
                var message = e is HttpRequestException ? e.Message : "Unreadable answer from the service.";
                Status = message;
                PresenceStatus = message;
            }
        }, cts.Token);
    }

    /// <summary>Keeps the service's in-world presence switch and character hash in step with the setting and the character.</summary>
    private void SyncConsent(Configuration c)
    {
        if (consentTask is { IsCompleted: false })
            return;
        string? hash = null;
        if (c.SharePresence && objects.LocalPlayer is { } me)
            hash = XivPresence.CharacterHash(me.HomeWorld.RowId, me.Name.TextValue);
        if (c.SharePresence && hash is null)
            return;
        if (c.SharePresence == pushedOn && (!c.SharePresence || hash == pushedHash))
            return;
        var (on, sent) = (c.SharePresence, hash);
        var sg = plugin.Spaceghost();
        if (sg is null)
            return;
        consentTask = Task.Run(async () =>
        {
            try
            {
                await sg.SetPresenceAsync(on, sent, cts.Token).ConfigureAwait(false);
                (pushedOn, pushedHash) = (on, sent);
                PresenceStatus = on ? "In-world presence is on." : "In-world presence is off.";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                PresenceStatus = e.Message;
                pushedOn = on;
                pushedHash = sent; // do not retry every frame; a changed setting or character tries again
            }
        }, cts.Token);
    }

    // ---- the in-game map ----------------------------------------------------------------------

    private int SizeFactor(uint mapId)
    {
        if (sizeFactors.TryGetValue(mapId, out var f))
            return f;
        f = data.GetExcelSheet<Lumina.Excel.Sheets.Map>()?.TryGetRow(mapId, out var row) == true ? row.SizeFactor : 100;
        return sizeFactors[mapId] = f > 0 ? f : 100;
    }

    private unsafe void DrawMap(EorzeaMap m, Configuration c)
    {
        MapAreaFound = false;
        var addon = gameGui.GetAddonByName("AreaMap");
        if (addon.IsNull || !addon.IsVisible || AgentMap.Instance() is null)
            return;
        var shown = AgentMap.Instance()->SelectedMapId;
        var canvas = FindMapCanvas((AtkUnitBase*)addon.Address);
        if (canvas is null)
            return;
        MapAreaFound = true;
        var (origin, size) = (canvas.Value.Min + new Vector2(c.MapNudgeX, c.MapNudgeY), canvas.Value.Max - canvas.Value.Min);
        size *= c.MapScale;
        var draw = ImGui.GetBackgroundDrawList();
        if (c.ShowMapArea)
            draw.AddRect(origin, origin + size, 0xFF00FFFF, 0, ImDrawFlags.None, 2);
        var sf = SizeFactor(shown);
        var cells = m.Cells.Where(x => x.Map == shown).ToList();
        CellsOnThisMap = cells.Count;
        if (cells.Count == 0)
            return;
        var max = cells.Max(x => x.Listens);
        var half = m.CellSize / 2f;
        var hovered = new List<EorzeaCell>();
        var mouse = ImGui.GetMousePos();
        foreach (var cell in cells)
        {
            var a = origin + new Vector2(XivPresence.MapFraction(cell.X - half, sf), XivPresence.MapFraction(cell.Y - half, sf)) * size;
            var b = origin + new Vector2(XivPresence.MapFraction(cell.X + half, sf), XivPresence.MapFraction(cell.Y + half, sf)) * size;
            var heat = MathF.Sqrt(cell.Listens / (float)max);
            draw.AddRectFilled(a, b, ImGui.GetColorU32(new Vector4(1f, 0.55f - 0.35f * heat, 0.15f, 0.20f + 0.45f * heat)), 3);
            if (mouse.X >= a.X && mouse.X < b.X && mouse.Y >= a.Y && mouse.Y < b.Y)
                hovered.Add(cell);
        }

        if (hovered.Count > 0)
            ImGui.SetTooltip(string.Join("\n", hovered.Select(x => $"{x.Listens} listen{(x.Listens == 1 ? "" : "s")} here this week")));
    }

    /// <summary>
    /// The screen rectangle of the map picture inside the AreaMap window: the largest visible, roughly square node
    /// among the window's own and its component children. This is a heuristic (the game has no API for it), so the
    /// settings window can draw the detected rectangle and nudge it.
    /// </summary>
    private static unsafe (Vector2 Min, Vector2 Max)? FindMapCanvas(AtkUnitBase* addon)
    {
        if (addon is null || addon->RootNode is null)
            return null;
        AtkResNode* best = null;
        var bestArea = 0f;
        var stack = new Stack<(nint Node, int Depth)>();
        stack.Push(((nint)addon->RootNode->ChildNode, 0));
        while (stack.Count > 0)
        {
            var (ptr, depth) = stack.Pop();
            for (var node = (AtkResNode*)ptr; node is not null; node = node->PrevSiblingNode)
            {
                var visible = (node->NodeFlags & NodeFlags.Visible) != 0;
                var (w, h) = (node->Width * node->ScaleX * addon->Scale, node->Height * node->ScaleY * addon->Scale);
                if (visible && w >= 200 && h >= 200 && MathF.Abs(w - h) <= 0.05f * MathF.Max(w, h) && w * h > bestArea)
                {
                    best = node;
                    bestArea = w * h;
                }

                if (visible && depth < 3 && node->ChildNode is not null)
                    stack.Push(((nint)node->ChildNode, depth + 1));
            }
        }

        if (best is null)
            return null;
        var min = new Vector2(best->ScreenX, best->ScreenY);
        return (min, min + new Vector2(best->Width * best->ScaleX, best->Height * best->ScaleY) * addon->Scale);
    }

    // ---- people around you --------------------------------------------------------------------

    private void DrawPeople(Configuration c)
    {
        var now = DateTimeOffset.UtcNow;
        var byHash = presence.Where(p => p.ExpiresAt > now).ToDictionary(p => p.Char, p => p);
        if (byHash.Count == 0)
            return;
        var self = objects.LocalPlayer?.GameObjectId;
        var draw = ImGui.GetBackgroundDrawList();
        var shown = 0;
        foreach (var obj in objects.PlayerObjects)
        {
            if (obj is not IPlayerCharacter pc || pc.GameObjectId == self || shown >= 40)
                continue;
            var (name, world) = (pc.Name.TextValue, pc.HomeWorld.RowId);
            if (!hashes.TryGetValue(pc.GameObjectId, out var known) || known.Name != name || known.World != world)
                hashes[pc.GameObjectId] = known = (name, world, XivPresence.CharacterHash(world, name));
            if (!byHash.TryGetValue(known.Hash, out var listener) || !gameGui.WorldToScreen(pc.Position + new Vector3(0, 2.4f, 0), out var screen))
                continue;
            var text = $"♪ {listener.Title} – {listener.Artist}";
            var size = ImGui.CalcTextSize(text);
            var min = screen - new Vector2(size.X / 2 + 5, size.Y + 6);
            draw.AddRectFilled(min, min + size + new Vector2(10, 4), 0xB0201810, 6);
            draw.AddRect(min, min + size + new Vector2(10, 4), 0xFF4A9CF0, 6);
            draw.AddText(min + new Vector2(5, 2), 0xFFFFFFFF, text);
            shown++;
        }

        if (hashes.Count > 512)
            hashes.Clear();
    }
}
