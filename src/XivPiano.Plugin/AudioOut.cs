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
public sealed class AudioOut(HttpClient http) : IAudioOut, IDisposable
{
    private readonly Lock gate = new();
    private WaveOut? output;
    private MpegSampleProvider? source;
    private float volume = 0.6f;
    private bool stopping;

    public event Action? Ended;

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
        using var response = await http.GetAsync(track.AudioUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"the audio link answered {(int)response.StatusCode}");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var mpeg = new MpegFile(new MemoryStream(bytes, writable: false));
        if (mpeg.SampleRate <= 0 || mpeg.Channels <= 0)
            throw new InvalidDataException("the audio is not an MP3 this player can read");

        var next = new MpegSampleProvider(mpeg, GainFactor(track.GainDb)) { Volume = volume };
        var device = new WaveOut { BufferMilliseconds = 200, NumberOfBuffers = 3 };
        device.Init(next);
        device.PlaybackStopped += OnStopped;
        lock (gate)
        {
            StopLocked();
            source = next;
            output = device;
            stopping = false;
            device.Play();
        }
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

    public void Pause() => output?.Pause();

    public void Resume() => output?.Play();

    public void Stop()
    {
        lock (gate)
            StopLocked();
    }

    private void StopLocked()
    {
        stopping = true;
        if (output is { } o)
        {
            o.PlaybackStopped -= OnStopped;
            o.Stop();
            o.Dispose();
        }

        source?.Dispose();
        output = null;
        source = null;
    }

    public void Dispose() => Stop();

    /// <summary>NLayer's decoder as an NAudio sample source, with gain and volume.</summary>
    private sealed class MpegSampleProvider(MpegFile mpeg, float gain) : ISampleProvider, IDisposable
    {
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
            return read;
        }

        public void Dispose() => mpeg.Dispose();
    }
}
