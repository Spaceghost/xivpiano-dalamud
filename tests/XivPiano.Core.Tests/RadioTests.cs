using System.Text.Json.Nodes;
using XivPiano.Core.Pandora;

namespace XivPiano.Core.Tests;

internal sealed class FakeAudio : IAudioOut
{
    public List<string> Played { get; } = [];

    public HashSet<string> Broken { get; } = [];

    public bool Paused { get; private set; }

    public TimeSpan Position => TimeSpan.Zero;

    public event Action? Ended;

    public Task PlayAsync(Track track, CancellationToken ct)
    {
        if (Broken.Contains(track.Title))
            throw new HttpRequestException("403 Forbidden");
        Played.Add(track.Title);
        Paused = false;
        return Task.CompletedTask;
    }

    public void Pause() => Paused = true;

    public void Resume() => Paused = false;

    public void Stop()
    {
    }

    public void Finish() => Ended?.Invoke();
}

public class RadioTests
{
    private static readonly Station Cafe = new("st", "s1", "Eorzean Café", false, false);

    private sealed class World
    {
        public FakePandora Server { get; } = new();

        public FakeAudio Audio { get; } = new();

        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_790_223_231);

        public int Batch { get; set; }

        public List<(string Method, JsonObject Body)> Feedback { get; } = [];

        public async Task<Radio> RadioAsync()
        {
            Server.Answer = (method, body) =>
            {
                if (method != "station.getPlaylist")
                {
                    Feedback.Add((method, body));
                    return new JsonObject();
                }

                Batch++;
                var items = new JsonArray();
                for (var i = 1; i <= 2; i++)
                {
                    items.Add(new JsonObject
                    {
                        ["songName"] = $"b{Batch}s{i}", ["artistName"] = "X", ["trackToken"] = $"t{Batch}{i}", ["stationId"] = "s1",
                        ["additionalAudioUrl"] = $"https://mp3/{Batch}/{i}", ["trackLength"] = 200,
                    });
                }

                return new JsonObject { ["items"] = items };
            };
            var client = new PandoraClient(new HttpClient(Server), () => Now);
            await client.LoginAsync("me@example.com", "right", CancellationToken.None);
            return new Radio(client, Audio, () => Now);
        }
    }

    [Fact]
    public async Task PlaysAStationAndKeepsASongInHand()
    {
        var w = new World();
        using var radio = await w.RadioAsync();
        await radio.PlayStationAsync(Cafe, CancellationToken.None);
        Assert.Equal("b1s1", radio.Current!.Title);
        Assert.Single(radio.Upcoming); // b1s2 waits

        await radio.NextAsync(CancellationToken.None);
        Assert.Equal("b1s2", radio.Current!.Title);
        Assert.Equal(2, radio.Upcoming.Count); // ran low: the next playlist was fetched ahead
        Assert.Equal("b1s1", radio.History.First().Title);
        Assert.Null(radio.Error);
    }

    [Fact]
    public async Task TheEndOfASongStartsTheNext()
    {
        var w = new World();
        using var radio = await w.RadioAsync();
        await radio.PlayStationAsync(Cafe, CancellationToken.None);
        w.Audio.Finish();
        for (var i = 0; i < 50 && radio.Current?.Title != "b1s2"; i++)
            await Task.Delay(10);
        Assert.Equal(["b1s1", "b1s2"], w.Audio.Played);
    }

    [Fact]
    public async Task ExpiredLinksAreSkipped()
    {
        var w = new World();
        using var radio = await w.RadioAsync();
        await radio.PlayStationAsync(Cafe, CancellationToken.None);
        w.Now += TimeSpan.FromMinutes(56); // b1s2's link is past Pandora's hour
        await radio.NextAsync(CancellationToken.None);
        Assert.Equal("b2s1", radio.Current!.Title);
        Assert.DoesNotContain("b1s2", w.Audio.Played);
    }

    [Fact]
    public async Task ABrokenLinkIsSkippedWithANote()
    {
        var w = new World();
        using var radio = await w.RadioAsync();
        w.Audio.Broken.Add("b1s1");
        await radio.PlayStationAsync(Cafe, CancellationToken.None);
        Assert.Equal("b1s2", radio.Current!.Title);
    }

    [Fact]
    public async Task BanAndTiredSendFeedbackAndSkip()
    {
        var w = new World();
        using var radio = await w.RadioAsync();
        await radio.PlayStationAsync(Cafe, CancellationToken.None);

        await radio.BanAsync(CancellationToken.None);
        var (method, body) = w.Feedback[^1];
        Assert.Equal("station.addFeedback", method);
        Assert.False(body["isPositive"]!.GetValue<bool>());
        Assert.Equal("t11", body["trackToken"]!.GetValue<string>());
        Assert.Equal("b1s2", radio.Current!.Title);

        await radio.TiredAsync(CancellationToken.None);
        Assert.Equal("user.sleepSong", w.Feedback[^1].Method);
        Assert.Equal("b2s1", radio.Current!.Title);

        await radio.LoveAsync(CancellationToken.None);
        Assert.True(w.Feedback[^1].Body["isPositive"]!.GetValue<bool>());
        Assert.True(radio.Current!.Loved);
        Assert.Equal("b2s1", radio.Current.Title); // love does not skip
    }

    [Fact]
    public async Task ErrorsBecomeWordsNotExceptions()
    {
        var w = new World();
        using var radio = await w.RadioAsync();
        w.Server.Answer = (_, _) => throw new HttpRequestException("network is down");
        await radio.PlayStationAsync(Cafe, CancellationToken.None);
        Assert.Null(radio.Current);
        Assert.Contains("network is down", radio.Error);
        Assert.Null(radio.Busy);
    }

    [Fact]
    public async Task PauseToggles()
    {
        var w = new World();
        using var radio = await w.RadioAsync();
        radio.TogglePause(); // nothing playing: nothing happens
        Assert.False(w.Audio.Paused);
        await radio.PlayStationAsync(Cafe, CancellationToken.None);
        radio.TogglePause();
        Assert.False(radio.Playing);
        radio.TogglePause();
        Assert.True(radio.Playing);
    }
}
