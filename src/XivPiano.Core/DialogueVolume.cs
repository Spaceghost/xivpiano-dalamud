namespace XivPiano.Core;

/// <summary>A time-based attenuation, separate from the user's volume.</summary>
public sealed class DialogueVolume
{
    private double attenuation = 1;
    public float Update(float volume, bool enabled, bool talking, float duckTo, double elapsedSeconds)
    {
        volume = float.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0.6f;
        duckTo = float.IsFinite(duckTo) ? Math.Clamp(duckTo, 0, 1) : 0.25f;
        var target = enabled && talking ? duckTo : 1;
        var step = double.IsFinite(elapsedSeconds) ? Math.Clamp(elapsedSeconds, 0, 1) / 0.3 : 0;
        attenuation += Math.Clamp(target - attenuation, -step, step);
        return volume * (float)attenuation;
    }
}
