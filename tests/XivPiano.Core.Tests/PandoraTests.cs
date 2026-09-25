using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using XivPiano.Core.Pandora;

namespace XivPiano.Core.Tests;

public class BlowfishTests
{
    // Eric Young's published Blowfish ECB vectors (from Schneier's test set).
    [Theory]
    [InlineData("0000000000000000", "0000000000000000", "4EF997456198DD78")]
    [InlineData("FFFFFFFFFFFFFFFF", "FFFFFFFFFFFFFFFF", "51866FD5B85ECB8A")]
    [InlineData("3000000000000000", "1000000000000001", "7D856F9A613063F2")]
    [InlineData("1111111111111111", "1111111111111111", "2466DD878B963C9D")]
    [InlineData("0123456789ABCDEF", "1111111111111111", "61F9C3802281B096")]
    [InlineData("FEDCBA9876543210", "0123456789ABCDEF", "0ACEAB0FC6A0A28D")]
    public void MatchesPublishedVectors(string key, string plain, string cipher)
    {
        var bf = new Blowfish(Convert.FromHexString(key));
        var data = Convert.FromHexString(plain);
        bf.EncryptBlocks(data);
        Assert.Equal(cipher, Convert.ToHexString(data));
        bf.DecryptBlocks(data);
        Assert.Equal(plain, Convert.ToHexString(data));
    }

    // Eric Young's variable-key-length set: the first N bytes of F0E1D2C3B4A5968778695A4B..., plaintext
    // FEDCBA9876543210. Cross-checked against an independent implementation (Pithos' pure-Python Blowfish).
    [Theory]
    [InlineData("F0", "F9AD597C49DB005E")]
    [InlineData("F0E1", "E91D21C1D961A6D6")]
    [InlineData("F0E1D2C3B4A59687", "E87A244E2CC85E82")]
    [InlineData("F0E1D2C3B4A5968778", "15750E7A4F4EC577")]
    [InlineData("F0E1D2C3B4A5968778695A4B3C2D1E0F0011223344556677", "05044B62FA52D080")]
    public void VariableLengthKeys(string key, string cipher)
    {
        var bf = new Blowfish(Convert.FromHexString(key));
        var data = Convert.FromHexString("FEDCBA9876543210");
        bf.EncryptBlocks(data);
        Assert.Equal(cipher, Convert.ToHexString(data));
    }

    [Fact]
    public void RejectsPartialBlocksAndBadKeys()
    {
        Assert.Throws<ArgumentException>(() => new Blowfish([1]).EncryptBlocks(new byte[7]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Blowfish([]));
    }
}

public class PandoraCryptoTests
{
    private static readonly PandoraCrypto Crypto = new(Partner.Android);

    [Fact]
    public void EncryptsToLowercaseHexOfZeroPaddedBlocks()
    {
        var hex = Crypto.Encrypt("{\"a\":1}"); // 7 bytes: one block
        Assert.Equal(16, hex.Length);
        Assert.Equal(hex.ToLowerInvariant(), hex);
        Assert.Equal(32, Crypto.Encrypt("{\"abc\":12}").Length); // 10 bytes: two blocks
    }

    [Fact]
    public void RoundTripsWithTheOtherKeyPair()
    {
        // Requests use the encrypt key and Pandora's answers the decrypt key; a partner built with the keys
        // swapped reads what we send, as Pandora does.
        var mirror = new PandoraCrypto(Partner.Android with { EncryptKey = Partner.Android.DecryptKey, DecryptKey = Partner.Android.EncryptKey });
        var json = "{\"username\":\"a@b.c\",\"syncTime\":1790000000}";
        var plain = Encoding.UTF8.GetString(mirror.Decrypt(Crypto.Encrypt(json))).TrimEnd('\0');
        Assert.Equal(json, plain);
    }

    [Fact]
    public void ReadsTheSyncTime()
    {
        var server = new PandoraCrypto(Partner.Android with { EncryptKey = Partner.Android.DecryptKey });
        var hex = server.Encrypt("abcd1790223231\u0002\u0002");
        Assert.Equal(1790223231, Crypto.DecryptSyncTime(hex));
        Assert.Throws<FormatException>(() => Crypto.DecryptSyncTime(server.Encrypt("xxxxnotanumber")));
    }
}

/// <summary>A fake Pandora: decrypts what the client sends and answers by method.</summary>
internal sealed class FakePandora : HttpMessageHandler
{
    private readonly PandoraCrypto serverSide = new(Partner.Android with { EncryptKey = Partner.Android.DecryptKey, DecryptKey = Partner.Android.EncryptKey });

    public List<(string Method, Dictionary<string, string> Query, JsonObject Body)> Calls { get; } = [];

    public Func<string, JsonObject, JsonNode>? Answer { get; set; }

    public long ServerTime { get; set; } = 1_790_223_231;

    public int ExpireTokenOnce { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("https", request.RequestUri!.Scheme);
        var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        var method = query["method"];
        var raw = await request.Content!.ReadAsStringAsync(ct);
        var json = method == "auth.partnerLogin" ? raw : Encoding.UTF8.GetString(serverSide.Decrypt(raw)).TrimEnd('\0');
        var body = (JsonObject)JsonNode.Parse(json)!;
        Calls.Add((method, query, body));

        if (method != "auth.partnerLogin" && method != "auth.userLogin" && ExpireTokenOnce > 0)
        {
            ExpireTokenOnce--;
            return Json(new JsonObject { ["stat"] = "fail", ["code"] = 1001, ["message"] = "INVALID_AUTH_TOKEN" });
        }

        JsonNode? result = method switch
        {
            "auth.partnerLogin" => new JsonObject
            {
                ["partnerId"] = "42",
                ["partnerAuthToken"] = "partner+token/=",
                ["syncTime"] = serverSide.Encrypt($"abcd{ServerTime}"),
            },
            "auth.userLogin" => body["password"]?.GetValue<string>() == "right"
                ? new JsonObject { ["userId"] = "u1", ["userAuthToken"] = "user+token", ["isSubscriber"] = false }
                : null,
            _ => Answer?.Invoke(method, body) ?? new JsonObject(),
        };
        return result == null
            ? Json(new JsonObject { ["stat"] = "fail", ["code"] = 1002, ["message"] = "INVALID_LOGIN" })
            : Json(new JsonObject { ["stat"] = "ok", ["result"] = result });
    }

    private static HttpResponseMessage Json(JsonObject o) =>
        new(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };
}

public class PandoraClientTests
{
    private static (PandoraClient Client, FakePandora Server) Make(DateTimeOffset? now = null)
    {
        var server = new FakePandora();
        var clock = now ?? DateTimeOffset.FromUnixTimeSeconds(1_790_223_000); // 231 s behind the server
        return (new PandoraClient(new HttpClient(server), Partner.Android, () => clock), server);
    }

    [Fact]
    public async Task LogsInAsPartnerThenUser()
    {
        var (client, server) = Make();
        var account = await client.LoginAsync("me@example.com", "right", CancellationToken.None);
        Assert.Equal("u1", account.UserId);
        Assert.True(client.LoggedIn);

        var (m1, q1, b1) = server.Calls[0];
        Assert.Equal("auth.partnerLogin", m1);
        Assert.Equal("android-generic", b1["deviceModel"]!.GetValue<string>());
        Assert.False(q1.ContainsKey("partner_id"));

        var (m2, q2, b2) = server.Calls[1];
        Assert.Equal("auth.userLogin", m2);
        Assert.Equal("42", q2["partner_id"]);
        Assert.Equal("partner+token/=", q2["auth_token"]); // escaped in the URL, intact after unescaping
        Assert.Equal("me@example.com", b2["username"]!.GetValue<string>());
        Assert.Equal(1_790_223_231, b2["syncTime"]!.GetValue<long>()); // the server's clock, not ours
    }

    [Fact]
    public async Task WrongPasswordIsAPandoraError()
    {
        var (client, _) = Make();
        var e = await Assert.ThrowsAsync<PandoraException>(() => client.LoginAsync("me@example.com", "wrong", CancellationToken.None));
        Assert.Equal(PandoraException.InvalidLogin, e.Code);
        Assert.Contains("email and password", e.Message);
        Assert.False(client.LoggedIn);
    }

    [Fact]
    public async Task PlaylistPrefersTheMp3AndSkipsAds()
    {
        var (client, server) = Make();
        server.Answer = (method, body) =>
        {
            Assert.Equal("station.getPlaylist", method);
            Assert.Equal("HTTP_128_MP3", body["additionalAudioUrl"]!.GetValue<string>());
            Assert.Equal("user+token", body["userAuthToken"]!.GetValue<string>());
            return JsonNode.Parse("""
                {"items":[
                  {"adToken":"ad1"},
                  {"songName":"Answers","artistName":"Susan Calloway","albumName":"FFXIV","albumArtUrl":"https://art/1.jpg",
                   "trackToken":"t1","stationId":"s1","trackLength":312,"songRating":1,"trackGain":"-2.5",
                   "audioUrlMap":{"highQuality":{"encoding":"aacplus","bitrate":"64","audioUrl":"https://aac/1"}},
                   "additionalAudioUrl":"https://mp3/1"},
                  {"songName":"Old","artistName":"A","trackToken":"t2","stationId":"s1","trackLength":10,
                   "audioUrlMap":{"highQuality":{"encoding":"aacplus","bitrate":"64","audioUrl":"https://aac/2"},
                                  "lowQuality":{"encoding":"mp3","bitrate":"64","audioUrl":"https://mp3/2"}}},
                  {"songName":"AacOnly","artistName":"B","trackToken":"t3","stationId":"s1",
                   "audioUrlMap":{"highQuality":{"encoding":"aacplus","bitrate":"64","audioUrl":"https://aac/3"}}}
                ]}
                """)!;
        };
        await client.LoginAsync("me@example.com", "right", CancellationToken.None);
        var tracks = await client.GetPlaylistAsync("st", CancellationToken.None);
        Assert.Equal(2, tracks.Count); // the ad and the AAC-only song are left out
        var t = tracks[0];
        Assert.Equal(("Answers", "Susan Calloway", "https://mp3/1", 128, true, -2.5), (t.Title, t.Artist, t.AudioUrl, t.Bitrate, t.Loved, t.GainDb));
        Assert.Equal(TimeSpan.FromSeconds(312), t.Length);
        Assert.Equal("https://mp3/2", tracks[1].AudioUrl); // from the map when no extra URL came back
    }

    [Fact]
    public async Task AnExpiredSessionIsRenewedOnce()
    {
        var (client, server) = Make();
        server.Answer = (_, _) => new JsonObject { ["stations"] = new JsonArray(new JsonObject
        {
            ["stationToken"] = "st", ["stationId"] = "s1", ["stationName"] = "Eorzean Café", ["isQuickMix"] = false,
        }) };
        await client.LoginAsync("me@example.com", "right", CancellationToken.None);
        server.ExpireTokenOnce = 1;
        var stations = await client.GetStationsAsync(CancellationToken.None);
        Assert.Equal("Eorzean Café", Assert.Single(stations).Name);
        Assert.Equal(["auth.partnerLogin", "auth.userLogin", "user.getStationList", "auth.partnerLogin", "auth.userLogin", "user.getStationList"],
            server.Calls.Select(c => c.Method).ToArray());
    }

    [Fact]
    public async Task SearchMergesKindsByScore()
    {
        var (client, server) = Make();
        server.Answer = (_, _) => JsonNode.Parse("""
            {"artists":[{"artistName":"Nobuo Uematsu","musicToken":"R1","score":100}],
             "songs":[{"songName":"Prelude","artistName":"Nobuo Uematsu","musicToken":"S1","score":90}],
             "genreStations":[{"stationName":"Video Game Music","musicToken":"G1","score":95}]}
            """)!;
        await client.LoginAsync("me@example.com", "right", CancellationToken.None);
        var found = await client.SearchAsync("uematsu", CancellationToken.None);
        Assert.Equal(["R1", "G1", "S1"], found.Select(f => f.MusicToken).ToArray());
        Assert.Equal(SearchKind.Song, found[2].Kind);
    }

    [Fact]
    public async Task ExplainsInWords()
    {
        var (client, server) = Make();
        server.Answer = (_, _) => JsonNode.Parse("""
            {"explanations":[{"focusTraitName":"orchestral arrangements"},{"focusTraitName":"a vocal-centric aesthetic"},
                             {"focusTraitName":"many other similarities identified in the Music Genome Project"}]}
            """)!;
        await client.LoginAsync("me@example.com", "right", CancellationToken.None);
        Assert.Equal("Features orchestral arrangements and a vocal-centric aesthetic.", await client.ExplainAsync("t1", CancellationToken.None));
    }

    [Fact]
    public async Task CallsNeedALogin() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Make().Client.GetStationsAsync(CancellationToken.None));

    /// <summary>The real partner login: proves the keys and the encryption against Pandora itself.</summary>
    [Fact]
    public async Task LivePartnerLogin()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("XIVPIANO_LIVE") == "1", "set XIVPIANO_LIVE=1 to call Pandora");
        using var http = new HttpClient();
        var crypto = new PandoraCrypto(Partner.Android);
        var body = new JsonObject
        {
            ["username"] = Partner.Android.Username, ["password"] = Partner.Android.Password,
            ["deviceModel"] = Partner.Android.DeviceModel, ["version"] = "5",
        }.ToJsonString();
        using var response = await http.PostAsync("https://tuner.pandora.com/services/json/?method=auth.partnerLogin",
            new StringContent(body, Encoding.UTF8, "text/plain"));
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("ok", answer["stat"]!.GetValue<string>());
        var serverTime = crypto.DecryptSyncTime(answer["result"]!["syncTime"]!.GetValue<string>());
        Assert.InRange(serverTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 86400, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400);
    }
}
