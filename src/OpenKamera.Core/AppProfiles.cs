namespace OpenKamera.Core;

/// <summary>
/// Per-app profiles: "when Zoom opens the camera, use my Client calls preset". Watches which apps hold a camera and
/// reports the preset to apply when a linked app starts using it. The newest app to start wins.
/// </summary>
public sealed class AppProfileWatcher
{
    private HashSet<string> _previous = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>App name → preset name.</summary>
    public Dictionary<string, string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Feeds the apps using a camera now; returns (app, preset) when a linked app has just started.</summary>
    public (string App, string Preset)? Update(IEnumerable<string> appsUsingCamera)
    {
        var now = new HashSet<string>(appsUsingCamera, StringComparer.OrdinalIgnoreCase);
        (string, string)? hit = null;
        foreach (var app in now)
            if (!_previous.Contains(app) && Links.TryGetValue(app, out var preset)) hit = (app, preset);
        _previous = now;
        return hit;
    }
}
