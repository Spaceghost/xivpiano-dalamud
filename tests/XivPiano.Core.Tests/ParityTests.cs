using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using XivPiano.Core.Pandora;

namespace XivPiano.Core.Tests;

/// <summary>The features pianobar, Pithos and Elpis have: station management, Shuffle, modes, settings.</summary>
public class StationManagementTests
{
    private static async Task<(PandoraClient Client, FakePandora Server)> SignedInAsync(Func<string, JsonObject, JsonNode> answer)
    {
        var server = new FakePandora { Answer = answer };
        var client = new PandoraClient(new HttpClient(server), () => DateTimeOffset.FromUnixTimeSeconds(1_790_223_000));
        await client.LoginAsync("me@example.com", "right", CancellationToken.None);
        return (client, server);
    }

    [Fact]
    public async Task StationDetailsListSeedsAndThumbs()
    {
        var (client, _) = await SignedInAsync((_, _) => JsonNode.Parse("""
            {"music":{"songs":[{"seedId":"s1","songName":"Answers","artistName":"Susan Calloway"}],
                      "artists":[{"seedId":"a1","artistName":"Nobuo Uematsu"}]},
             "feedback":{"thumbsUp":[{"feedbackId":"f1","songName":"Prelude","artistName":"Nobuo Uematsu","isPositive":true}],
                         "thumbsDown":[{"feedbackId":"f2","songName":"Noise","artistName":"X","isPositive":false}]}}
            """)!);
        var d = await client.GetStationDetailsAsync(new Station("st", "s", "Café", false, false), CancellationToken.None);
        Assert.Equal([("s1", SearchKind.Song), ("a1", SearchKind.Artist)], d.Seeds.Select(x => (x.SeedId, x.Kind)).ToArray());
        Assert.Equal([("f1", true), ("f2", false)], d.Feedback.Select(x => (x.FeedbackId, x.Positive)).ToArray());
    }

    [Fact]
    public async Task ASharedStationIsTakenOverBeforeASeedIsAdded()
    {
        var (client, server) = await SignedInAsync((m, _) => m == "station.transformSharedStation"
            ? new JsonObject { ["stationToken"] = "mine", ["stationId"] = "s9", ["stationName"] = "Friend's mix" }
            : new JsonObject());
        await client.AddSeedAsync(new Station("theirs", "s9", "Friend's mix", false, true), "R123", CancellationToken.None);
        Assert.Equal(["station.transformSharedStation", "station.addMusic"], server.Calls.Skip(2).Select(c => c.Method).ToArray());
        Assert.Equal("R123", server.Calls[^1].Body["musicToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task ShuffleStationsAndModesAndGenres()
    {
        var (client, server) = await SignedInAsync((m, _) => m switch
        {
            "user.getStationList" => JsonNode.Parse("""{"stations":[{"stationToken":"q","stationId":"q","stationName":"Shuffle","isQuickMix":true,"quickMixStationIds":["s1","s2"]}]}""")!,
            "interactiveradio.v1.getAvailableModesSimple" => JsonNode.Parse("""{"currentModeId":0,"availableModes":[{"modeId":0,"modeName":"My Station","modeDescription":"Plays what you like"},{"modeId":2,"modeName":"Deep Cuts","modeDescription":"Less-played"}]}""")!,
            "station.getGenreStations" => JsonNode.Parse("""{"categories":[{"categoryName":"Soundtracks","stations":[{"stationName":"Video Game","stationToken":"G1"}]}]}""")!,
            _ => new JsonObject(),
        });
        var shuffle = Assert.Single(await client.GetStationsAsync(CancellationToken.None));
        Assert.Equal(["s1", "s2"], shuffle.QuickMixIds);
        await client.SetQuickMixAsync(["s2", "s3"], CancellationToken.None);
        Assert.Equal("""["s2","s3"]""", server.Calls[^1].Body["quickMixStationIds"]!.ToJsonString());

        var modes = await client.GetStationModesAsync(new Station("st", "s1", "Café", false, false), CancellationToken.None);
        Assert.Equal(("My Station", true), (modes[0].Name, modes[0].Active));
        Assert.False(modes[1].Active);
        Assert.Equal("s1", server.Calls[^1].Body["stationId"]!.GetValue<string>());

        var genres = Assert.Single(await client.GetGenreStationsAsync(CancellationToken.None));
        Assert.Equal(("Soundtracks", "G1"), (genres.Name, genres.Stations[0].Token));
    }

    [Fact]
    public async Task TheExplicitFilterIsChangedWithTheCredentials()
    {
        var (client, server) = await SignedInAsync((m, _) => m == "user.getSettings" ? new JsonObject { ["isExplicitContentFilterEnabled"] = true } : new JsonObject());
        Assert.True(await client.GetExplicitFilterAsync(CancellationToken.None));
        await client.SetExplicitFilterAsync(false, CancellationToken.None);
        var body = server.Calls[^1].Body;
        Assert.Equal(("me@example.com", "right", false), (body["currentUsername"]!.GetValue<string>(), body["currentPassword"]!.GetValue<string>(), body["isExplicitContentFilterEnabled"]!.GetValue<bool>()));
    }
}

public class RadioParityTests
{
    private static readonly Station Cafe = new("st", "s1", "Café", false, false);

    [Fact]
    public async Task HistorySongsCanBeRatedWithoutSkipping()
    {
        var server = new FakePandora();
        var batch = 0;
        var feedback = new List<JsonObject>();
        server.Answer = (m, b) =>
        {
            if (m != "station.getPlaylist")
            {
                feedback.Add(b);
                return m == "user.getStationList" ? JsonNode.Parse("""{"stations":[{"stationToken":"st","stationId":"s1","stationName":"Café"}]}""")! : new JsonObject();
            }

            batch++;
            return new JsonObject { ["items"] = new JsonArray(
                new JsonObject { ["songName"] = $"b{batch}a", ["artistName"] = "X", ["trackToken"] = $"t{batch}a", ["stationId"] = "s1", ["additionalAudioUrl"] = "https://mp3/a" },
                new JsonObject { ["songName"] = $"b{batch}b", ["artistName"] = "X", ["trackToken"] = $"t{batch}b", ["stationId"] = "s1", ["additionalAudioUrl"] = "https://mp3/b" }) };
        };
        var client = new PandoraClient(new HttpClient(server));
        await client.LoginAsync("me@example.com", "right", CancellationToken.None);
        using var radio = new Radio(client, new FakeAudio());
        await radio.LoadStationsAsync(CancellationToken.None);
        await radio.PlayStationAsync(Cafe, CancellationToken.None);
        await radio.NextAsync(CancellationToken.None);
        var previous = radio.History.First();
        await radio.BanAsync(CancellationToken.None, previous);
        Assert.Equal("b1b", radio.Current!.Title); // banning a past song does not skip the one playing
        Assert.True(radio.History.First().Banned);
        Assert.Equal("t1a", feedback[^1]["trackToken"]!.GetValue<string>());
        Assert.Equal("st", feedback[^1]["stationToken"]!.GetValue<string>());
    }
}

public class ScrobblingTests
{
    private static Track Song(int seconds, string token = "t1") =>
        new(token, "s1", "Answers", "Susan Calloway", "FFXIV", null, "u", "mp3", 128, TimeSpan.FromSeconds(seconds), false, 0, null, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(200, 99, false)]
    [InlineData(200, 100, true)]   // half
    [InlineData(600, 240, true)]   // four minutes
    [InlineData(600, 239, false)]
    [InlineData(30, 29, false)]    // 30 s or shorter never counts
    public void TheRule(int length, int played, bool counts) =>
        Assert.Equal(counts, ScrobbleRule.Counts(TimeSpan.FromSeconds(length), TimeSpan.FromSeconds(played)));

    [Fact]
    public void OncePerPlay()
    {
        var rule = new ScrobbleRule();
        Assert.False(rule.Due(Song(200), TimeSpan.FromSeconds(50)));
        Assert.True(rule.Due(Song(200), TimeSpan.FromSeconds(120)));
        Assert.False(rule.Due(Song(200), TimeSpan.FromSeconds(150)));
        Assert.True(rule.Due(Song(200, "t2"), TimeSpan.FromSeconds(120))); // the next song counts again
    }

    [Fact]
    public void LastFmSignature()
    {
        // md5("api_keyKEYmethodauth.getTokenSECRET"); format is never signed.
        var sig = LastFm.Sign(new Dictionary<string, string> { ["method"] = "auth.getToken", ["api_key"] = "KEY", ["format"] = "json" }, "SECRET");
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("api_keyKEYmethodauth.getTokenSECRET"))), sig);
    }

    [Fact]
    public async Task LastFmScrobblesSigned()
    {
        var sent = new List<string>();
        var handler = new Handler(async r =>
        {
            sent.Add(r.Method == HttpMethod.Get ? r.RequestUri!.Query : await r.Content!.ReadAsStringAsync());
            return r.RequestUri!.Query.Contains("auth.getSession") ? """{"session":{"key":"SK"}}""" : "{}";
        });
        var fm = new LastFm(new HttpClient(handler), "KEY", "SECRET");
        Assert.Equal("SK", await fm.FinishAuthAsync("TOK", CancellationToken.None));
        await fm.ScrobbleAsync(Song(200), DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), CancellationToken.None);
        Assert.Contains("method=track.scrobble", sent[^1]);
        Assert.Contains("timestamp=1790000000", sent[^1]);
        Assert.Contains("sk=SK", sent[^1]);
        Assert.Contains("api_sig=", sent[^1]);
    }

    [Fact]
    public void ListenBrainzBody()
    {
        var b = ListenBrainz.Body("single", Song(200), DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));
        Assert.Equal("single", b["listen_type"]!.GetValue<string>());
        var listen = b["payload"]![0]!;
        Assert.Equal(1_790_000_000, listen["listened_at"]!.GetValue<long>());
        Assert.Equal(("Susan Calloway", "Answers", "FFXIV"), (listen["track_metadata"]!["artist_name"]!.GetValue<string>(),
            listen["track_metadata"]!["track_name"]!.GetValue<string>(), listen["track_metadata"]!["release_name"]!.GetValue<string>()));
        Assert.Null(ListenBrainz.Body("playing_now", Song(200), null)["payload"]![0]!["listened_at"]);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await answer(request), Encoding.UTF8, "application/json") };
    }
}
