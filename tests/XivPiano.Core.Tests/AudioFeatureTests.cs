using System.Text.Json;

namespace XivPiano.Core.Tests;

public sealed class AudioFeatureTests
{
    private const int Rate = 48000;
    private static float[] Tone(double frequency, double seconds, float amplitude = .5f, int channels = 1, bool opposite = false)
    {
        var pcm = new float[(int)(Rate * seconds) * channels];
        for (var i = 0; i < pcm.Length / channels; i++)
            for (var c = 0; c < channels; c++)
                pcm[i * channels + c] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / Rate) * (opposite && c % 2 != 0 ? -1 : 1);
        return pcm;
    }

    private static void Zero(AudioFeatureSnapshot frame)
    {
        Assert.Equal(0, frame.Rms);
        Assert.Equal(0, frame.Peak);
        Assert.Equal(0, frame.Low);
        Assert.Equal(0, frame.Mid);
        Assert.Equal(0, frame.High);
        Assert.Equal(0, frame.Onset);
        if (frame.Spectrum is { } spectrum)
            for (var i = 0; i < AudioSpectrum.BandCount; i++) Assert.Equal(0, spectrum[i]);
    }

    [Fact]
    public void SilenceIsARealFreshZeroMeasurement()
    {
        var analyzer = new AudioFeatures(Rate, 2, 42);
        analyzer.Push(new float[Rate / 5]);
        var frame = analyzer.Read(.1, true, 1);
        Assert.True(frame.Fresh);
        Assert.True(frame.Playing);
        Assert.Equal(42, frame.StreamId);
        Assert.Equal(5, frame.Sequence);
        Zero(frame);
    }

    [Fact]
    public void CausalHardwareCursorDoesNotPublishQueuedMusicEarly()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        analyzer.Push(new float[Rate / 10]);
        analyzer.Push(Tone(900, .2));
        Zero(analyzer.Read(.1, true, 1));
        Assert.False(analyzer.Read(.01, true, 2).Fresh); // backwards cursor is rejected
        var frame = analyzer.Read(.2, true, 3);
        Assert.True(frame.Fresh);
        Assert.Equal(10, frame.Sequence);
        Assert.InRange(frame.Rms, .35f, .36f);
        Assert.InRange(frame.Peak, .499f, .501f);
    }

    [Theory]
    [InlineData(80, 0)]
    [InlineData(800, 1)]
    [InlineData(7000, 2)]
    public void CrossoverBandsDistinguishLowMiddleAndHighTones(double frequency, int dominant)
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        analyzer.Push(Tone(frequency, .3));
        var frame = analyzer.Read(.3, true, 1);
        float[] bands = [frame.Low, frame.Mid, frame.High];
        for (var i = 0; i < bands.Length; i++)
            if (i != dominant) Assert.True(bands[dominant] > bands[i] * 1.25f);
    }

    [Fact]
    public void AntiPhaseStereoDoesNotDisappear()
    {
        var mono = new AudioFeatures(Rate, 1, 1);
        var stereo = new AudioFeatures(Rate, 2, 2);
        mono.Push(Tone(800, .1));
        stereo.Push(Tone(800, .1, channels: 2, opposite: true));
        var a = mono.Read(.1, true, 1);
        var b = stereo.Read(.1, true, 1);
        Assert.Equal(a.Rms, b.Rms, 5);
        Assert.Equal(a.Mid, b.Mid, 5);
    }

    [Fact]
    public void PostVolumeAmplitudeScalesFeaturesAndMuteIsSilent()
    {
        var loud = new AudioFeatures(Rate, 1, 1);
        var quiet = new AudioFeatures(Rate, 1, 2);
        var muted = new AudioFeatures(Rate, 1, 3);
        loud.Push(Tone(800, .1, .8f));
        quiet.Push(Tone(800, .1, .2f));
        muted.Push(Tone(800, .1, 0));
        Assert.Equal(loud.Read(.1, true, 1).Rms / 4, quiet.Read(.1, true, 1).Rms, 5);
        Zero(muted.Read(.1, true, 1));
    }

    [Fact]
    public void PositivePulseProducesShortOnsetAndSilenceDecaysIt()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        analyzer.Push(new float[Rate / 5]);
        analyzer.Push(Tone(100, .02, .8f));
        analyzer.Push(new float[Rate / 5]);
        Assert.Equal(0, analyzer.Read(.2, true, 1).Onset);
        var pulse = analyzer.Read(.22, true, 1.02);
        Assert.True(pulse.Onset > .8f);
        Assert.InRange(analyzer.Read(.42, true, 1.22).Onset, 0, .1f);
    }

    [Fact]
    public void PauseStopAndResumeDoNotLeakAnOldOnset()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        analyzer.Push(Tone(100, .2));
        Assert.True(analyzer.Read(.02, true, 1).Fresh);
        var paused = analyzer.Read(.02, false, 2);
        Assert.False(paused.Playing);
        Assert.False(paused.Fresh);
        Zero(paused);
        Assert.Equal(0, analyzer.Read(.02, true, 3).Onset);
        Assert.Equal(0, analyzer.Read(.02, true, 3.01).Onset);
        analyzer.ResetPlayback();
        Assert.Equal(0, analyzer.Read(.04, true, 4).Onset);
        Zero(AudioFeatureSnapshot.Silent(2, false));
    }

    [Fact]
    public void StalledCursorAndMissingHistoryGoInactive()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        analyzer.Push(Tone(100, .2));
        Assert.True(analyzer.Read(.1, true, 1).Fresh);
        var stalled = analyzer.Read(.1, true, 1.21);
        Assert.False(stalled.Fresh);
        Zero(stalled);
        Assert.False(analyzer.Read(1, true, 2).Fresh);
    }

    [Fact]
    public void InvalidClockPositionAndSamplesNeverProduceNonFiniteJson()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        var pcm = Tone(100, .1);
        pcm[0] = float.NaN;
        pcm[1] = float.PositiveInfinity;
        pcm[2] = float.NegativeInfinity;
        analyzer.Push(pcm);
        var valid = analyzer.Read(.1, true, 1);
        Assert.True(valid.Fresh);
        Assert.NotEmpty(JsonSerializer.Serialize(valid));
        Zero(analyzer.Read(double.NaN, true, 2));
        Zero(analyzer.Read(.1, true, double.PositiveInfinity));
        analyzer.Read(.1, true, 5);
        Zero(analyzer.Read(.11, true, 4));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(192001, 2)]
    [InlineData(48000, 0)]
    [InlineData(48000, 9)]
    public void UnsupportedFormatsAreExplicit(int rate, int channels) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFeatures(rate, channels, 1));

    [Fact]
    public void OversizedCallsDiscardHistoryAndCannotReplayOldFeatures()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        analyzer.Push(Tone(100, .1));
        analyzer.Push(new float[Rate + 1]);
        Assert.False(analyzer.Read(.1, true, 1).Fresh);
    }

    [Fact]
    public void HistoryIsBoundedAndPollingDoesNotConsumeFrames()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        var pcm = Tone(100, 1);
        for (var i = 0; i < 5; i++) analyzer.Push(pcm);
        Assert.False(analyzer.Read(.1, true, 1).Fresh);
        var a = analyzer.Read(5, true, 2);
        var b = analyzer.Read(5, true, 2);
        Assert.Equal(a, b);
        Assert.Equal(250, a.Sequence);
    }

    [Fact]
    public void BufferPartitionDoesNotChangeMeasurements()
    {
        var whole = new AudioFeatures(Rate, 2, 1);
        var split = new AudioFeatures(Rate, 2, 1);
        var pcm = Tone(800, .2, channels: 2);
        whole.Push(pcm);
        for (var i = 0; i < pcm.Length; i += 242) split.Push(pcm.AsSpan(i, Math.Min(242, pcm.Length - i)));
        Assert.Equal(whole.Read(.2, true, 1), split.Read(.2, true, 1));
    }

    [Fact]
    public void SteadyAudioPushAndPollAllocateNoManagedMemory()
    {
        var analyzer = new AudioFeatures(Rate, 2, 1);
        var pcm = Tone(800, .02, channels: 2);
        analyzer.Push(pcm);
        analyzer.Read(.02, true, 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= 100; i++)
        {
            analyzer.Push(pcm);
            analyzer.Read((i + 1) * .02, true, 1 + i * .02);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void JsonContractIsVersionedAnonymousAndPreservesInt64Generation()
    {
        const long generation = 1L << 40;
        var analyzer = new AudioFeatures(Rate, 1, generation);
        analyzer.Push(Tone(100, .1));
        var json = JsonSerializer.Serialize(analyzer.Read(.1, true, 1),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(generation, root.GetProperty("streamId").GetInt64());
        Assert.Equal(5, root.GetProperty("sequence").GetInt64());
        Assert.Equal(14, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("title", out _));
        Assert.False(root.TryGetProperty("audioUrl", out _));
        Assert.False(root.TryGetProperty("token", out _));
        foreach (var name in new[] { "rms", "peak", "low", "mid", "high", "onset" })
            Assert.InRange(root.GetProperty(name).GetSingle(), 0, 1);
        var spectrum = root.GetProperty("spectrum");
        Assert.Equal(40, spectrum.GetProperty("minimumHz").GetSingle());
        Assert.Equal(16000, spectrum.GetProperty("maximumHz").GetSingle());
        Assert.Equal(32, spectrum.GetProperty("bands").GetArrayLength());
        foreach (var band in spectrum.GetProperty("bands").EnumerateArray()) Assert.InRange(band.GetSingle(), 0, 1);
        // An older parser using only the original fields remains valid.
        var legacy = JsonSerializer.Deserialize<LegacySnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal(1, legacy.Version); Assert.Equal(generation, legacy.StreamId); Assert.True(legacy.Fresh);
    }

    private readonly record struct LegacySnapshot(int Version, long StreamId, long Sequence, bool Playing, bool Fresh, float Rms);
    private static AudioSpectrum Spectrum(AudioFeatureSnapshot frame) => Assert.IsType<AudioSpectrum>(frame.Spectrum);
    private static int Dominant(AudioSpectrum value)
    {
        var best = 0;
        for (var i = 1; i < AudioSpectrum.BandCount; i++) if (value[i] > value[best]) best = i;
        return best;
    }
    private static double Power(AudioSpectrum value)
    {
        double sum = 0;
        for (var i = 0; i < AudioSpectrum.BandCount; i++) sum += value[i] * value[i];
        return sum;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(3000)]
    [InlineData(10000)]
    public void ActualLogSpectrumLocatesIndependentFrequenciesAndConservesAudiblePower(double frequency)
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        analyzer.Push(Tone(frequency, .3, .6f));
        var spectrum = Spectrum(analyzer.Read(.3, true, 1));
        var expected = (int)(Math.Log(frequency / spectrum.MinimumHz) / Math.Log(spectrum.MaximumHz / spectrum.MinimumHz) * 32);
        Assert.InRange(Dominant(spectrum), expected - 1, expected + 1);
        Assert.InRange(Power(spectrum), .176, .184); // .6-amplitude sine has .18 mean-square power.
    }

    [Fact]
    public void TwoTonesWithinOneOldCrossoverBandHaveDistinctSpectralPeaks()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        var pcm = Tone(500, .2, .3f); var second = Tone(1500, .2, .3f);
        for (var i = 0; i < pcm.Length; i++) pcm[i] += second[i];
        analyzer.Push(pcm);
        var spectrum = Spectrum(analyzer.Read(.2, true, 1));
        var firstBin = (int)(Math.Log(500 / 40d) / Math.Log(400) * 32);
        var secondBin = (int)(Math.Log(1500 / 40d) / Math.Log(400) * 32);
        Assert.True(spectrum[firstBin] > .15f);
        Assert.True(spectrum[secondBin] > .15f);
        Assert.True(spectrum[(firstBin + secondBin) / 2] < .01f);
    }

    [Fact]
    public void SpectrumRespectsGainAntiphaseAndFreshSilence()
    {
        var loud = new AudioFeatures(Rate, 1, 1); var quiet = new AudioFeatures(Rate, 2, 2);
        loud.Push(Tone(800, .2, .8f)); quiet.Push(Tone(800, .2, .2f, 2, true));
        var a = Spectrum(loud.Read(.2, true, 1)); var b = Spectrum(quiet.Read(.2, true, 1));
        for (var i = 0; i < 32; i++) Assert.Equal(a[i] / 4, b[i], 6);
        quiet.Push(new float[Rate / 5 * 2]);
        var silent = quiet.Read(.4, true, 1.2);
        Assert.True(silent.Fresh); Assert.Equal(0, silent.Rms); Assert.Equal(0, Power(Spectrum(silent)));
        Assert.InRange(silent.Onset, 0, .02f); // Existing positive onset envelope decays independently.
    }

    [Fact]
    public void SpectrumUsesPlayedHistoryNotQueuedDecodeAndReturnedValueSurvivesOverwrite()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1);
        analyzer.Push(Tone(500, .2)); var old = analyzer.Read(.2, true, 1);
        var saved = Spectrum(old); var copy = saved;
        analyzer.Push(Tone(7000, .2));
        Assert.Equal(saved, Spectrum(analyzer.Read(.2, true, 1.01)));
        Assert.NotEqual(Dominant(saved), Dominant(Spectrum(analyzer.Read(.4, true, 1.2))));
        var pcm = Tone(1200, 1);
        for (var n = 0; n < 4; n++) analyzer.Push(pcm);
        Assert.Equal(copy, old.Spectrum); // History ring has wrapped but no mutable array escaped.
    }

    [Fact]
    public void PausedStaleInvalidAndMalformedSpectrumIsUnavailableNotAnOldArray()
    {
        var analyzer = new AudioFeatures(Rate, 1, 1); analyzer.Push(Tone(800, .2));
        Assert.NotNull(analyzer.Read(.1, true, 1).Spectrum);
        Assert.Null(analyzer.Read(.1, false, 2).Spectrum);
        Assert.NotNull(analyzer.Read(.1, true, 3).Spectrum);
        Assert.Null(analyzer.Read(.1, true, 3.21).Spectrum);
        Assert.Null(analyzer.Read(double.NaN, true, 4).Spectrum);
        analyzer.Push(new float[Rate + 1]);
        Assert.Null(analyzer.Read(.2, true, 5).Spectrum);
        var json = JsonSerializer.Serialize(AudioFeatureSnapshot.Silent(1, false), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("spectrum", out _));
        Assert.Equal(13, document.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(8000, 1)]
    [InlineData(44100, 2)]
    [InlineData(192000, 8)]
    public void SpectrumFormatBoundsNyquistFiniteAndMultichannelSafe(int rate, int channels)
    {
        var analyzer = new AudioFeatures(rate, channels, 1);
        var pcm = new float[rate / 10 * channels];
        for (var i = 0; i < pcm.Length / channels; i++)
            for (var c = 0; c < channels; c++) pcm[i * channels + c] = .5f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate) * (c % 2 == 0 ? 1 : -1);
        analyzer.Push(pcm); var spectrum = Spectrum(analyzer.Read(.1, true, 1));
        Assert.Equal(Math.Min(16000, rate * .5f), spectrum.MaximumHz);
        for (var i = 0; i < 32; i++) Assert.InRange(spectrum[i], 0, 1);
        Assert.InRange(Power(spectrum), .12, .13);
    }
}
