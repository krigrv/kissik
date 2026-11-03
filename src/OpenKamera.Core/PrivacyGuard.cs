namespace OpenKamera.Core;

/// <summary>
/// Privacy guard: when someone else appears in the shot (a second face) for a moment, hide the picture;
/// show it again once you're alone for a little longer. Debounced so a poster or a glitch doesn't flicker it.
/// </summary>
public sealed class PrivacyGuard
{
    private DateTime _crowdSince = DateTime.MaxValue, _aloneSince = DateTime.MaxValue;

    public TimeSpan TriggerAfter { get; init; } = TimeSpan.FromSeconds(0.8);
    public TimeSpan ReleaseAfter { get; init; } = TimeSpan.FromSeconds(3);
    public bool Hiding { get; private set; }

    /// <summary>Feeds the latest face count; returns true when <see cref="Hiding"/> changed.</summary>
    public bool Update(int faces, DateTime now)
    {
        if (faces >= 2) { _aloneSince = DateTime.MaxValue; if (_crowdSince == DateTime.MaxValue) _crowdSince = now; }
        else { _crowdSince = DateTime.MaxValue; if (_aloneSince == DateTime.MaxValue) _aloneSince = now; }
        if (!Hiding && now - _crowdSince >= TriggerAfter) { Hiding = true; return true; }
        if (Hiding && now - _aloneSince >= ReleaseAfter) { Hiding = false; return true; }
        return false;
    }

    public void Reset() { Hiding = false; _crowdSince = _aloneSince = DateTime.MaxValue; }
}
