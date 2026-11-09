using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using XivPiano.Core.Pandora;

namespace XivPiano.Core.Tests;

public class EorzeaPresenceTests
{
    private static Track Song() => new("t1", "s1", "Prelude", "Uematsu", "FFXIV", null, "u", "mp3", 128, TimeSpan.FromSeconds(200), false, 0, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void ListenCarriesZoneMapAndPositionOnlyWhenGiven()
    {
        var plain = ListenBrainz.Body("playing_now", Song(), null)["payload"]![0]!["track_metadata"]!["additional_info"]!;
        Assert.Null(plain["xiv_territory"]);
        var info = ListenBrainz.Body("single", Song(), DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), new XivLocation(148, 4, new Vector2(20.37f, 11.94f)))["payload"]![0]!["track_metadata"]!["additional_info"]!;
        Assert.Equal(148, info["xiv_territory"]!.GetValue<int>());
        Assert.Equal(4, info["xiv_map"]!.GetValue<int>());
        Assert.Equal(20.4, info["xiv_x"]!.GetValue<double>(), 3);
        var zoneOnly = ListenBrainz.Body("single", Song(), DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), new XivLocation(148, 4, null))["payload"]![0]!["track_metadata"]!["additional_info"]!;
        Assert.Null(zoneOnly["xiv_x"]);
    }

    [Fact]
    public void CharacterHashIsStableAndSeparatesWorldsAndNames()
    {
        var a = XivPresence.CharacterHash(21, "Ann Example");
        Assert.Equal(32, a.Length);
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.Equal(a, XivPresence.CharacterHash(21, "Ann Example"));
        Assert.NotEqual(a, XivPresence.CharacterHash(22, "Ann Example"));
        Assert.NotEqual(a, XivPresence.CharacterHash(21, "Bob Example"));
        // The Worker accepts exactly this shape (src/accounts.js: 32 lowercase hex).
        Assert.DoesNotContain("Ann", a);
    }

    [Theory]
    [InlineData(1f, 100, 0f)]
    [InlineData(42f, 100, 1f)]
    [InlineData(21.5f, 100, 0.5f)]
    [InlineData(1f, 200, 0f)]
    [InlineData(11.25f, 200, 0.5f)]
    public void MapFractionInvertsTheGameFormula(float coordinate, int sizeFactor, float expected) =>
        Assert.Equal(expected, XivPresence.MapFraction(coordinate, sizeFactor), 3);

    [Fact]
    public void ParsesTheAggregateAndSkipsMalformedCells()
    {
        var map = EorzeaJson.ParseMap("""{"cell":3,"zones":[{"id":148,"listens":5}],"cells":[{"zone":148,"map":4,"x":19.5,"y":10.5,"listens":2},{"zone":1,"map":"x"},{"zone":148,"map":4,"x":1,"y":1,"listens":0}]}""");
        Assert.Equal(3f, map.CellSize);
        var cell = Assert.Single(map.Cells);
        Assert.Equal(new EorzeaCell(148, 4, 19.5f, 10.5f, 2), cell);
        Assert.Equal(5, map.ZoneListens[148]);
    }

    [Fact]
    public void ParsesPresenceAndDropsEntriesWithoutAValidCharacterHash()
    {
        var hash = new string('a', 32);
        var list = EorzeaJson.ParsePresence($$"""{"members":[{"char":"{{hash}}","name":"ann","zone":148,"track":{"artist":"Boards","title":"Dayvan"},"expires_at":1790000000000},{"char":"short","track":{},"expires_at":1},{"char":"{{hash}}"}]}""");
        var m = Assert.Single(list);
        Assert.Equal((hash, "ann", 148, "Boards", "Dayvan"), (m.Char, m.Name, m.Zone, m.Artist, m.Title));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790000000000), m.ExpiresAt);
    }

    [Fact]
    public async Task ClientCallsTheDocumentedEndpointsWithTheToken()
    {
        var seen = new List<(HttpMethod, string, string?, string?)>();
        using var http = new HttpClient(new Recorder(async r =>
        {
            seen.Add((r.Method, r.RequestUri!.AbsolutePath, r.Headers.Authorization?.ToString(), r.Content is null ? null : await r.Content.ReadAsStringAsync()));
            return r.RequestUri.AbsolutePath.EndsWith("presence") ? """{"members":[]}""" : r.RequestUri.AbsolutePath.EndsWith("eorzea") ? """{"cells":[]}""" : "{}";
        }));
        var sg = new SpaceghostScrobbles(http, "sgs_token");
        await sg.GetEorzeaMapAsync(CancellationToken.None);
        await sg.GetPresenceAsync(CancellationToken.None);
        await sg.SetPresenceAsync(true, new string('b', 32), CancellationToken.None);
        await sg.SetPresenceAsync(false, null, CancellationToken.None);
        Assert.Equal("/scrobble/api/views/ffxiv/eorzea", seen[0].Item2);
        Assert.Equal((HttpMethod.Get, "/scrobble/1/xiv/presence", "Token sgs_token"), (seen[1].Item1, seen[1].Item2, seen[1].Item3));
        Assert.Equal("/scrobble/1/user/settings", seen[2].Item2);
        Assert.True(JsonNode.Parse(seen[2].Item4!)!["xiv_presence"]!.GetValue<bool>());
        Assert.Equal(new string('b', 32), JsonNode.Parse(seen[2].Item4!)!["xiv_char"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(seen[3].Item4!)!["xiv_char"]);
    }

    [Fact]
    public async Task ForbiddenPresenceIsAnExplainedError()
    {
        using var http = new HttpClient(new Recorder(_ => Task.FromResult("{}"), HttpStatusCode.Forbidden));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => new SpaceghostScrobbles(http, "t").GetPresenceAsync(CancellationToken.None));
        Assert.Contains("crew", e.Message);
    }

    private sealed class Recorder(Func<HttpRequestMessage, Task<string>> answer, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(status) { Content = new StringContent(await answer(request), Encoding.UTF8, "application/json") };
    }
}
