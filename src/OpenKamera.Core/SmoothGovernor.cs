namespace OpenKamera.Core;

/// <summary>
/// Smooth-first mode: watches how long effects take per frame against the frame budget and steps effect quality
/// down when the video would stutter, then back up once there is headroom. Level 0 = full quality,
/// 1 = lighter background edges, 2 = also skips stabilisation and face tracking, 3 = also processes at half size
/// (meetings get at most 1080p anyway; not used while recording, since a recording keeps one size).
/// </summary>
public sealed class SmoothGovernor
{
    public const int MaxLevel = 3;
    private DateTime _overSince = DateTime.MaxValue, _underSince = DateTime.MaxValue;

    public int Level { get; private set; }
    public TimeSpan StepDownAfter { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan StepUpAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Feeds the smoothed effects time; returns true when the level changed.</summary>
    public bool Update(double effectsMs, int targetFps, DateTime now)
    {
        double budget = 1000.0 / Math.Max(1, targetFps);
        bool over = effectsMs > budget * 0.85, under = effectsMs < budget * 0.45;
        if (over) { _underSince = DateTime.MaxValue; if (_overSince == DateTime.MaxValue) _overSince = now; }
        else if (under) { _overSince = DateTime.MaxValue; if (_underSince == DateTime.MaxValue) _underSince = now; }
        else { _overSince = _underSince = DateTime.MaxValue; }

        if (over && Level < MaxLevel && now - _overSince >= StepDownAfter) { Level++; _overSince = DateTime.MaxValue; return true; }
        if (under && Level > 0 && now - _underSince >= StepUpAfter) { Level--; _underSince = DateTime.MaxValue; return true; }
        return false;
    }

    public void Reset() { Level = 0; _overSince = _underSince = DateTime.MaxValue; }
}
