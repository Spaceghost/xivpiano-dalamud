using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace XivPiano.Core;

/// <summary>Explicit private upload and revocable sharing using the listener's existing client token.</summary>
public sealed class RecordingHost
{
    private readonly HttpClient http;
    private readonly string endpoint;
    private readonly string token;

    public RecordingHost(HttpClient http, string clientToken, string serviceEndpoint = SpaceghostScrobbles.DefaultEndpoint)
    {
        if (!SpaceghostScrobbles.TryNormalizeEndpoint(serviceEndpoint, out endpoint))
            throw new ArgumentException("Choose a valid HTTPS recording host.", nameof(serviceEndpoint));
        token = clientToken.Trim();
        if (token.Length == 0 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new ArgumentException("Save your Spaceghost Scrobbles client token in Settings first.", nameof(clientToken));
        this.http = http;
    }

    public async Task<string> ShareAsync(RecordingLibrary library, SavedRecording recording, CancellationToken ct)
    {
        var input = new JsonObject
        {
            ["id"] = recording.Id,
            ["bytes"] = recording.Bytes,
            ["track_metadata"] = new JsonObject
            {
                ["artist_name"] = recording.Artist,
                ["track_name"] = recording.Title,
                ["release_name"] = recording.Album,
                ["additional_info"] = new JsonObject { ["duration_ms"] = recording.DurationMs, ["submission_client"] = "XivPiano" },
            },
        };
        var upload = await JsonAsync("uploads", input, ct).ConfigureAwait(false);
        if (upload["status"]?.GetValue<string>() != "ready")
        {
            var lease = upload["upload_id"]?.GetValue<string>() ?? throw new FormatException("The recording host did not reserve an upload.");
            var bytes = await library.ReadAsync(recording.Id, ct).ConfigureAwait(false);
            using var request = Request(HttpMethod.Put, recording.Id + "/audio");
            request.Headers.Add("X-Recording-Upload", lease);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            await CheckAsync(response, ct).ConfigureAwait(false);
        }
        var share = await JsonAsync(recording.Id + "/share", new JsonObject(), ct).ConfigureAwait(false);
        if (!Uri.TryCreate(share["url"]?.GetValue<string>(), UriKind.Absolute, out var link) ||
            link.Scheme != Uri.UriSchemeHttps || link.GetLeftPart(UriPartial.Authority) != new Uri(endpoint).GetLeftPart(UriPartial.Authority) ||
            link.UserInfo.Length != 0 || link.Query.Length != 0 || !link.AbsolutePath.StartsWith(new Uri(endpoint).AbsolutePath.TrimEnd('/') + "/listen/", StringComparison.Ordinal) || link.Fragment.Length != 44)
            throw new FormatException("The recording host returned an invalid sharing link.");
        return link.AbsoluteUri;
    }

    public Task RevokeAsync(SavedRecording recording, CancellationToken ct) => JsonAsync(recording.Id + "/revoke", new JsonObject(), ct);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, endpoint + "/1/recordings/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        return request;
    }

    private async Task<JsonObject> JsonAsync(string path, JsonObject input, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, path);
        request.Content = new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await CheckAsync(response, ct).ConfigureAwait(false);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject
            ?? throw new FormatException("The recording host returned an invalid reply.");
    }

    private static Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new HttpRequestException("The recording host rejected the client token. Check Settings → Scrobbling.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The recording host answered {(int)response.StatusCode}. Your local MP3 has been kept; retry sharing later.");
        return Task.CompletedTask;
    }
}
