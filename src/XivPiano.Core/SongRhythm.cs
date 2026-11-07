namespace XivPiano.Core;

/// <summary>Anonymous song-relative onsets. No PCM, URLs or account data cross IPC.</summary>
public sealed record SongPulse(double Seconds, float Strength, float Bass);
public sealed record SongRhythmTimeline(int Version, long StreamId, bool Playing,
    double PositionSeconds, double DurationSeconds, bool Ready, double LookAheadSeconds,
    SongPulse[] Pulses);

/// <summary>Bounded, streaming 20ms energy/onset analysis of a separately decoded song.
/// This is a chart aid, not pitch recognition or a claim of perfect musical beat detection.</summary>
public sealed class SongRhythmAnalysis
{
    public const double MaximumSeconds = 12 * 60;
    private readonly int rate, channels, window;
    private readonly double alpha;
    private readonly double[] low;
    private readonly List<SongPulse> pulses = [];
    private int frames;
    private long total;
    private double energy, bassEnergy, previous, floor = .01, lastPulse = -1;
    public SongRhythmAnalysis(int sampleRate, int channelCount)
    {
        if (sampleRate is < 8000 or > 192000 || channelCount is < 1 or > 8) throw new ArgumentOutOfRangeException();
        rate = sampleRate; channels = channelCount; window = sampleRate / 50;
        low = new double[channels]; alpha = 1 - Math.Exp(-Math.Tau * 240 / rate);
    }
    public void Push(ReadOnlySpan<float> samples)
    {
        if (samples.Length % channels != 0 || samples.Length > rate * channels) throw new ArgumentException("Expected at most one second of interleaved PCM");
        for (var frame = 0; frame < samples.Length && total < rate * MaximumSeconds; frame += channels)
        {
            for (var c = 0; c < channels; c++)
            {
                var value = float.IsFinite(samples[frame + c]) ? Math.Clamp(samples[frame + c], -1, 1) : 0;
                low[c] += alpha * (value - low[c]); energy += value * value; bassEnergy += low[c] * low[c];
            }
            total++; frames++;
            if (frames < window) continue;
            var rms = Math.Sqrt(energy / (frames * channels));
            var bass = Math.Sqrt(bassEnergy / (frames * channels));
            var strength = Math.Clamp((rms - previous - .006) / (floor + .025), 0, 1);
            var time = (double)total / rate;
            if (strength >= .30 && rms >= .015 && time - lastPulse >= .22)
            {
                pulses.Add(new(Math.Max(0, time - .01), (float)strength, (float)Math.Clamp(bass / (rms + .001), 0, 1)));
                lastPulse = time;
            }
            previous = rms; floor += (rms - floor) * .025;
            frames = 0; energy = bassEnergy = 0;
        }
    }
    public SongPulse[] Finish() => pulses.ToArray();
    public static SongRhythmTimeline Read(long stream, bool playing, double position, double duration,
        SongPulse[]? all, double lookAhead)
    {
        position = double.IsFinite(position) ? Math.Max(0, position) : 0;
        lookAhead = double.IsFinite(lookAhead) ? Math.Clamp(lookAhead, 1, 30) : 16;
        var end = position + lookAhead;
        return new(1, stream, playing, position, Math.Max(0, double.IsFinite(duration) ? duration : 0), all != null, lookAhead,
            all == null ? [] : all.Where(p => p.Seconds >= position && p.Seconds <= end).Take(128).ToArray());
    }
}
