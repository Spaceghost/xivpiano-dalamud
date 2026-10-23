using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XivPiano.Core.Pandora;

namespace XivPiano.Core;

/// <summary>A destination for now-playing metadata and completed listens.</summary>
public interface IScrobbler
{
    string Name { get; }

    Task NowPlayingAsync(Track track, CancellationToken ct);

    Task ScrobbleAsync(Track track, DateTimeOffset startedAt, CancellationToken ct);
}

/// <summary>
/// When a listen counts, by the rule Last.fm sets and ListenBrainz follows: the song is longer than 30 seconds and
/// has played for half its length or four minutes, whichever comes first. Reported once per play.
/// </summary>
public sealed class ScrobbleRule
{
    private string? playing;
    private bool reported;

    public static bool Counts(TimeSpan length, TimeSpan played) =>
        length > TimeSpan.FromSeconds(30) && played >= TimeSpan.FromTicks(Math.Min(length.Ticks / 2, TimeSpan.FromMinutes(4).Ticks));

    /// <summary>True exactly once per play of a track, when it has played enough.</summary>
    public bool Due(Track track, TimeSpan played)
    {
        if (track.Token != playing)
        {
            playing = track.Token;
            reported = false;
        }

        if (reported || !Counts(track.Length, played))
            return false;
        reported = true;
        return true;
    }
}

/// <summary>
/// Last.fm's scrobbling API 2.0 with desktop authorization: <see cref="BeginAuthAsync"/> gets a token and the page
/// where the listener approves it, <see cref="FinishAuthAsync"/> turns the approved token into a session key that
/// lasts until revoked. Calls are signed with the API secret (md5 over the sorted parameters).
/// </summary>
public sealed class LastFm(HttpClient http, string apiKey, string apiSecret, string? sessionKey = null) : IScrobbler
{
    public const string Endpoint = "https://ws.audioscrobbler.com/2.0/";

    public string Name => "Last.fm";

    public string? SessionKey { get; private set; } = sessionKey;

    public async Task<(string Token, string ApproveUrl)> BeginAuthAsync(CancellationToken ct)
    {
        var r = await CallAsync(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["method"] = "auth.getToken" }, get: true, ct).ConfigureAwait(false);
        var token = r["token"]?.GetValue<string>() ?? throw new FormatException("Last.fm gave no token.");
        return (token, $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(apiKey)}&token={Uri.EscapeDataString(token)}");
    }

    public async Task<string> FinishAuthAsync(string token, CancellationToken ct)
    {
        var r = await CallAsync(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["method"] = "auth.getSession", ["token"] = token }, get: true, ct).ConfigureAwait(false);
        SessionKey = r["session"]?["key"]?.GetValue<string>() ?? throw new FormatException("Last.fm gave no session; approve XivPiano on the page first.");
        return SessionKey;
    }

    public Task NowPlayingAsync(Track track, CancellationToken ct) =>
        CallAsync(TrackParams("track.updateNowPlaying", track), get: false, ct);

    public Task ScrobbleAsync(Track track, DateTimeOffset startedAt, CancellationToken ct)
    {
        var p = TrackParams("track.scrobble", track);
        p["timestamp"] = startedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return CallAsync(p, get: false, ct);
    }

    private SortedDictionary<string, string> TrackParams(string method, Track t)
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = method,
            ["artist"] = t.Artist,
            ["track"] = t.Title,
            ["sk"] = SessionKey ?? throw new InvalidOperationException("Connect Last.fm first."),
        };
        if (t.Album.Length > 0)
            p["album"] = t.Album;
        if (t.Length > TimeSpan.Zero)
            p["duration"] = ((int)t.Length.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return p;
    }

    /// <summary>api_sig: md5 of every parameter (but format) as name+value, sorted by name, then the secret.</summary>
    internal static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string secret)
    {
        var text = new StringBuilder();
        foreach (var (k, v) in parameters.Where(p => p.Key != "format").OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(k).Append(v);
        text.Append(secret);
#pragma warning disable CA5351 // Last.fm's protocol defines the signature as md5
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
#pragma warning restore CA5351
    }

    private async Task<JsonObject> CallAsync(SortedDictionary<string, string> p, bool get, CancellationToken ct)
    {
        p["api_key"] = apiKey;
        p["api_sig"] = Sign(p, apiSecret);
        p["format"] = "json";
        HttpResponseMessage response;
        if (get)
        {
            var query = string.Join("&", p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            response = await http.GetAsync(Endpoint + "?" + query, ct).ConfigureAwait(false);
        }
        else
        {
            using var body = new FormUrlEncodedContent(p);
            response = await http.PostAsync(Endpoint, body, ct).ConfigureAwait(false);
        }

        using (response)
        {
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject ?? [];
            if (node["error"] != null)
                throw new HttpRequestException($"Last.fm: {node["message"]?.GetValue<string>() ?? "error " + node["error"]}");
            return node;
        }
    }
}

/// <summary>ListenBrainz (MetaBrainz's open listen history): a user token, no app key.</summary>
public sealed class ListenBrainz(HttpClient http, string userToken, string endpoint = "https://api.listenbrainz.org") : IScrobbler
{
    public string Name => "ListenBrainz";

    public Task NowPlayingAsync(Track track, CancellationToken ct) => SubmitAsync("playing_now", track, null, ct);

    public Task ScrobbleAsync(Track track, DateTimeOffset startedAt, CancellationToken ct) => SubmitAsync("single", track, startedAt, ct);

    internal static JsonObject Body(string type, Track t, DateTimeOffset? at)
    {
        var meta = new JsonObject
        {
            ["artist_name"] = t.Artist,
            ["track_name"] = t.Title,
            ["additional_info"] = new JsonObject
            {
                ["media_player"] = "XivPiano",
                ["submission_client"] = "XivPiano",
                ["music_service_name"] = "Pandora",
                ["duration_ms"] = (long)t.Length.TotalMilliseconds,
            },
        };
        if (t.Album.Length > 0)
            meta["release_name"] = t.Album;
        var listen = new JsonObject { ["track_metadata"] = meta };
        if (at is { } a)
            listen["listened_at"] = a.ToUnixTimeSeconds();
        return new JsonObject { ["listen_type"] = type, ["payload"] = new JsonArray(listen) };
    }

    private async Task SubmitAsync(string type, Track t, DateTimeOffset? at, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/1/submit-listens")
        {
            Content = new StringContent(Body(type, t, at).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Token " + userToken);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ListenBrainz answered {(int)response.StatusCode}.");
    }
}

/// <summary>
/// An opt-in listening history hosted by Spaceghost. Its dashboard owns downstream
/// Last.fm/ListenBrainz connections; the client sends the public ListenBrainz metadata only.
/// </summary>
public sealed class SpaceghostScrobbles : IScrobbler
{
    public const string DefaultEndpoint = "https://spacegho.st/mods/ffxiv/scrobble";
    public const string Dashboard = DefaultEndpoint + "/";
    private readonly HttpClient http;
    private readonly string clientToken;
    private readonly string endpoint;

    public SpaceghostScrobbles(HttpClient http, string clientToken, string endpoint = DefaultEndpoint)
    {
        if (!TryNormalizeEndpoint(endpoint, out var normalized))
            throw new ArgumentException("Use an HTTPS service URL without a username, query or fragment.", nameof(endpoint));
        var token = clientToken.Trim();
        if (token.Length == 0 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new ArgumentException("Enter a client token from the scrobble dashboard.", nameof(clientToken));
        this.http = http;
        this.clientToken = token;
        this.endpoint = normalized;
    }

    public string Name => "Spaceghost Scrobbles";

    public static bool TryNormalizeEndpoint(string? value, out string endpoint)
    {
        endpoint = "";
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        endpoint = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    public Task NowPlayingAsync(Track track, CancellationToken ct) => SubmitAsync("playing_now", track, null, ct);

    public Task ScrobbleAsync(Track track, DateTimeOffset startedAt, CancellationToken ct) => SubmitAsync("single", track, startedAt, ct);

    /// <summary>Checks authorization without adding a listen or a now-playing entry.</summary>
    public async Task<string> ValidateTokenAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/1/validate-token");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        CheckResponse(response);
        JsonObject? answer;
        try
        {
            answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new FormatException("Spaceghost Scrobbles returned an unreadable connection reply.");
        }
        if (answer?["valid"] is not JsonValue valid || !valid.TryGetValue<bool>(out var accepted) || !accepted ||
            answer["user_name"] is not JsonValue name || !name.TryGetValue<string>(out var userName) || string.IsNullOrWhiteSpace(userName))
            throw new FormatException("Spaceghost Scrobbles did not confirm this client token.");
        return userName;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, endpoint + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", clientToken);
        return request;
    }

    private async Task SubmitAsync(string type, Track track, DateTimeOffset? at, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "/1/submit-listens");
        request.Content = new StringContent(ListenBrainz.Body(type, track, at).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        CheckResponse(response);
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new HttpRequestException("Spaceghost Scrobbles rejected the client token. Create a new token in the dashboard.", null, response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Spaceghost Scrobbles answered {(int)response.StatusCode}.", null, response.StatusCode);
    }
}
