namespace XivPiano.Core;

public enum PauseAction { None, Pause, Resume }

/// <summary>Owns only pauses initiated by active automatic reasons.</summary>
public sealed class AutomaticPause
{
    private bool owned, manualOverride;
    public float Gain { get; private set; } = 1;
    public PauseAction Update(bool requested, bool hasTrack, bool playing, bool deferResume = false, double elapsedSeconds = 0.3)
    {
        if (!hasTrack) { Reset(); return PauseAction.None; }
        var step = double.IsFinite(elapsedSeconds) ? (float)(Math.Clamp(elapsedSeconds, 0, 1) / 0.3) : 0;
        if (manualOverride)
        {
            if (!requested && !deferResume) manualOverride = false;
            Gain = Math.Min(1, Gain + step);
            return PauseAction.None;
        }
        if (requested)
        {
            if (!playing) return PauseAction.None;
            owned = true;
            Gain = Math.Max(0, Gain - step);
            return Gain == 0 ? PauseAction.Pause : PauseAction.None;
        }
        if (deferResume && owned) return PauseAction.None;
        bool resume = owned && !playing;
        owned = false;
        Gain = Math.Min(1, Gain + step);
        return resume ? PauseAction.Resume : PauseAction.None;
    }
    public void ManualChange() { owned = false; manualOverride = true; }
    public void Reset() { owned = false; manualOverride = false; Gain = 1; }
}
