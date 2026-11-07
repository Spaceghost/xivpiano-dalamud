using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivPiano.Core;

/// <summary>Owned, immutable 32-band linear RMS-amplitude spectrum. Equal logarithmic
/// frequency intervals from MinimumHz through MaximumHz; not musical notes or BPM.
/// Value storage prevents consumers from changing audio history or retaining a mutable array.</summary>
[JsonConverter(typeof(AudioSpectrumJsonConverter))]
public readonly struct AudioSpectrum : IEquatable<AudioSpectrum>
{
    public const int BandCount = 32;
    [InlineArray(BandCount)] private struct BandBuffer { private float element; }
    private readonly BandBuffer bands;
    public float MinimumHz { get; }
    public float MaximumHz { get; }
    public float this[int index] => index is >= 0 and < BandCount ? bands[index] : throw new ArgumentOutOfRangeException(nameof(index));
    internal AudioSpectrum(float minimumHz, float maximumHz, ReadOnlySpan<float> values)
    {
        if (values.Length != BandCount) throw new ArgumentException("Expected32 spectrum bands.", nameof(values));
        MinimumHz = minimumHz; MaximumHz = maximumHz; bands = default;
        for (var i = 0; i < BandCount; i++) bands[i] = values[i];
    }
    public bool Equals(AudioSpectrum other)
    {
        if (MinimumHz != other.MinimumHz || MaximumHz != other.MaximumHz) return false;
        for (var i = 0; i < BandCount; i++) if (bands[i] != other.bands[i]) return false;
        return true;
    }
    public override bool Equals(object? other) => other is AudioSpectrum value && Equals(value);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(MinimumHz); hash.Add(MaximumHz);
        for (var i = 0; i < BandCount; i++) hash.Add(bands[i]);
        return hash.ToHashCode();
    }
}

/// <summary>Fixed anonymous v1 extension. Deserialization is intentionally not a producer capability.</summary>
public sealed class AudioSpectrumJsonConverter : JsonConverter<AudioSpectrum>
{
    public override AudioSpectrum Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        throw new NotSupportedException("Audio spectrum is a read-only producer payload.");
    public override void Write(Utf8JsonWriter writer, AudioSpectrum value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("minimumHz", value.MinimumHz); writer.WriteNumber("maximumHz", value.MaximumHz);
        writer.WriteStartArray("bands");
        for (var i = 0; i < AudioSpectrum.BandCount; i++) writer.WriteNumberValue(value[i]);
        writer.WriteEndArray(); writer.WriteEndObject();
    }
}

/// <summary>Local, anonymous visual features; not a beat/BPM or musical-note detector.</summary>
public readonly record struct AudioFeatureSnapshot(int Version, long StreamId, long Sequence,
    bool Playing, bool Fresh, double AgeSeconds, double PositionSeconds,
    float Rms, float Peak, float Low, float Mid, float High, float Onset)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AudioSpectrum? Spectrum { get; init; }
    public static AudioFeatureSnapshot Silent(long streamId, bool playing, double position = 0) =>
        new(1, streamId, 0, playing, false, 1, Math.Max(0, double.IsFinite(position) ? position : 0), 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Analyzes post-volume interleaved PCM in 20ms windows. One audio producer; polling consumers.
/// Fixed 128-window (~2.56s) history lets consumers use the hardware playback position, not decode-ahead.
/// Three first-order crossover bands (250/2000Hz), channel-energy averaging (no anti-phase cancellation),
/// and a short positive-energy onset envelope. A trailing Hann FFT adds32 real logarithmic
/// power-integrated bands (40Hz..min(16kHz,Nyquist)), channel-energy averaged. FFT length is
/// next-power-of-two >=40ms (512..8192 samples); effective frequency resolution is rate/N,
/// so adjacent low bands can share spectral resolution. No IO/subscriptions/per-buffer allocation.
/// </summary>
public sealed class AudioFeatures
{
    public const int HistoryLength = 128;
    public const double MaximumStallSeconds = .2;
    private readonly Lock gate = new();
    private readonly Frame[] history = new Frame[HistoryLength];
    private readonly double[] lowState;
    private readonly double[] wideState;
    private readonly int sampleRate;
    private readonly int channels;
    private readonly int windowFrames;
    private readonly double lowAlpha;
    private readonly double wideAlpha;
    private readonly long streamId;
    private readonly LogSpectrum spectrum;
    private long totalFrames;
    private long sequence;
    private int inWindow;
    private double energy, lowEnergy, midEnergy, highEnergy, peak, previousRms, onset;
    private int published;
    private int count;
    private double lastNow = double.NaN, lastPosition = -1, lastAdvance;
    private bool suppressNextOnset = true;
    private long suppressedSequence;

    private readonly record struct Frame(long Sequence, long EndFrame,
        float Rms, float Peak, float Low, float Mid, float High, float Onset, AudioSpectrum Spectrum);

    public AudioFeatures(int sampleRate, int channels, long streamId)
    {
        if (sampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        this.sampleRate = sampleRate;
        this.channels = channels;
        this.streamId = streamId;
        spectrum = new(sampleRate, channels);
        windowFrames = Math.Max(1, sampleRate / 50);
        lowAlpha = 1 - Math.Exp(-2 * Math.PI * 250 / sampleRate);
        wideAlpha = 1 - Math.Exp(-2 * Math.PI * 2000 / sampleRate);
        lowState = new double[channels];
        wideState = new double[channels];
    }

    /// <summary>Called once for precisely the samples sent to the output device, after gain/clipping.</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        // Audio buffers are normally 200ms. Refuse malformed or >1s calls rather than unbounded analysis.
        if (samples.Length % channels != 0 || samples.Length > sampleRate * channels)
        {
            totalFrames += samples.Length / channels;
            inWindow = 0;
            energy = lowEnergy = midEnergy = highEnergy = peak = previousRms = onset = 0;
            Array.Clear(lowState);
            Array.Clear(wideState);
            spectrum.Reset();
            lock (gate) count = 0;
            return;
        }
        for (var i = 0; i < samples.Length; i += channels)
        {
            for (var c = 0; c < channels; c++)
            {
                var raw = samples[i + c];
                var x = float.IsFinite(raw) ? Math.Clamp(raw, -1f, 1f) : 0;
                spectrum.PushChannel(c, x);
                lowState[c] += lowAlpha * (x - lowState[c]);
                wideState[c] += wideAlpha * (x - wideState[c]);
                var mid = wideState[c] - lowState[c];
                var high = x - wideState[c];
                energy += x * x;
                lowEnergy += lowState[c] * lowState[c];
                midEnergy += mid * mid;
                highEnergy += high * high;
                peak = Math.Max(peak, Math.Abs(x));
            }
            spectrum.EndSample();
            totalFrames++;
            if (++inWindow < windowFrames) continue;
            var divisor = windowFrames * channels;
            var rms = Math.Sqrt(energy / divisor);
            var rise = rms > .008 ? Math.Clamp((rms - previousRms - .008) / (previousRms + .08), 0, 1) : 0;
            onset = Math.Max(rise, onset * Math.Exp(-(double)windowFrames / sampleRate / .08));
            previousRms = rms;
            var frame = new Frame(++sequence, totalFrames, Unit(rms), Unit(peak),
                Unit(Math.Sqrt(lowEnergy / divisor)), Unit(Math.Sqrt(midEnergy / divisor)),
                Unit(Math.Sqrt(highEnergy / divisor)), Unit(onset), spectrum.Capture());
            lock (gate)
            {
                history[published] = frame;
                published = (published + 1) % HistoryLength;
                count = Math.Min(count + 1, HistoryLength);
            }
            inWindow = 0;
            energy = lowEnergy = midEnergy = highEnergy = peak = 0;
        }
    }

    public void ResetPlayback()
    {
        lock (gate)
        {
            lastNow = double.NaN;
            lastPosition = -1;
            suppressNextOnset = true;
        }
    }

    /// <summary>Non-consuming poll, with process-monotonic seconds and actual output-device position.</summary>
    public AudioFeatureSnapshot Read(double playedSeconds, bool playing, double nowSeconds)
    {
        lock (gate)
        {
            if (!playing || !double.IsFinite(playedSeconds) || playedSeconds < 0 ||
                !double.IsFinite(nowSeconds) || (!double.IsNaN(lastNow) && nowSeconds < lastNow) ||
                (lastPosition >= 0 && playedSeconds + .001 < lastPosition))
            {
                lastNow = double.NaN;
                lastPosition = -1;
                suppressNextOnset = true;
                return AudioFeatureSnapshot.Silent(streamId, false, playedSeconds);
            }
            if (lastPosition < 0 || playedSeconds > lastPosition + .0001) lastAdvance = nowSeconds;
            lastNow = nowSeconds;
            lastPosition = playedSeconds;
            if (nowSeconds - lastAdvance > MaximumStallSeconds) return AudioFeatureSnapshot.Silent(streamId, playing, playedSeconds);
            // Never publish an analysis window still queued ahead of the device cursor.
            var playedFrame = playedSeconds * sampleRate + .0001;
            for (var n = 0; n < count; n++)
            {
                var frame = history[(published - 1 - n + HistoryLength) % HistoryLength];
                if (frame.EndFrame > playedFrame) continue;
                var age = Math.Max(0, playedSeconds - (double)frame.EndFrame / sampleRate);
                if (age > .15) break;
                if (suppressNextOnset) { suppressedSequence = frame.Sequence; suppressNextOnset = false; }
                return new(1, streamId, frame.Sequence, playing, true, age, playedSeconds,
                    frame.Rms, frame.Peak, frame.Low, frame.Mid, frame.High,
                    frame.Sequence == suppressedSequence ? 0 : frame.Onset)
                { Spectrum = frame.Spectrum };
            }
            return AudioFeatureSnapshot.Silent(streamId, playing, playedSeconds);
        }
    }

    private static float Unit(double value) => (float)Math.Clamp(value, 0, 1);

    /// <summary>Single audio-producer scratch. All arrays allocated once at construction.
    /// Per20ms window: channels*N*log2(N)/2 FFT butterflies plus <=N/2+64 band-bin visits
    /// per channel. Nyquist caps frequency range; no bin may borrow energy from the3 crossover bands.</summary>
    private sealed class LogSpectrum
    {
        private readonly int channels, size;
        private readonly float maximumHz;
        private readonly float[] ring;
        private readonly double[] real, imaginary, hann, cosine, sine, bandEnergy;
        private readonly int[] reversed, first, last;
        private readonly double[] firstWeight, lastWeight;
        private readonly double normalization;
        private int cursor;
        public LogSpectrum(int rate, int channels)
        {
            this.channels = channels; size = 1;
            while (size < rate * .04) size <<= 1;
            maximumHz = Math.Min(16000, rate * .5f);
            ring = new float[size * channels]; real = new double[size]; imaginary = new double[size];
            hann = new double[size]; cosine = new double[size / 2]; sine = new double[size / 2]; reversed = new int[size];
            bandEnergy = new double[AudioSpectrum.BandCount]; first = new int[AudioSpectrum.BandCount]; last = new int[AudioSpectrum.BandCount];
            firstWeight = new double[AudioSpectrum.BandCount]; lastWeight = new double[AudioSpectrum.BandCount];
            var bits = System.Numerics.BitOperations.Log2((uint)size); double windowPower = 0;
            for (var i = 0; i < size; i++)
            {
                var at = i; var reverse = 0;
                for (var b = 0; b < bits; b++) { reverse = (reverse << 1) | (at & 1); at >>= 1; }
                reversed[i] = reverse;
                hann[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / size); windowPower += hann[i] * hann[i];
                if (i < size / 2) { cosine[i] = Math.Cos(-2 * Math.PI * i / size); sine[i] = Math.Sin(-2 * Math.PI * i / size); }
            }
            normalization = 1 / (size * windowPower * channels);
            var binHz = (double)rate / size;
            for (var band = 0; band < AudioSpectrum.BandCount; band++)
            {
                var low = 40 * Math.Pow(maximumHz / 40d, (double)band / AudioSpectrum.BandCount);
                var high = 40 * Math.Pow(maximumHz / 40d, (double)(band + 1) / AudioSpectrum.BandCount);
                first[band] = Math.Clamp((int)Math.Floor(low / binHz + .5), 1, size / 2);
                last[band] = Math.Clamp((int)Math.Ceiling(high / binHz + .5) - 1, first[band], size / 2);
                firstWeight[band] = Overlap(first[band], low, high, binHz);
                lastWeight[band] = Overlap(last[band], low, high, binHz);
            }
        }
        private static double Overlap(int bin, double low, double high, double width) =>
            Math.Clamp((Math.Min(high, (bin + .5) * width) - Math.Max(low, (bin - .5) * width)) / width, 0, 1);
        public void PushChannel(int channel, float sample) => ring[channel * size + cursor] = sample;
        public void EndSample() => cursor = (cursor + 1) & (size - 1);
        public void Reset() { Array.Clear(ring); cursor = 0; }
        public AudioSpectrum Capture()
        {
            Array.Clear(bandEnergy);
            for (var channel = 0; channel < channels; channel++)
            {
                for (var i = 0; i < size; i++)
                {
                    var at = reversed[i]; real[at] = ring[channel * size + ((cursor + i) & (size - 1))] * hann[i]; imaginary[at] = 0;
                }
                for (var length = 2; length <= size; length <<= 1)
                {
                    var half = length / 2; var stride = size / length;
                    for (var start = 0; start < size; start += length)
                        for (var j = 0; j < half; j++)
                        {
                            var a = start + j; var b = a + half; var twiddle = j * stride;
                            var re = cosine[twiddle] * real[b] - sine[twiddle] * imaginary[b];
                            var im = sine[twiddle] * real[b] + cosine[twiddle] * imaginary[b];
                            real[b] = real[a] - re; imaginary[b] = imaginary[a] - im; real[a] += re; imaginary[a] += im;
                        }
                }
                for (var band = 0; band < AudioSpectrum.BandCount; band++)
                    for (var bin = first[band]; bin <= last[band]; bin++)
                    {
                        var weight = bin == first[band] ? firstWeight[band] : bin == last[band] ? lastWeight[band] : 1;
                        var symmetry = bin == size / 2 ? 1 : 2;
                        bandEnergy[band] += weight * symmetry * (real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
                    }
            }
            Span<float> values = stackalloc float[AudioSpectrum.BandCount];
            for (var band = 0; band < values.Length; band++) values[band] = Unit(Math.Sqrt(bandEnergy[band] * normalization));
            return new(40, maximumHz, values);
        }
    }
}
