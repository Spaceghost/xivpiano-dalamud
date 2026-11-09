using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace XivPiano.Core;

/// <summary>
/// Where the player is: the TerritoryType row, the Map row and, when known, the position in map units (the
/// in-game 1..42 coordinates). The service keeps only the centre of a few-unit cell, never the exact position.
/// </summary>
public readonly record struct XivLocation(int Territory, int Map, Vector2? Position);

/// <summary>Listens in one map cell over the last 7 days, summed over everyone (X and Y are the cell centre).</summary>
public sealed record EorzeaCell(int Zone, int Map, float X, float Y, int Listens);

public sealed record EorzeaMap(IReadOnlyList<EorzeaCell> Cells, float CellSize, IReadOnlyDictionary<int, int> ZoneListens);

/// <summary>A consenting crew member who is playing now, as the service sends them. Char is the character hash.</summary>
public sealed record PresenceMember(string Char, string Name, int? Zone, string Artist, string Title, DateTimeOffset ExpiresAt);

public static class XivPresence
{
    /// <summary>
    /// The identifier other XivPiano users compute for a character they can see: SHA-256 over a fixed label, the
    /// home world row and the name, truncated to 16 bytes (32 hex characters). Names are public in game; this is
    /// not a secret, only a way to match a listener to the character beside you without sending a name.
    /// </summary>
    public static string CharacterHash(uint homeWorld, string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"xivpiano-presence-v1|{homeWorld}|{name}"));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 16));
    }

    /// <summary>
    /// Pixel fraction (0..1) along the map texture of a map coordinate, for a map with the given size factor
    /// (100 = a field, 200 = a city or ward): the inverse of the game's c = 41/s * (pixel/2048) + 1.
    /// </summary>
    public static float MapFraction(float coordinate, int sizeFactor) => (coordinate - 1f) * (sizeFactor / 100f) / 41f;
}

public static class EorzeaJson
{
    public static EorzeaMap ParseMap(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Unreadable map.");
        var cells = new List<EorzeaCell>();
        if (root["cells"] is JsonArray array)
            foreach (var n in array.OfType<JsonObject>())
            {
                if (n["zone"] is not JsonValue z || n["map"] is not JsonValue m || n["x"] is not JsonValue x || n["y"] is not JsonValue y || n["listens"] is not JsonValue l)
                    continue;
                if (z.TryGetValue<int>(out var zone) && m.TryGetValue<int>(out var map) && x.TryGetValue<double>(out var cx) && y.TryGetValue<double>(out var cy) && l.TryGetValue<int>(out var listens) && listens > 0)
                    cells.Add(new EorzeaCell(zone, map, (float)cx, (float)cy, listens));
            }

        var zones = new Dictionary<int, int>();
        if (root["zones"] is JsonArray za)
            foreach (var n in za.OfType<JsonObject>())
                if (n["id"] is JsonValue id && n["listens"] is JsonValue c && id.TryGetValue<int>(out var zid) && c.TryGetValue<int>(out var count))
                    zones[zid] = count;
        var size = root["cell"] is JsonValue cv && cv.TryGetValue<double>(out var d) && d > 0 ? (float)d : 3f;
        return new EorzeaMap(cells, size, zones);
    }

    public static IReadOnlyList<PresenceMember> ParsePresence(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Unreadable presence.");
        var list = new List<PresenceMember>();
        if (root["members"] is not JsonArray array)
            return list;
        foreach (var n in array.OfType<JsonObject>())
        {
            var hash = n["char"]?.GetValue<string>();
            var track = n["track"] as JsonObject;
            if (hash is not { Length: 32 } || track is null || n["expires_at"] is not JsonValue e || !e.TryGetValue<long>(out var expires))
                continue;
            int? zone = n["zone"] is JsonValue zv && zv.TryGetValue<int>(out var zi) ? zi : null;
            list.Add(new PresenceMember(hash, n["name"]?.GetValue<string>() ?? "", zone,
                track["artist"]?.GetValue<string>() ?? "", track["title"]?.GetValue<string>() ?? "", DateTimeOffset.FromUnixTimeMilliseconds(expires)));
        }

        return list;
    }
}
