using XivPiano.Core.Pandora;

namespace XivPiano.Core;

/// <summary>Plays one track at a time; the plugin's is NAudio + NLayer, the tests' is a fake.</summary>
public interface IAudioOut
{
    /// <summary>Starts a track (stopping any other); completes when it is playing, or throws.</summary>
    Task PlayAsync(Track track, CancellationToken ct);

    void Pause();

    void Resume();

    void Stop();

    bool Paused { get; }

    TimeSpan Position { get; }

    /// <summary>Raised when a track played to its end (not when stopped).</summary>
    event Action? Ended;
}

/// <summary>
/// The radio: a station's queue of songs, refilled from Pandora before it runs dry, played through an
/// <see cref="IAudioOut"/>. Everything that changes state goes through one gate, so a button pressed while a
/// song is still loading waits its turn instead of racing it.
/// </summary>
public sealed class Radio : IDisposable
{
    private readonly PandoraClient pandora;
    private readonly IAudioOut audio;
    private readonly Func<DateTimeOffset> now;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<Track> queue = [];
    private readonly LinkedList<Track> history = new();

    public Radio(PandoraClient pandora, IAudioOut audio, Func<DateTimeOffset>? clock = null)
    {
        this.pandora = pandora;
        this.audio = audio;
        now = clock ?? (() => DateTimeOffset.UtcNow);
        audio.Ended += OnEnded;
    }

    public const int HistoryLength = 20;

    public IReadOnlyList<Station> Stations { get; private set; } = [];

    public Station? Station { get; private set; }

    public Track? Current { get; private set; }

    public IReadOnlyCollection<Track> History => history;

    public IReadOnlyList<Track> Upcoming => queue;

    /// <summary>The last thing that went wrong, in words for the player; cleared by the next success.</summary>
    public string? Error { get; private set; }

    /// <summary>What a status line should say while something is loading.</summary>
    public string? Busy { get; private set; }

    public bool Playing => Current != null && !audio.Paused;

    public event Action? Changed;

    public async Task LoadStationsAsync(CancellationToken ct) =>
        await Guard("Loading your stations", async () =>
        {
            Stations = await pandora.GetStationsAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

    public async Task PlayStationAsync(Station station, CancellationToken ct) =>
        await Guard($"Tuning in to {station.Name}", async () =>
        {
            Station = station;
            queue.Clear();
            await AdvanceAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

    public async Task NextAsync(CancellationToken ct) =>
        await Guard("Next song", () => AdvanceAsync(ct)).ConfigureAwait(false);

    public void TogglePause()
    {
        if (Current == null)
            return;
        if (audio.Paused)
            audio.Resume();
        else
            audio.Pause();
        Changed?.Invoke();
    }

    public void Stop()
    {
        audio.Stop();
        Current = null;
        Changed?.Invoke();
    }

    /// <summary>Thumbs up: Pandora plays more like it.</summary>
    public async Task LoveAsync(CancellationToken ct) =>
        await Guard("Loving it", async () =>
        {
            if (Current is not { } t || Station is not { } s)
                return;
            await pandora.AddFeedbackAsync(s.Token, t.Token, true, ct).ConfigureAwait(false);
            Current = t with { Loved = true };
        }).ConfigureAwait(false);

    /// <summary>Thumbs down: never on this station again, and skipped now.</summary>
    public async Task BanAsync(CancellationToken ct) =>
        await Guard("Banning it", async () =>
        {
            if (Current is not { } t || Station is not { } s)
                return;
            await pandora.AddFeedbackAsync(s.Token, t.Token, false, ct).ConfigureAwait(false);
            await AdvanceAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

    /// <summary>Tired of it: not played for a month on any station, and skipped now.</summary>
    public async Task TiredAsync(CancellationToken ct) =>
        await Guard("Shelving it for a month", async () =>
        {
            if (Current is not { } t)
                return;
            await pandora.SleepSongAsync(t.Token, ct).ConfigureAwait(false);
            await AdvanceAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

    public async Task BookmarkAsync(bool artist, CancellationToken ct) =>
        await Guard(artist ? "Bookmarking the artist" : "Bookmarking the song", async () =>
        {
            if (Current is not { } t)
                return;
            if (artist)
                await pandora.BookmarkArtistAsync(t.Token, ct).ConfigureAwait(false);
            else
                await pandora.BookmarkSongAsync(t.Token, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

    public Task<string> ExplainAsync(CancellationToken ct) =>
        Current is { } t ? pandora.ExplainAsync(t.Token, ct) : Task.FromResult("Nothing is playing.");

    /// <summary>Makes a station, adds it to the list and tunes in.</summary>
    public async Task CreateAndPlayAsync(Func<Task<Station>> create, CancellationToken ct)
    {
        Station? made = null;
        await Guard("Making the station", async () =>
        {
            made = await create().ConfigureAwait(false);
            Stations = [.. Stations.Where(s => s.Id != made.Id), made];
        }).ConfigureAwait(false);
        if (made != null)
            await PlayStationAsync(made, ct).ConfigureAwait(false);
    }

    // ---- inside the gate -------------------------------------------------------------------------

    /// <summary>Plays the next usable song, fetching more when the queue runs low. At most three fetches.</summary>
    private async Task AdvanceAsync(CancellationToken ct)
    {
        if (Station is not { } station)
            return;
        if (Current is { } finished)
        {
            history.AddFirst(finished);
            while (history.Count > HistoryLength)
                history.RemoveLast();
        }

        Current = null;
        for (var fetches = 0; ; )
        {
            queue.RemoveAll(t => t.IsStale(now()));
            if (queue.Count == 0)
            {
                if (fetches++ == 3)
                    throw new InvalidOperationException($"{station.Name} gave no playable songs.");
                queue.AddRange(await pandora.GetPlaylistAsync(station.Token, ct).ConfigureAwait(false));
                continue;
            }

            var next = queue[0];
            queue.RemoveAt(0);
            try
            {
                await audio.PlayAsync(next, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException)
            {
                Error = $"Skipped \"{next.Title}\": {e.Message}";
                continue; // a broken or expired link: the next one
            }

            Current = next;
            break;
        }

        // Keep one song in hand so the next one starts without waiting for Pandora.
        if (queue.Count == 0)
            queue.AddRange(await pandora.GetPlaylistAsync(station.Token, ct).ConfigureAwait(false));
    }

    private void OnEnded() => _ = NextAsync(CancellationToken.None);

    private async Task Guard(string what, Func<Task> action)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        Busy = what;
        Changed?.Invoke();
        try
        {
            await action().ConfigureAwait(false);
            Error = null;
        }
        catch (PandoraException e)
        {
            Error = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            Error = e is TaskCanceledException ? "Pandora did not answer in time." : e.Message;
        }
        finally
        {
            Busy = null;
            gate.Release();
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        audio.Ended -= OnEnded;
        gate.Dispose();
    }
}
