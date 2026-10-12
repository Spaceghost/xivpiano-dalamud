namespace XivPiano.Core.Tests;

public class AutomaticPauseTests
{
    [Fact]
    public void ManualOverridePreventsRePauseAndUnwantedResumeUntilReasonsEnd()
    {
        var p = new AutomaticPause();
        p.Update(true, true, true);
        p.ManualChange();
        Assert.Equal(PauseAction.None, p.Update(true, true, true));
        Assert.Equal(1f, p.Gain);
        Assert.Equal(PauseAction.None, p.Update(false, true, false, deferResume: true));
        Assert.Equal(PauseAction.None, p.Update(true, true, false));
        Assert.Equal(PauseAction.None, p.Update(false, true, false));
        Assert.Equal(PauseAction.None, p.Update(false, true, false));
        Assert.Equal(PauseAction.Pause, p.Update(true, true, true));
    }

    [Fact]
    public void FadesBeforePausingAndCanReverseAnInterruptedFade()
    {
        var p = new AutomaticPause();
        Assert.Equal(PauseAction.None, p.Update(true, true, true, elapsedSeconds: 0.15));
        Assert.Equal(0.5f, p.Gain, 4);
        Assert.Equal(PauseAction.None, p.Update(false, true, true, elapsedSeconds: 0.15));
        Assert.Equal(1f, p.Gain);
        Assert.Equal(PauseAction.Pause, p.Update(true, true, true, elapsedSeconds: 0.3));
        Assert.Equal(0f, p.Gain);
        Assert.Equal(PauseAction.Resume, p.Update(false, true, false, elapsedSeconds: 0.15));
        Assert.Equal(0.5f, p.Gain, 4);
        p.Update(false, true, true, elapsedSeconds: 0.15);
        Assert.Equal(1f, p.Gain);
    }

    [Fact]
    public void LoadingDefersOwnedResumeWithoutPausingOrdinaryPlayback()
    {
        var p = new AutomaticPause();
        Assert.Equal(PauseAction.None, p.Update(false, true, true, deferResume: true));
        Assert.Equal(PauseAction.Pause, p.Update(true, true, true));
        Assert.Equal(PauseAction.None, p.Update(false, true, false, deferResume: true));
        Assert.Equal(PauseAction.None, p.Update(false, true, false, deferResume: true));
        Assert.Equal(PauseAction.None, p.Update(true, true, false));
        Assert.Equal(PauseAction.Resume, p.Update(false, true, false));
        Assert.Equal(PauseAction.None, p.Update(false, true, true));
        p.Update(true, true, true);
        p.Update(false, false, false, deferResume: true);
        Assert.Equal(PauseAction.None, p.Update(false, true, false));
    }

    [Fact]
    public void OverlappingDutyAndCutsceneReasonsDoNotResumeEachOther()
    {
        var p = new AutomaticPause();
        Assert.Equal(PauseAction.Pause, p.Update(true, true, true));
        Assert.Equal(PauseAction.None, p.Update(true, true, false));
        Assert.Equal(PauseAction.None, p.Update(true, true, false));
        Assert.Equal(PauseAction.Resume, p.Update(false, true, false));
        Assert.Equal(PauseAction.None, p.Update(false, true, true));
    }
    [Fact]
    public void PreexistingManualPauseAndStoppedTrackAreNotResumed()
    {
        var p = new AutomaticPause();
        Assert.Equal(PauseAction.None, p.Update(true, true, false));
        Assert.Equal(PauseAction.None, p.Update(false, true, false));
        Assert.Equal(PauseAction.Pause, p.Update(true, true, true));
        Assert.Equal(PauseAction.None, p.Update(true, false, false));
        Assert.Equal(PauseAction.None, p.Update(false, true, false));
        p.Update(true, true, true); p.Reset();
        Assert.Equal(PauseAction.None, p.Update(false, true, false));
    }
}
