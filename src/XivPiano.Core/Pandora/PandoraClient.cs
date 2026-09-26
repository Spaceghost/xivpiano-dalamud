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
public sealed class PandoraClient(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private Partner partner = Partner.Android;
    private PandoraCrypto crypto = new(Partner.Android);

    private string? partnerId;
    private string? partnerAuthToken;
    private string? userId;
    private string? userAuthToken;
    private long timeOffset;
    private (string Email, string Password)? credentials;

    public Account? Account { get; private set; }

    public bool LoggedIn => userAuthToken != null;

    public AudioQuality Quality { get; set; } = AudioQuality.Best;

    public ClientChoice ClientChoice { get; set; } = ClientChoice.Automatic;

    /// <summary>The client identity the session uses now.</summary>
    public Partner Partner => partner;

    /// <summary>
    /// Signs in. Automatic starts as Android (Elpis, pianobar); a paid account that is then offered no 192 kbit/s
    /// stream is moved to the Pandora One client on its first playlist (Pithos), see <see cref="GetPlaylistAsync"/>.
    /// A Pandora One login that Pandora refuses (a free account) falls back to Android.
    /// </summary>
    public async Task<Account> LoginAsync(string email, string password, CancellationToken ct)
    {
        if (ClientChoice == ClientChoice.PandoraOne)
        {
            try
            {
                return await LoginAsAsync(Partner.PandoraOne, email, password, ct).ConfigureAwait(false);
            }
            catch (PandoraException e) when (e.Code is PandoraException.ListenerNotAuthorized or PandoraException.PartnerNotAuthorized)
            {
                // Not a paid account after all: the ordinary client still works.
            }
        }

        return await LoginAsAsync(Partner.Android, email, password, ct).ConfigureAwait(false);
    }

    // ---- session ---------------------------------------------------------------------------------

    private async Task<Account> LoginAsAsync(Partner client, string email, string password, CancellationToken ct)
    {
        await loginGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            partner = client;
            crypto = new PandoraCrypto(client);
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
            Account = new Account(
                userId,
                user["isSubscriber"]?.GetValue<bool>() ?? false,
                user["hasAudioAds"]?.GetValue<bool>() ?? true,
                client == Partner.PandoraOne ? "Pandora One" : "Android");
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
            .Select(ParseStation)
            .ToList();
    }

    private static Station ParseStation(JsonObject s) =>
        new(Str(s, "stationToken"), Str(s, "stationId"), Str(s, "stationName"),
            s["isQuickMix"]?.GetValue<bool>() ?? false, s["isShared"]?.GetValue<bool>() ?? false)
        {
            QuickMixIds = (s["quickMixStationIds"] as JsonArray ?? []).Select(x => x?.GetValue<string>()).OfType<string>().ToList(),
        };

    /// <summary>A station's seeds and the thumbs you gave on it.</summary>
    public async Task<StationDetails> GetStationDetailsAsync(Station station, CancellationToken ct)
    {
        var r = await CallAsync("station.getStation", new JsonObject
        {
            ["stationToken"] = station.Token,
            ["includeExtendedAttributes"] = true,
        }, ct).ConfigureAwait(false);
        var seeds = new List<Seed>();
        var music = r["music"] as JsonObject;
        foreach (var x in (music?["songs"] as JsonArray ?? []).OfType<JsonObject>())
            seeds.Add(new Seed(Str(x, "seedId"), SearchKind.Song, Str(x, "songName"), x["artistName"]?.GetValue<string>()));
        foreach (var x in (music?["artists"] as JsonArray ?? []).OfType<JsonObject>())
            seeds.Add(new Seed(Str(x, "seedId"), SearchKind.Artist, Str(x, "artistName"), null));
        foreach (var x in (music?["genres"] as JsonArray ?? []).OfType<JsonObject>())
            seeds.Add(new Seed(Str(x, "seedId"), SearchKind.Genre, x["genreName"]?.GetValue<string>() ?? x["stationName"]?.GetValue<string>() ?? "Genre", null));
        var feedback = new List<Feedback>();
        var fb = r["feedback"] as JsonObject;
        foreach (var (key, positive) in new[] { ("thumbsUp", true), ("thumbsDown", false) })
        {
            foreach (var x in (fb?[key] as JsonArray ?? []).OfType<JsonObject>())
                feedback.Add(new Feedback(Str(x, "feedbackId"), Str(x, "songName"), x["artistName"]?.GetValue<string>() ?? "", x["isPositive"]?.GetValue<bool>() ?? positive));
        }

        return new StationDetails(station, seeds, feedback);
    }

    /// <summary>Adds variety: another song or artist (a search result's music token) as a seed.</summary>
    public async Task AddSeedAsync(Station station, string musicToken, CancellationToken ct)
    {
        await OwnAsync(station, ct).ConfigureAwait(false);
        await CallAsync("station.addMusic", new JsonObject { ["stationToken"] = station.Token, ["musicToken"] = musicToken }, ct).ConfigureAwait(false);
    }

    public Task DeleteSeedAsync(string seedId, CancellationToken ct) =>
        CallAsync("station.deleteMusic", new JsonObject { ["seedId"] = seedId }, ct);

    public Task DeleteFeedbackAsync(string feedbackId, CancellationToken ct) =>
        CallAsync("station.deleteFeedback", new JsonObject { ["feedbackId"] = feedbackId }, ct);

    /// <summary>Pandora's genre stations, by category; making one uses its token as the music token.</summary>
    public async Task<IReadOnlyList<GenreCategory>> GetGenreStationsAsync(CancellationToken ct)
    {
        var r = await CallAsync("station.getGenreStations", [], ct).ConfigureAwait(false);
        return (r["categories"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => new GenreCategory(
                c["categoryName"]?.GetValue<string>() ?? "",
                (c["stations"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(s => new Station(Str(s, "stationToken"), s["stationId"]?.GetValue<string>() ?? Str(s, "stationToken"), Str(s, "stationName"), false, false))
                    .ToList()))
            .ToList();
    }

    /// <summary>Which of your stations Shuffle (QuickMix) plays from.</summary>
    public Task SetQuickMixAsync(IEnumerable<string> stationIds, CancellationToken ct) =>
        CallAsync("user.setQuickMix", new JsonObject { ["quickMixStationIds"] = new JsonArray([.. stationIds.Select(id => (JsonNode?)id)]) }, ct);

    /// <summary>A station shared with you becomes your own copy, which your thumbs and seeds can change.</summary>
    public async Task<Station> TransformSharedStationAsync(Station station, CancellationToken ct)
    {
        var r = await CallAsync("station.transformSharedStation", new JsonObject { ["stationToken"] = station.Token }, ct).ConfigureAwait(false);
        return r["stationToken"] != null ? ParseStation(r) : station with { IsShared = false };
    }

    /// <summary>Ratings and seeds cannot change a station shared with you, so it is made yours first (as pianobar does).</summary>
    private async Task OwnAsync(Station station, CancellationToken ct)
    {
        if (station.IsShared)
            await TransformSharedStationAsync(station, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StationMode>> GetStationModesAsync(Station station, CancellationToken ct) =>
        ParseModes(await CallAsync("interactiveradio.v1.getAvailableModesSimple", new JsonObject { ["stationId"] = station.Id }, ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<StationMode>> SetStationModeAsync(Station station, int modeId, CancellationToken ct) =>
        ParseModes(await CallAsync("interactiveradio.v1.setAndGetAvailableModes", new JsonObject { ["stationId"] = station.Id, ["modeId"] = modeId }, ct).ConfigureAwait(false));

    private static List<StationMode> ParseModes(JsonObject r)
    {
        var current = r["currentModeId"]?.GetValue<int>() ?? -1;
        return (r["availableModes"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(m => new StationMode(m["modeId"]?.GetValue<int>() ?? 0, m["modeName"]?.GetValue<string>() ?? "",
                m["modeDescription"]?.GetValue<string>() ?? "", (m["modeId"]?.GetValue<int>() ?? 0) == current))
            .ToList();
    }

    /// <summary>Pandora's explicit content filter for the account.</summary>
    public async Task<bool> GetExplicitFilterAsync(CancellationToken ct) =>
        (await CallAsync("user.getSettings", [], ct).ConfigureAwait(false))["isExplicitContentFilterEnabled"]?.GetValue<bool>() ?? false;

    /// <summary>Changing a setting needs the account's email and password again, as pianobar sends them.</summary>
    public async Task SetExplicitFilterAsync(bool enabled, CancellationToken ct)
    {
        if (credentials is not { } c)
            throw new InvalidOperationException("Sign in again to change settings.");
        await CallAsync("user.changeSettings", new JsonObject
        {
            ["userInitiatedChange"] = true,
            ["currentUsername"] = c.Email,
            ["currentPassword"] = c.Password,
            ["isExplicitContentFilterEnabled"] = enabled,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The extra MP3 formats to ask for: 192 kbit/s only exists for paid accounts.</summary>
    internal string[] RequestedFormats() =>
        Account is { IsPaid: true } && Quality == AudioQuality.Best ? ["HTTP_128_MP3", "HTTP_192_MP3"] : ["HTTP_128_MP3"];

    /// <summary>
    /// The next few songs of a station. Ads (items without a song) are left out. A paid account on the Android
    /// client that is offered no 192 kbit/s stream (with Automatic and Best) moves to the Pandora One client once,
    /// whose high-quality stream is the 192 kbit/s MP3, and asks again.
    /// </summary>
    public async Task<IReadOnlyList<Track>> GetPlaylistAsync(string stationToken, CancellationToken ct)
    {
        var tracks = await FetchPlaylistAsync(stationToken, ct).ConfigureAwait(false);
        if (ClientChoice == ClientChoice.Automatic && Quality == AudioQuality.Best && Account is { IsPaid: true }
            && partner == Partner.Android && tracks.Count > 0 && tracks.All(t => t.Bitrate < 192) && credentials is { } c)
        {
            try
            {
                await LoginAsAsync(Partner.PandoraOne, c.Email, c.Password, ct).ConfigureAwait(false);
            }
            catch (PandoraException)
            {
                await LoginAsAsync(Partner.Android, c.Email, c.Password, ct).ConfigureAwait(false);
                ClientChoice = ClientChoice.Android; // Pandora One is not for this account: stop trying
                return tracks;
            }

            return await FetchPlaylistAsync(stationToken, ct).ConfigureAwait(false);
        }

        return tracks;
    }

    private async Task<IReadOnlyList<Track>> FetchPlaylistAsync(string stationToken, CancellationToken ct)
    {
        var fetched = now();
        var formats = RequestedFormats();
        var result = await CallAsync("station.getPlaylist", new JsonObject
        {
            ["stationToken"] = stationToken,
            ["includeTrackLength"] = true,
            // The clients' own streams are AAC+ (or, for Pandora One, a 192 kbit/s MP3 as high quality); these add
            // MP3s this player can decode: 128 kbit/s for every account, 192 for paid ones.
            ["additionalAudioUrl"] = string.Join(",", formats),
        }, ct).ConfigureAwait(false);
        var cap = Quality == AudioQuality.Standard ? 128 : int.MaxValue;
        return (result["items"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(i => i["songName"] != null)
            .Select(i => ParseTrack(i, fetched, formats, cap))
            .OfType<Track>()
            .ToList();
    }

    /// <summary>
    /// The song, with the best MP3 it offers up to <paramref name="capKbps"/>: the extra URLs answer the requested
    /// formats in order (a string for one, an array for several), and the audioUrlMap may hold an MP3 as well.
    /// </summary>
    internal static Track? ParseTrack(JsonObject i, DateTimeOffset fetched, IReadOnlyList<string>? requested = null, int capKbps = int.MaxValue)
    {
        requested ??= ["HTTP_128_MP3"];
        var candidates = new List<(string Url, int Bitrate)>();
        var extra = i["additionalAudioUrl"] switch
        {
            JsonValue v => [v.GetValue<string>()],
            JsonArray a => a.Select(x => x?.GetValue<string>()).ToArray(),
            _ => [],
        };
        for (var k = 0; k < extra.Length && k < requested.Count; k++)
        {
            if (!string.IsNullOrEmpty(extra[k]) && FormatBitrate(requested[k]) is { } kbps)
                candidates.Add((extra[k]!, kbps));
        }

        if (i["audioUrlMap"] is JsonObject map)
        {
            foreach (var quality in new[] { "highQuality", "mediumQuality", "lowQuality" })
            {
                if (map[quality] is JsonObject q && q["encoding"]?.GetValue<string>() == "mp3" && q["audioUrl"]?.GetValue<string>() is { Length: > 0 } u)
                    candidates.Add((u, int.TryParse(q["bitrate"]?.ToString(), out var b) ? b : 0));
            }
        }

        var pick = candidates.Where(c => c.Bitrate <= capKbps).OrderByDescending(c => c.Bitrate).FirstOrDefault();
        if (pick.Url == null)
            pick = candidates.OrderBy(c => c.Bitrate).FirstOrDefault();
        if (pick.Url == null)
            return null; // nothing this player can decode
        var (url, encoding, bitrate) = (pick.Url, "mp3", pick.Bitrate);
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

    /// <summary>HTTP_192_MP3 -> 192.</summary>
    internal static int? FormatBitrate(string format)
    {
        var parts = format.Split('_');
        return parts.Length == 3 && parts[2] == "MP3" && int.TryParse(parts[1], out var kbps) ? kbps : null;
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
            await LoginAsAsync(partner, c.Email, c.Password, ct).ConfigureAwait(false);
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
