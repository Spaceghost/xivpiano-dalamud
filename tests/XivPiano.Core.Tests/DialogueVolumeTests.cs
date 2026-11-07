namespace XivPiano.Core.Tests;

public class DialogueVolumeTests
{
    [Fact]
    public void RampsAndRespectsVolumeChangesWithoutRestoringStaleSliderValue()
    {
        var p = new DialogueVolume();
        Assert.Equal(0.6f, p.Update(0.6f, false, true, 0.25f, 1), 4);
        Assert.Equal(0.3f, p.Update(0.6f, true, true, 0.25f, 0.15), 4);
        Assert.Equal(0.15f, p.Update(0.6f, true, true, 0.25f, 0.15), 4);
        Assert.Equal(0.05f, p.Update(0.2f, true, true, 0.25f, 0), 4);
        Assert.Equal(0.2f, p.Update(0.2f, true, false, 0.25f, 0.3), 4);
        Assert.Equal(0.05f, p.Update(0.2f, true, true, 0.25f, 0.3), 4);
        Assert.Equal(0.2f, p.Update(0.2f, false, true, 0.25f, 0.3), 4);
    }
    [Fact]
    public void RejectsNonfiniteAndNegativeTimingWithoutInvalidAudioGain()
    {
        var p = new DialogueVolume();
        Assert.Equal(0.6f, p.Update(float.NaN, true, true, float.NaN, -1), 4);
        Assert.Equal(0.6f, p.Update(float.NaN, true, true, float.NaN, double.NaN), 4);
        Assert.Equal(0.15f, p.Update(float.NaN, true, true, float.NaN, 1), 4);
    }
}
