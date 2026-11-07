using System.Diagnostics;
using NAudio;
using NAudio.Wave;
using NLayer;
using XivPiano.Core;
using XivPiano.Core.Pandora;

namespace XivPiano.Plugin;

/// <summary>
/// Plays Pandora's MP3 streams: downloaded whole (a song is a few MB, and a complete file can be paused,
/// resumed and timed without a network stall), decoded by NLayer and played through winmm. The song's replay
/// gain and the player's volume are applied here.
/// </summary>
public sealed class AudioOut(HttpClient http, RecordingLibrary? recordings = null) : IAudioOut, IDisposable
{
    public Func<Track, ReadOnlyMemory<byte>, CancellationToken, Task>? SaveDownloaded { get; set; }
    private readonly Lock gate = new();
    private WaveOut? output;
    private MpegSampleProvider? source;
    private float volume = 0.6f;
    private bool stopping;
    private bool disposed;
    private long streamId;

    /// <summary>Read-only visual snapshot aligned to winmm's played bytes, never MP3 decode-ahead.</summary>
    /// Called on the game's main thread (other plugins poll it every frame), so it never waits: a busy gate
    /// reads as silence for one frame. Under Wine, a contended Lock wait there once lost its wake-up and froze
    /// the game for good.
    public AudioFeatureSnapshot ReadFeatures()
    {
        if (!gate.TryEnter())
            return AudioFeatureSnapshot.Silent(Interlocked.Read(ref streamId), false);
        try
        {
            if (output is not { } device || source is not { FeaturesAvailable: true } current)
                return AudioFeatureSnapshot.Silent(streamId, false);
            try
            {
                var rate = device.OutputWaveFormat.AverageBytesPerSecond;
                if (rate <= 0) return AudioFeatureSnapshot.Silent(streamId, false);
                return current.Features.Read((double)device.GetPosition() / rate,
                    device.PlaybackState == PlaybackState.Playing,
                    (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
            }
            catch (Exception e) when (e is MmException or ObjectDisposedException or InvalidOperationException)
            {
                // Device teardown/failure must not leak stale visual energy or affect audio playback.
                return AudioFeatureSnapshot.Silent(streamId, false);
            }
        }
        finally
        {
            gate.Exit();
        }
    }

    public event Action? Ended;

    public SongRhythmTimeline ReadRhythm(double lookAhead)
    {
        if (!gate.TryEnter()) return SongRhythmAnalysis.Read(Interlocked.Read(ref streamId), false, 0, 0, null, lookAhead);
        try
        {
            if (output is not { } device || source is not { } current)
                return SongRhythmAnalysis.Read(streamId, false, 0, 0, null, lookAhead);
            var rate = device.OutputWaveFormat.AverageBytesPerSecond;
            var position = rate > 0 ? (double)device.GetPosition() / rate : 0;
            return SongRhythmAnalysis.Read(current.StreamId, device.PlaybackState == PlaybackState.Playing,
                position, current.Duration.TotalSeconds, current.Rhythm, lookAhead);
        }
        catch (Exception e) when (e is MmException or ObjectDisposedException or InvalidOperationException)
        {
            return SongRhythmAnalysis.Read(streamId, false, 0, 0, null, lookAhead);
        }
        finally { gate.Exit(); }
    }

    public bool Paused => output?.PlaybackState == PlaybackState.Paused;

    public TimeSpan Position => source?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration => source?.Duration ?? TimeSpan.Zero;

    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0f, 1f);
            if (source != null)
                source.Volume = volume;
        }
    }

    public async Task PlayAsync(Track track, CancellationToken ct)
    {
        byte[] bytes;
        if (track.RecordingId is { } id)
            bytes = await (recordings ?? throw new IOException("The recording library is unavailable.")).ReadAsync(id, ct).ConfigureAwait(false);
        else
        {
            using var response = await http.GetAsync(track.AudioUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"the audio link answered {(int)response.StatusCode}");
            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        var mpeg = new MpegFile(new MemoryStream(bytes, writable: false));
        if (mpeg.SampleRate <= 0 || mpeg.Channels <= 0)
        {
            mpeg.Dispose();
            throw new InvalidDataException("the audio is not an MP3 this player can read");
        }

        var next = new MpegSampleProvider(mpeg, GainFactor(track.GainDb), Interlocked.Increment(ref streamId)) { Volume = volume };
        var device = new WaveOut { BufferMilliseconds = 200, NumberOfBuffers = 3 };
        var installed = false;
        (WaveOut? Output, MpegSampleProvider? Source) old = default;
        try
        {
            device.Init(next);
            device.PlaybackStopped += OnStopped;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                ct.ThrowIfCancellationRequested();
                old = DetachLocked();
                source = next;
                output = device;
                stopping = false;
                try { device.Play(); installed = true; }
                catch { DetachLocked(); throw; }
            }
        }
        finally
        {
            Release(old);
            if (!installed)
            {
                device.PlaybackStopped -= OnStopped;
                device.Dispose();
                next.Dispose();
            }
        }
        next.AnalyzeSong(bytes);
        if (track.RecordingId == null && SaveDownloaded is { } save)
            await save(track, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Pandora's trackGain is replay gain in dB; capped so a quiet master never clips.</summary>
    internal static float GainFactor(double db) => (float)Math.Pow(10, Math.Clamp(db, -15, 6) / 20);

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        bool natural;
        lock (gate)
            natural = sender == output && !stopping;
        if (natural)
            Ended?.Invoke();
    }

    public void Pause()
    {
        lock (gate)
        {
            output?.Pause();
            source?.Features.ResetPlayback();
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            source?.Features.ResetPlayback();
            output?.Play();
        }
    }

    public void Stop()
    {
        (WaveOut? Output, MpegSampleProvider? Source) old;
        lock (gate)
            old = DetachLocked();
        Release(old);
    }

    /// <summary>Takes the current player out under the gate; stopping and disposing it (slow winmm calls)
    /// happens after the gate is released, so readers are never held up behind a device teardown.</summary>
    private (WaveOut? Output, MpegSampleProvider? Source) DetachLocked()
    {
        stopping = true;
        var old = (output, source);
        if (output is { } o)
            o.PlaybackStopped -= OnStopped;
        output = null;
        source = null;
        return old;
    }

    private static void Release((WaveOut? Output, MpegSampleProvider? Source) old)
    {
        if (old.Output is { } o)
        {
            o.Stop();
            o.Dispose();
        }
        old.Source?.Dispose();
    }

    public void Dispose()
    {
        (WaveOut? Output, MpegSampleProvider? Source) old;
        lock (gate)
        {
            disposed = true;
            old = DetachLocked();
        }
        Release(old);
    }

    /// <summary>NLayer's decoder as an NAudio sample source, with gain and volume.</summary>
    private sealed class MpegSampleProvider(MpegFile mpeg, float gain, long streamId) : ISampleProvider, IDisposable
    {
        public long StreamId { get; } = streamId;
        private SongPulse[]? rhythm;
        private readonly CancellationTokenSource analysisCancellation = new();
        public SongPulse[]? Rhythm => Volatile.Read(ref rhythm);
        public void AnalyzeSong(byte[] bytes)
        {
            var token = analysisCancellation.Token;
            _ = Task.Run(() =>
            {
                try
                {
                    // Independent decoder: never seek, retain PCM from or contend with the sound callback.
                    using var decoder = new MpegFile(new MemoryStream(bytes, writable: false));
                    var analysis = new SongRhythmAnalysis(decoder.SampleRate, decoder.Channels);
                    var samples = new float[decoder.SampleRate / 4 * decoder.Channels];
                    var decoded = 0d;
                    while (decoded < SongRhythmAnalysis.MaximumSeconds)
                    {
                        token.ThrowIfCancellationRequested();
                        var count = decoder.ReadSamples(samples, 0, samples.Length);
                        if (count == 0) break;
                        analysis.Push(samples.AsSpan(0, count));
                        decoded += (double)count / decoder.Channels / decoder.SampleRate;
                    }
                    token.ThrowIfCancellationRequested();
                    Volatile.Write(ref rhythm, analysis.Finish());
                }
                catch { /* Optional chart analysis must never break music playback. */ }
            }, token);
        }
        public AudioFeatures Features { get; } = new(mpeg.SampleRate, mpeg.Channels, streamId);
        private volatile bool featuresAvailable = true;
        public bool FeaturesAvailable => featuresAvailable;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(mpeg.SampleRate, mpeg.Channels);

        public float Volume { get; set; } = 1f;

        public TimeSpan Position => mpeg.Time;

        public TimeSpan Duration => mpeg.Duration;

        // NLayer decodes into an array; NAudio 3 reads into a span. One array, grown as needed, so a read allocates nothing.
        private float[] scratch = [];

        public int Read(Span<float> buffer)
        {
            if (scratch.Length < buffer.Length)
                scratch = new float[buffer.Length];
            var read = mpeg.ReadSamples(scratch, 0, buffer.Length);
            var scale = gain * Volume;
            for (var i = 0; i < read; i++)
                buffer[i] = Math.Clamp(scratch[i] * scale, -1f, 1f);
            // Visual analysis is optional: it must never break the sound callback.
            if (FeaturesAvailable)
            {
                try { Features.Push(buffer[..read]); }
                catch { featuresAvailable = false; }
            }
            return read;
        }

        public void Dispose() { analysisCancellation.Cancel(); mpeg.Dispose(); }
    }
}
