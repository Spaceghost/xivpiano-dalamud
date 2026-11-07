using Xunit;
namespace XivPiano.Core.Tests;

public sealed class SongRhythmTests
{
    [Fact] public void FindsFuturePulsesWithoutDependingOnAudioCallbackHistory()
    {
        const int rate = 8000;
        var analyzer = new SongRhythmAnalysis(rate,2);
        for (var second = 0; second < 20; second++) {
            var pcm = new float[rate * 2];
            for (var i = 0; i < rate; i++) {
                var local = (double)(i % (rate/2))/rate;
                var sample = local < .06 ? .7f * (float)Math.Sin(Math.Tau * 160 * i/rate) : 0;
                pcm[i*2] = sample; pcm[i*2+1] = -sample;
            }
            analyzer.Push(pcm);
        }
        var pulses = analyzer.Finish();
        Assert.InRange(pulses.Length,35,42);
        Assert.All(pulses, p => Assert.True(double.IsFinite(p.Seconds) && p.Strength > 0));
        var ahead = SongRhythmAnalysis.Read(7,true,2,20,pulses,16);
        Assert.True(ahead.Ready);
        Assert.Contains(ahead.Pulses,p => p.Seconds > 15);
        Assert.All(ahead.Pulses,p => Assert.InRange(p.Seconds,2,18));
    }
    [Fact] public void SilenceMalformedSamplesAndPausedPlaybackRemainBounded()
    {
        var a = new SongRhythmAnalysis(8000,1);
        a.Push(new float[8000]); a.Push(Enumerable.Repeat(float.NaN,8000).ToArray());
        Assert.Empty(a.Finish());
        Assert.Throws<ArgumentException>(() => a.Push(new float[8001]));
        var paused = SongRhythmAnalysis.Read(2,false,double.NaN,double.NaN,null,double.PositiveInfinity);
        Assert.False(paused.Playing); Assert.False(paused.Ready); Assert.Empty(paused.Pulses);
        Assert.Equal(16,paused.LookAheadSeconds);
        Assert.Equal(30,SongRhythmAnalysis.Read(2,true,0,20,[],999).LookAheadSeconds);
    }
}
