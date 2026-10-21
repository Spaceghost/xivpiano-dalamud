using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace XivPiano.Core.Pandora;

/// <summary>
/// Pandora account and playback policy over melodica's libpiano protocol engine.
/// Native code constructs and validates protocol transactions; HttpClient owns HTTPS
/// and the managed models retain application metadata outside the historical ABI.
/// </summary>
public sealed class PandoraClient(HttpClient http, Func<DateTimeOffset>? clock = null) : IDisposable
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private readonly SemaphoreSlim protocolGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sessionSync = new();
    private MelodicaProtocol? protocol;
    private CancellationTokenSource? activeRequest;
    private long partnerServerTime;
    private long sessionEpoch;
    private long signoutEpoch;
    private long loginIntent;
    private int resetProtocol;
    private int disposed;
    private Partner partner = Partner.Android;

    private string? userAuthToken;
    private long timeOffset;
    private (string Email, string Password)? credentials;

    public Account? Account { get; private set; }

    public bool LoggedIn { get { lock (sessionSync) return userAuthToken != null; } }

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
        (long Signout, long Login) intent;
        CancellationTokenSource? superseded;
        lock (sessionSync)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            intent = (signoutEpoch, ++loginIntent);
            superseded = activeRequest;
        }
        CancelRequest(superseded);
        if (ClientChoice == ClientChoice.PandoraOne)
        {
            try
            {
                return await LoginAsAsync(Partner.PandoraOne, email, password, ct, intent).ConfigureAwait(false);
            }
            catch (PandoraException e) when (e.Code is PandoraException.ListenerNotAuthorized or PandoraException.PartnerNotAuthorized)
            {
                // Not a paid account after all: the ordinary client still works.
            }
        }

        return await LoginAsAsync(Partner.Android, email, password, ct, intent).ConfigureAwait(false);
    }

    // ---- session ---------------------------------------------------------------------------------

    private async Task<Account> LoginAsAsync(Partner client, string email, string password, CancellationToken ct,
        (long Signout, long Login) intent)
    {
        lock (sessionSync)
        {
            EnsureIntent(intent, ct);
        }
        await loginGate.WaitAsync(ct).ConfigureAwait(false);
        long attempt = -1;
        try
        {
            CancellationTokenSource? superseded;
            lock (sessionSync)
            {
                EnsureIntent(intent, ct);
                attempt = ++sessionEpoch;
                partner = client;
                userAuthToken = null;
                credentials = null;
                Account = null;
                superseded = activeRequest;
            }
            CancelRequest(superseded);
            var partnerResult = await PostAsync("auth.partnerLogin", new JsonObject
            {
                ["username"] = partner.Username,
                ["password"] = partner.Password,
                ["deviceModel"] = partner.DeviceModel,
                ["version"] = "5",
                ["includeUrls"] = true,
            }, ct, attempt).ConfigureAwait(false);
            var partnerToken = Str(partnerResult, "partnerAuthToken");
            var serverTime = partnerServerTime;
            var offset = serverTime - now().ToUnixTimeSeconds();

            var user = await PostAsync("auth.userLogin", new JsonObject
            {
                ["loginType"] = "user",
                ["username"] = email,
                ["password"] = password,
                ["partnerAuthToken"] = partnerToken,
                ["syncTime"] = now().ToUnixTimeSeconds() + offset,
                ["returnIsSubscriber"] = true,
            }, ct, attempt).ConfigureAwait(false);
            var id = Str(user, "userId");
            var token = Str(user, "userAuthToken");
            // Validate richer account metadata before committing any credentials.
            var account = new Account(
                id,
                Boolean(user, "isSubscriber", false),
                Boolean(user, "hasAudioAds", true),
                client == Partner.PandoraOne ? "Pandora One" : "Android");
            lock (sessionSync)
            {
                EnsureSession(attempt, ct);
                EnsureIntent(intent, ct);
                timeOffset = offset;
                userAuthToken = token;
                credentials = (email, password);
                Account = account;
            }
            return account;
        }
        catch
        {
            lock (sessionSync)
            {
                if (sessionEpoch == attempt)
                {
                    userAuthToken = null;
                    credentials = null;
                    Account = null;
                    Interlocked.Exchange(ref resetProtocol, 1);
                }
            }
            DisposeIdleProtocol();
            throw;
        }
        finally
        {
            loginGate.Release();
        }
    }

    public void Logout()
    {
        (long Signout, long Login) intent;
        CancellationTokenSource? superseded;
        lock (sessionSync)
        {
            ++sessionEpoch;
            ++signoutEpoch;
            ++loginIntent;
            intent = (signoutEpoch, loginIntent);
            userAuthToken = null;
            credentials = null;
            Account = null;
            Interlocked.Exchange(ref resetProtocol, 1);
            superseded = activeRequest;
        }
        CancelRequest(superseded);
        _ = DrainSignedOutProtocolAsync(intent);
    }

    private void EnsureSession(long expectedEpoch, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ct.ThrowIfCancellationRequested();
        if (sessionEpoch != expectedEpoch)
            throw new OperationCanceledException("The Pandora session changed during the request.", ct);
    }

    private void EnsureIntent((long Signout, long Login) intent, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ct.ThrowIfCancellationRequested();
        if (intent != (signoutEpoch, loginIntent))
            throw new OperationCanceledException("The Pandora sign-in was superseded or signed out.", ct);
    }

    private static void CancelRequest(CancellationTokenSource? request)
    {
        try { request?.Cancel(); }
        catch (ObjectDisposedException) { } // The transaction completed after the snapshot.
    }

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
        station = await OwnAsync(station, ct).ConfigureAwait(false);
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
    private async Task<Station> OwnAsync(Station station, CancellationToken ct)
    {
        return station.IsShared ? await TransformSharedStationAsync(station, ct).ConfigureAwait(false) : station;
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
        (string Email, string Password) saved;
        long epoch;
        lock (sessionSync)
        {
            if (credentials is not { } c)
                throw new InvalidOperationException("Sign in again to change settings.");
            saved = c;
            epoch = sessionEpoch;
        }
        await CallAsync("user.changeSettings", new JsonObject
        {
            ["userInitiatedChange"] = true,
            ["currentUsername"] = saved.Email,
            ["currentPassword"] = saved.Password,
            ["isExplicitContentFilterEnabled"] = enabled,
        }, ct, epoch).ConfigureAwait(false);
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
        (long Signout, long Login) intent;
        lock (sessionSync) intent = (signoutEpoch, loginIntent);
        var tracks = await FetchPlaylistAsync(stationToken, ct, intent).ConfigureAwait(false);
        Account? account;
        Partner identity;
        (string Email, string Password)? saved;
        lock (sessionSync)
        {
            EnsureIntent(intent, ct);
            account = Account;
            identity = partner;
            saved = credentials;
        }
        if (ClientChoice == ClientChoice.Automatic && Quality == AudioQuality.Best && account is { IsPaid: true }
            && identity == Partner.Android && tracks.Count > 0 && tracks.All(t => t.Bitrate < 192) && saved is { } c)
        {
            try
            {
                await LoginAsAsync(Partner.PandoraOne, c.Email, c.Password, ct, intent).ConfigureAwait(false);
            }
            catch (PandoraException)
            {
                await LoginAsAsync(Partner.Android, c.Email, c.Password, ct, intent).ConfigureAwait(false);
                lock (sessionSync)
                {
                    EnsureIntent(intent, ct);
                    ClientChoice = ClientChoice.Android; // Pandora One is not for this account: stop trying
                }
                return tracks;
            }

            return await FetchPlaylistAsync(stationToken, ct, intent).ConfigureAwait(false);
        }

        return tracks;
    }

    private async Task<IReadOnlyList<Track>> FetchPlaylistAsync(string stationToken, CancellationToken ct,
        (long Signout, long Login) intent)
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
        }, ct, requiredIntent: intent).ConfigureAwait(false);
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
        try { return ParseTrackCore(i, fetched, requested, capKbps); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new FormatException("Pandora returned invalid track metadata.", e);
        }
    }

    private static Track? ParseTrackCore(JsonObject i, DateTimeOffset fetched, IReadOnlyList<string>? requested, int capKbps)
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
        if (!double.IsFinite(gain)) gain = 0;
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
            TimeSpan.FromSeconds(Math.Max(0, i["trackLength"]?.GetValue<int>() ?? 0)),
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
    private async Task<JsonObject> CallAsync(string method, JsonObject body, CancellationToken ct,
        long? requiredEpoch = null, (long Signout, long Login)? requiredIntent = null)
    {
        var transaction = WithSession(body, requiredEpoch, requiredIntent);
        try
        {
            return await PostAsync(method, transaction.Body, ct, transaction.Epoch).ConfigureAwait(false);
        }
        catch (PandoraException e) when (e.Code == PandoraException.InvalidAuthToken)
        {
            (string Email, string Password) saved;
            Partner identity;
            lock (sessionSync)
            {
                EnsureSession(transaction.Epoch, ct);
                if (credentials is not { } c)
                    throw;
                saved = c;
                identity = partner;
            }
            await LoginAsAsync(identity, saved.Email, saved.Password, ct, transaction.Intent).ConfigureAwait(false);
            var retry = WithSession(body, requiredIntent: transaction.Intent);
            return await PostAsync(method, retry.Body, ct, retry.Epoch).ConfigureAwait(false);
        }
    }

    private (JsonObject Body, long Epoch, (long Signout, long Login) Intent) WithSession(JsonObject body,
        long? requiredEpoch = null, (long Signout, long Login)? requiredIntent = null)
    {
        lock (sessionSync)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (requiredIntent is { } intent)
                EnsureIntent(intent, CancellationToken.None);
            if (requiredEpoch is { } expected && sessionEpoch != expected)
                throw new OperationCanceledException("The Pandora session changed before the request.");
            if (userAuthToken == null)
                throw new InvalidOperationException("Not logged in to Pandora.");
            var copy = (JsonObject)body.DeepClone();
            copy["userAuthToken"] = userAuthToken;
            copy["syncTime"] = now().ToUnixTimeSeconds() + timeOffset;
            return (copy, sessionEpoch, (signoutEpoch, loginIntent));
        }
    }

    private async Task<JsonObject> PostAsync(string method, JsonObject body, CancellationToken ct, long expectedEpoch)
    {
        CancellationTokenSource linked;
        lock (sessionSync)
        {
            EnsureSession(expectedEpoch, ct);
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        }
        using var linkedScope = linked;
        await protocolGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (sessionSync)
            {
                EnsureSession(expectedEpoch, ct);
                activeRequest = linked;
                if (method == "auth.partnerLogin")
                {
                    protocol?.Dispose();
                    protocol = new MelodicaProtocol(partner);
                    Interlocked.Exchange(ref resetProtocol, 0);
                }
            }
            var native = protocol ?? throw new InvalidOperationException("Initialize the Pandora partner session first.");
            try
            {
                var request = native.Prepare(method, body.ToJsonString());
                using var content = new StringContent(request.Body, System.Text.Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                using var response = await http.PostAsync(request.Url, content, linked.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                lock (sessionSync) EnsureSession(expectedEpoch, ct);
                JsonObject answer;
                try
                {
                    answer = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("not an object");
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
                {
                    // Malformed responses also exercise native transaction cleanup.
                    native.Accept(text);
                    throw new HttpRequestException($"Pandora answered {(int)response.StatusCode} with something that is not JSON.", e);
                }

                var status = native.Accept(NativeResponse(method, answer));
                if (answer["stat"]?.GetValue<string>() == "ok")
                {
                    native.Check(status, allowContinue: method == "auth.partnerLogin");
                    lock (sessionSync) EnsureSession(expectedEpoch, ct);
                    if (method == "auth.partnerLogin")
                        partnerServerTime = native.ServerTime;
                    return answer["result"] as JsonObject ?? [];
                }

                var code = answer["code"]?.GetValue<int>() ?? -1;
                var message = answer["message"]?.GetValue<string>() ?? "no message";
                if (code == PandoraException.InvalidAuthToken)
                {
                    lock (sessionSync)
                    {
                        if (sessionEpoch == expectedEpoch)
                            userAuthToken = null;
                    }
                }
                throw new PandoraException(code, message);
            }
            finally
            {
                // Covers cancellation, HTTP failures, invalid JSON and successful accepts.
                native.Abort();
                ResetProtocolIfRequested();
            }
        }
        finally
        {
            lock (sessionSync)
            {
                if (ReferenceEquals(activeRequest, linked)) activeRequest = null;
            }
            protocolGate.Release();
        }
    }

    private string NativeResponse(string method, JsonObject answer)
    {
        if (method != "station.getPlaylist" || answer["stat"]?.GetValue<string>() != "ok")
            return answer.ToJsonString();

        // Historical libpiano selects one named audioUrlMap quality. XivPiano
        // negotiates extra MP3 URLs and can choose a lower named quality. Project
        // exactly the selected playable stream into that legacy slot, retaining
        // the original response for bitrate/account metadata and managed models.
        var normalized = (JsonObject)answer.DeepClone();
        if (normalized["result"]?["items"] is JsonArray items)
        {
            var formats = RequestedFormats();
            var cap = Quality == AudioQuality.Standard ? 128 : int.MaxValue;
            foreach (var item in items.OfType<JsonObject>().ToArray())
            {
                if (item["songName"] == null)
                    continue;
                var selected = ParseTrack(item, now(), formats, cap);
                if (selected != null)
                    item["audioUrlMap"] = new JsonObject
                    {
                        ["highQuality"] = new JsonObject
                        {
                            ["encoding"] = selected.AudioEncoding,
                            ["audioUrl"] = selected.AudioUrl,
                            ["bitrate"] = selected.Bitrate.ToString(CultureInfo.InvariantCulture),
                        },
                    };
                else
                    // The effective playlist contains playable MP3s. AAC-only
                    // songs are omitted by the same policy used for the queue.
                    items.Remove(item);
            }
        }
        return normalized.ToJsonString();
    }

    public void Dispose()
    {
        lock (sessionSync)
        {
            if (disposed != 0)
                return;
            disposed = 1;
            ++sessionEpoch;
            ++signoutEpoch;
            ++loginIntent;
            credentials = null;
            userAuthToken = null;
            Account = null;
        }
        lifetime.Cancel();
        _ = DisposeProtocolAsync();
    }

    private void ResetProtocolIfRequested()
    {
        // Caller owns protocolGate. Lock ordering is always gate -> sessionSync;
        // sign-out releases sessionSync before trying to acquire the gate.
        lock (sessionSync)
        {
            if (Interlocked.Exchange(ref resetProtocol, 0) == 0)
                return;
            protocol?.Dispose();
            protocol = null;
        }
    }

    private void DisposeIdleProtocol()
    {
        if (!protocolGate.Wait(0))
            return;
        try { ResetProtocolIfRequested(); }
        finally { protocolGate.Release(); }
    }

    private async Task DrainSignedOutProtocolAsync((long Signout, long Login) intent)
    {
        await protocolGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sessionSync)
            {
                // A new explicit sign-in owns its new session. A delayed logout
                // drain must never dispose that replacement session.
                if (intent != (signoutEpoch, loginIntent))
                    return;
                protocol?.Dispose();
                protocol = null;
                Interlocked.Exchange(ref resetProtocol, 0);
            }
        }
        finally { protocolGate.Release(); }
    }

    private async Task DisposeProtocolAsync()
    {
        // Cancelling HTTP is asynchronous; wait without blocking Dalamud's
        // framework thread before releasing the session's native allocations.
        await protocolGate.WaitAsync().ConfigureAwait(false);
        try
        {
            protocol?.Dispose();
            protocol = null;
            lifetime.Dispose();
        }
        finally
        {
            protocolGate.Release();
        }
    }

    private static string Str(JsonObject o, string key) =>
        o[key]?.GetValue<string>() ?? throw new FormatException($"Pandora's answer has no {key}.");

    private static bool Boolean(JsonObject o, string key, bool fallback)
    {
        if (o[key] == null) return fallback;
        if (o[key] is JsonValue value && value.TryGetValue<bool>(out var result)) return result;
        throw new FormatException($"Pandora's answer has invalid {key} metadata.");
    }
}
