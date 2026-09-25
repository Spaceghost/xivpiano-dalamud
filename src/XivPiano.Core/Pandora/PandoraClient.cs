using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace XivPiano.Core.Pandora;

/// <summary>
/// Pandora's JSON API (tuner.pandora.com/services/json), as documented at 6xq.net/pandora-apidoc and used by
/// pianobar, Pithos, Elpis and pydora. Every call is HTTPS. After the partner login, bodies are Blowfish-wrapped
/// (see <see cref="PandoraCrypto"/>) and carry the server's clock and the user's token.
/// </summary>
public sealed class PandoraClient(HttpClient http, Partner partner, Func<DateTimeOffset>? clock = null)
{
    private readonly PandoraCrypto crypto = new(partner);
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim loginGate = new(1, 1);

    private string? partnerId;
    private string? partnerAuthToken;
    private string? userId;
    private string? userAuthToken;
    private long timeOffset;
    private (string Email, string Password)? credentials;

    public Account? Account { get; private set; }

    public bool LoggedIn => userAuthToken != null;

    // ---- session ---------------------------------------------------------------------------------

    public async Task<Account> LoginAsync(string email, string password, CancellationToken ct)
    {
        await loginGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            partnerId = partnerAuthToken = userId = userAuthToken = null;
            Account = null;
            var partnerResult = await PostAsync("auth.partnerLogin", new JsonObject
            {
                ["username"] = partner.Username,
                ["password"] = partner.Password,
                ["deviceModel"] = partner.DeviceModel,
                ["version"] = "5",
                ["includeUrls"] = true,
            }, encrypt: false, ct).ConfigureAwait(false);
            partnerId = Str(partnerResult, "partnerId");
            partnerAuthToken = Str(partnerResult, "partnerAuthToken");
            var serverTime = crypto.DecryptSyncTime(Str(partnerResult, "syncTime"));
            timeOffset = serverTime - now().ToUnixTimeSeconds();

            var user = await PostAsync("auth.userLogin", new JsonObject
            {
                ["loginType"] = "user",
                ["username"] = email,
                ["password"] = password,
                ["partnerAuthToken"] = partnerAuthToken,
                ["syncTime"] = SyncTime(),
                ["returnIsSubscriber"] = true,
            }, encrypt: true, ct).ConfigureAwait(false);
            userId = Str(user, "userId");
            userAuthToken = Str(user, "userAuthToken");
            credentials = (email, password);
            Account = new Account(userId, user["isSubscriber"]?.GetValue<bool>() ?? false);
            return Account;
        }
        finally
        {
            loginGate.Release();
        }
    }

    public void Logout()
    {
        partnerId = partnerAuthToken = userId = userAuthToken = null;
        credentials = null;
        Account = null;
    }

    private long SyncTime() => now().ToUnixTimeSeconds() + timeOffset;

    // ---- calls -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Station>> GetStationsAsync(CancellationToken ct)
    {
        var result = await CallAsync("user.getStationList", new JsonObject { ["includeStationArtUrl"] = false }, ct).ConfigureAwait(false);
        return (result["stations"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(s => new Station(
                Str(s, "stationToken"),
                Str(s, "stationId"),
                Str(s, "stationName"),
                s["isQuickMix"]?.GetValue<bool>() ?? false,
                s["isShared"]?.GetValue<bool>() ?? false))
            .ToList();
    }

    /// <summary>The next few songs of a station. Ads (items without a song) are left out.</summary>
    public async Task<IReadOnlyList<Track>> GetPlaylistAsync(string stationToken, CancellationToken ct)
    {
        var fetched = now();
        var result = await CallAsync("station.getPlaylist", new JsonObject
        {
            ["stationToken"] = stationToken,
            ["includeTrackLength"] = true,
            // The Android partner's own streams are AAC+; this adds a 128 kbit/s MP3 for every account.
            ["additionalAudioUrl"] = "HTTP_128_MP3",
        }, ct).ConfigureAwait(false);
        return (result["items"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(i => i["songName"] != null)
            .Select(i => ParseTrack(i, fetched))
            .OfType<Track>()
            .ToList();
    }

    internal static Track? ParseTrack(JsonObject i, DateTimeOffset fetched)
    {
        // additionalAudioUrl is a string for one requested format and an array for several.
        var mp3 = i["additionalAudioUrl"] switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonArray a => a.LastOrDefault()?.GetValue<string>(),
            _ => null,
        };
        string url, encoding;
        int bitrate;
        if (!string.IsNullOrEmpty(mp3))
        {
            (url, encoding, bitrate) = (mp3, "mp3", 128);
        }
        else if (BestMp3(i["audioUrlMap"] as JsonObject) is { } fromMap)
        {
            (url, encoding, bitrate) = fromMap;
        }
        else
        {
            return null; // nothing this player can decode
        }

        _ = double.TryParse(i["trackGain"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var gain);
        return new Track(
            Str(i, "trackToken"),
            Str(i, "stationId"),
            Str(i, "songName"),
            Str(i, "artistName"),
            i["albumName"]?.GetValue<string>() ?? "",
            i["albumArtUrl"]?.GetValue<string>() is { Length: > 0 } art ? art : null,
            url,
            encoding,
            bitrate,
            TimeSpan.FromSeconds(i["trackLength"]?.GetValue<int>() ?? 0),
            (i["songRating"]?.GetValue<int>() ?? 0) == 1,
            gain,
            i["songDetailUrl"]?.GetValue<string>(),
            fetched);
    }

    private static (string, string, int)? BestMp3(JsonObject? map)
    {
        if (map == null)
            return null;
        foreach (var quality in new[] { "highQuality", "mediumQuality", "lowQuality" })
        {
            if (map[quality] is JsonObject q && q["encoding"]?.GetValue<string>() == "mp3" && q["audioUrl"]?.GetValue<string>() is { } u)
                return (u, "mp3", int.TryParse(q["bitrate"]?.ToString(), out var b) ? b : 0);
        }

        return null;
    }

    public Task AddFeedbackAsync(string stationToken, string trackToken, bool positive, CancellationToken ct) =>
        CallAsync("station.addFeedback", new JsonObject
        {
            ["stationToken"] = stationToken,
            ["trackToken"] = trackToken,
            ["isPositive"] = positive,
        }, ct);

    /// <summary>"I'm tired of this song": not played for a month, on any station.</summary>
    public Task SleepSongAsync(string trackToken, CancellationToken ct) =>
        CallAsync("user.sleepSong", new JsonObject { ["trackToken"] = trackToken }, ct);

    public Task BookmarkSongAsync(string trackToken, CancellationToken ct) =>
        CallAsync("bookmark.addSongBookmark", new JsonObject { ["trackToken"] = trackToken }, ct);

    public Task BookmarkArtistAsync(string trackToken, CancellationToken ct) =>
        CallAsync("bookmark.addArtistBookmark", new JsonObject { ["trackToken"] = trackToken }, ct);

    /// <summary>Why Pandora played this: its Music Genome traits, as a sentence.</summary>
    public async Task<string> ExplainAsync(string trackToken, CancellationToken ct)
    {
        var result = await CallAsync("track.explainTrack", new JsonObject { ["trackToken"] = trackToken }, ct).ConfigureAwait(false);
        var traits = (result["explanations"] as JsonArray ?? [])
            .Select(e => e?["focusTraitName"]?.GetValue<string>())
            .OfType<string>()
            .Where(t => !t.StartsWith("many other", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return traits.Count == 0 ? "Pandora did not say." : "Features " + JoinWords(traits) + ".";
    }

    private static string JoinWords(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string text, CancellationToken ct)
    {
        var result = await CallAsync("music.search", new JsonObject
        {
            ["searchText"] = text,
            ["includeNearMatches"] = true,
            ["includeGenreStations"] = true,
        }, ct).ConfigureAwait(false);
        var found = new List<SearchResult>();
        foreach (var a in (result["artists"] as JsonArray ?? []).OfType<JsonObject>())
            found.Add(new SearchResult(SearchKind.Artist, Str(a, "musicToken"), Str(a, "artistName"), null, a["score"]?.GetValue<int>() ?? 0));
        foreach (var s in (result["songs"] as JsonArray ?? []).OfType<JsonObject>())
            found.Add(new SearchResult(SearchKind.Song, Str(s, "musicToken"), Str(s, "songName"), s["artistName"]?.GetValue<string>(), s["score"]?.GetValue<int>() ?? 0));
        foreach (var g in (result["genreStations"] as JsonArray ?? []).OfType<JsonObject>())
            found.Add(new SearchResult(SearchKind.Genre, Str(g, "musicToken"), Str(g, "stationName"), null, g["score"]?.GetValue<int>() ?? 0));
        return found.OrderByDescending(r => r.Score).ToList();
    }

    public async Task<Station> CreateStationAsync(string musicToken, CancellationToken ct)
    {
        var s = await CallAsync("station.createStation", new JsonObject { ["musicToken"] = musicToken }, ct).ConfigureAwait(false);
        return new Station(Str(s, "stationToken"), Str(s, "stationId"), Str(s, "stationName"), false, false);
    }

    /// <summary>A new station from the song (or its artist) that is playing.</summary>
    public async Task<Station> CreateStationFromTrackAsync(string trackToken, bool fromArtist, CancellationToken ct)
    {
        var s = await CallAsync("station.createStation", new JsonObject
        {
            ["trackToken"] = trackToken,
            ["musicType"] = fromArtist ? "artist" : "song",
        }, ct).ConfigureAwait(false);
        return new Station(Str(s, "stationToken"), Str(s, "stationId"), Str(s, "stationName"), false, false);
    }

    public Task DeleteStationAsync(string stationToken, CancellationToken ct) =>
        CallAsync("station.deleteStation", new JsonObject { ["stationToken"] = stationToken }, ct);

    public Task RenameStationAsync(string stationToken, string name, CancellationToken ct) =>
        CallAsync("station.renameStation", new JsonObject { ["stationToken"] = stationToken, ["stationName"] = name }, ct);

    // ---- transport -------------------------------------------------------------------------------

    /// <summary>A user call; an expired session is renewed once with the credentials of the last login.</summary>
    private async Task<JsonObject> CallAsync(string method, JsonObject body, CancellationToken ct)
    {
        if (!LoggedIn)
            throw new InvalidOperationException("Not logged in to Pandora.");
        try
        {
            return await PostAsync(method, WithSession(body), encrypt: true, ct).ConfigureAwait(false);
        }
        catch (PandoraException e) when (e.Code == PandoraException.InvalidAuthToken && credentials is { } c)
        {
            await LoginAsync(c.Email, c.Password, ct).ConfigureAwait(false);
            return await PostAsync(method, WithSession(body), encrypt: true, ct).ConfigureAwait(false);
        }
    }

    private JsonObject WithSession(JsonObject body)
    {
        var copy = (JsonObject)body.DeepClone();
        copy["userAuthToken"] = userAuthToken;
        copy["syncTime"] = SyncTime();
        return copy;
    }

    private async Task<JsonObject> PostAsync(string method, JsonObject body, bool encrypt, CancellationToken ct)
    {
        var query = new StringBuilder("https://").Append(partner.Host).Append("/services/json/?method=").Append(method);
        if (partnerId != null)
            query.Append("&partner_id=").Append(Uri.EscapeDataString(partnerId));
        if (userId != null)
            query.Append("&user_id=").Append(Uri.EscapeDataString(userId));
        if ((userAuthToken ?? partnerAuthToken) is { } token)
            query.Append("&auth_token=").Append(Uri.EscapeDataString(token));

        var json = body.ToJsonString();
        using var content = new StringContent(encrypt ? crypto.Encrypt(json) : json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var response = await http.PostAsync(new Uri(query.ToString()), content, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject answer;
        try
        {
            answer = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("not an object");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
        {
            throw new HttpRequestException($"Pandora answered {(int)response.StatusCode} with something that is not JSON.", e);
        }

        if (answer["stat"]?.GetValue<string>() == "ok")
            return answer["result"] as JsonObject ?? [];
        var code = answer["code"]?.GetValue<int>() ?? -1;
        var message = answer["message"]?.GetValue<string>() ?? "no message";
        if (code == PandoraException.InvalidAuthToken)
            userAuthToken = null;
        throw new PandoraException(code, message);
    }

    private static string Str(JsonObject o, string key) =>
        o[key]?.GetValue<string>() ?? throw new FormatException($"Pandora's answer has no {key}.");
}
