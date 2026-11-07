using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using XivPiano.Core.Pandora;

namespace XivPiano.Core.Tests;

public class MelodicaTests
{
    [Fact]
    public void NativeSessionBuildsPartnerRequestAndCanAbortAndRetry()
    {
        using var protocol = new MelodicaProtocol(Partner.Android);
        for (var i = 0; i < 25; i++)
        {
            var request = protocol.Prepare("auth.partnerLogin", "{}");
            Assert.Equal("https", request.Url.Scheme);
            Assert.Equal(Partner.Android.Host, request.Url.Host);
            Assert.Contains("method=auth.partnerLogin", request.Url.Query);
            var body = JsonNode.Parse(request.Body)!;
            Assert.Equal(Partner.Android.DeviceModel, body["deviceModel"]!.GetValue<string>());
            Assert.Equal(Partner.Android.Username, body["username"]!.GetValue<string>());
            protocol.Abort();
        }
    }

    [Fact]
    public void NativeParserRejectsMalformedResponsesAndSessionCanRetry()
    {
        using var protocol = new MelodicaProtocol(Partner.Android);
        protocol.Prepare("auth.partnerLogin", "{}");
        var status = protocol.Accept("{not-json");
        Assert.Throws<FormatException>(() => protocol.Check(status));
        protocol.Abort();
        var request = protocol.Prepare("auth.partnerLogin", "{}");
        Assert.Contains("auth.partnerLogin", request.Url.Query);
        protocol.Abort();
    }

    [Fact]
    public async Task HttpFailureAbortsNativeRequestBeforeNextLogin()
    {
        var server = new FailOnceHandler();
        using var client = new PandoraClient(new HttpClient(server));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.LoginAsync("fake@example.test", "right", CancellationToken.None));
        await client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        Assert.True(client.LoggedIn);
    }

    [Fact]
    public async Task CancellationReleasesTransactionGateAndNativeRequest()
    {
        var server = new CancelOnceHandler();
        using var client = new PandoraClient(new HttpClient(server));
        using var cancellation = new CancellationTokenSource();
        var login = client.LoginAsync("fake@example.test", "right", cancellation.Token);
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        await client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        Assert.True(client.LoggedIn);
    }

    [Fact]
    public async Task DisposedClientRejectsFurtherTransactions()
    {
        var server = new FakePandora();
        var client = new PandoraClient(new HttpClient(server));
        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.LoginAsync("fake@example.test", "right", CancellationToken.None));
        client.Dispose();
    }

    [Fact]
    public async Task HttpErrorCannotCommitAnOtherwiseValidLoginEnvelope()
    {
        using var handler = new HttpErrorOnceHandler();
        using var client = new PandoraClient(new HttpClient(handler));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.LoginAsync("fake@example.test", "right", CancellationToken.None));
        Assert.False(client.LoggedIn);
        Assert.Null(client.Account);
        await client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        Assert.True(client.LoggedIn);
    }

    [Fact]
    public async Task LogoutDuringUserLoginCannotResurrectTheSession()
    {
        using var handler = new PauseOnceHandler("auth.userLogin");
        using var client = new PandoraClient(new HttpClient(handler));
        var login = client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Logout();
        handler.Resume.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.False(client.LoggedIn);
        Assert.Null(client.Account);
        await client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        Assert.True(client.LoggedIn);
    }

    [Fact]
    public async Task LogoutCancelsStalledHttpWithoutWaitingForTheServer()
    {
        using var handler = new PauseOnceHandler("auth.userLogin");
        using var client = new PandoraClient(new HttpClient(handler));
        var login = client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Logout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(client.LoggedIn);
        await client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        Assert.True(client.LoggedIn);
    }

    [Fact]
    public async Task DisposeDuringUserLoginCannotResurrectTheSession()
    {
        using var handler = new PauseOnceHandler("auth.userLogin");
        var client = new PandoraClient(new HttpClient(handler));
        var login = client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Dispose();
        handler.Resume.SetResult();
        var error = await Record.ExceptionAsync(() => login);
        Assert.True(error is OperationCanceledException or ObjectDisposedException);
        Assert.False(client.LoggedIn);
        Assert.Null(client.Account);
    }

    [Fact]
    public async Task AccountSwitchCancelsAnOlderInFlightRequest()
    {
        using var handler = new PauseOnceHandler("user.getStationList");
        using var client = new PandoraClient(new HttpClient(handler));
        await client.LoginAsync("first@example.test", "right", CancellationToken.None);
        var stations = client.GetStationsAsync(CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = client.LoginAsync("second@example.test", "right", CancellationToken.None);
        handler.Resume.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stations);
        await next;
        Assert.True(client.LoggedIn);
    }

    [Fact]
    public async Task SupersededPandoraOneLoginCannotFallbackOverTheNewAccount()
    {
        using var handler = new PauseOnceHandler("auth.userLogin");
        using var client = new PandoraClient(new HttpClient(handler)) { ClientChoice = ClientChoice.PandoraOne };
        var first = client.LoginAsync("first@example.test", "right", CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.LoginAsync("second@example.test", "right", CancellationToken.None);
        handler.Resume.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await second;
        Assert.True(client.LoggedIn);
        Assert.Equal("Android", client.Account!.Client);
    }

    [Fact]
    public async Task LogoutDuringPlaylistProjectionCannotReturnAStalePlaylist()
    {
        var server = new FakePandora
        {
            Answer = (_, _) => JsonNode.Parse("""{"items":[{"songName":"A","artistName":"B","trackToken":"t","stationId":"s","additionalAudioUrl":"https://audio.test/track"}]}""")!,
        };
        PandoraClient? client = null;
        var armed = false;
        var ticks = 0;
        using var owned = client = new PandoraClient(new HttpClient(server), () =>
        {
            // Fetch timestamp, authenticated body clock, then native projection.
            if (armed && ++ticks == 3) client!.Logout();
            return DateTimeOffset.FromUnixTimeSeconds(1_790_223_000);
        });
        await client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPlaylistAsync("s", CancellationToken.None));
        Assert.False(client.LoggedIn);
        Assert.Null(client.Account);
    }

    [Theory]
    [InlineData("isSubscriber")]
    [InlineData("hasAudioAds")]
    public async Task InvalidAccountMetadataCannotPartiallyCommitLogin(string field)
    {
        using var handler = new InvalidMetadataOnceHandler(field);
        using var client = new PandoraClient(new HttpClient(handler));
        await Assert.ThrowsAsync<FormatException>(() =>
            client.LoginAsync("fake@example.test", "right", CancellationToken.None));
        Assert.False(client.LoggedIn);
        Assert.Null(client.Account);
        await client.LoginAsync("fake@example.test", "right", CancellationToken.None);
        Assert.True(client.LoggedIn);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void TrackMetadataCannotSendNonfiniteGainOrNegativeLengthToAudio(string gain)
    {
        var item = JsonNode.Parse("""{"songName":"A","artistName":"B","trackToken":"t","stationId":"s","additionalAudioUrl":"https://audio.test/track","trackLength":-1}""")!.AsObject();
        item["trackGain"] = gain;
        var track = PandoraClient.ParseTrack(item, DateTimeOffset.UnixEpoch);
        Assert.NotNull(track);
        Assert.Equal(0, track.GainDb);
        Assert.Equal(TimeSpan.Zero, track.Length);
    }

    [Fact]
    public void WrongTypeTrackMetadataHasAConsistentProtocolError()
    {
        var item = JsonNode.Parse("""{"songName":"A","additionalAudioUrl":42}""")!.AsObject();
        Assert.Throws<FormatException>(() => PandoraClient.ParseTrack(item, DateTimeOffset.UnixEpoch));
    }

    private sealed class HttpErrorOnceHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker next = new(new FakePandora());
        private int requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await next.SendAsync(request, ct);
            if (Interlocked.Increment(ref requests) == 1)
                response.StatusCode = HttpStatusCode.InternalServerError;
            return response;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) next.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class InvalidMetadataOnceHandler(string field) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker next = new(new FakePandora());
        private int replaced;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await next.SendAsync(request, ct);
            if (request.RequestUri!.Query.Contains("method=auth.userLogin") && Interlocked.Exchange(ref replaced, 1) == 0)
            {
                var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
                answer["result"]![field] = "invalid boolean";
                response.Content.Dispose();
                response.Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json");
            }
            return response;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) next.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class PauseOnceHandler(string method) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker next = new(new FakePandora());
        private int paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await next.SendAsync(request, ct);
            if (request.RequestUri!.Query.Contains("method=" + method) && Interlocked.Exchange(ref paused, 1) == 0)
            {
                Entered.SetResult();
                await Resume.Task.WaitAsync(ct);
            }
            return response;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) next.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FailOnceHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker next = new(new FakePandora());
        private int requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref requests) == 1
                ? throw new HttpRequestException("Synthetic pre-response failure")
                : next.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) next.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CancelOnceHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker next = new(new FakePandora());
        private int requests;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                Entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await next.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) next.Dispose();
            base.Dispose(disposing);
        }
    }
}
