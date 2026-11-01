namespace OpenKamera.Core;

public sealed record CameraDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record CameraProperty(
    string Name, double Min, double Max, double Step, double Default,
    double Value, bool CanAuto, bool Auto, bool Supported = true)
{
    public double Clamp(double value) =>
        Math.Round(Math.Clamp(value, Min, Max) / (Step == 0 ? 1 : Step)) * (Step == 0 ? 1 : Step);
}

public sealed record OutputProfile(string Name, int Width, int Height, int Fps)
{
    public static readonly OutputProfile P720 = new("Meetings", 1280, 720, 30);
    public static readonly OutputProfile P1080 = new("Meetings HD", 1920, 1080, 30);
    public static readonly OutputProfile P1080x60 = new("Streaming", 1920, 1080, 60);
    public static readonly OutputProfile P1440 = new("Meetings 1440p", 2560, 1440, 30);
    public static readonly OutputProfile P2160 = new("4K", 3840, 2160, 30);
    public static readonly OutputProfile Vertical = new("Vertical", 720, 1280, 30);
    public static readonly OutputProfile VerticalHd = new("Vertical HD", 1080, 1920, 30);
    public static IReadOnlyList<OutputProfile> All { get; } = [P720, P1080, P1080x60, P1440, P2160, Vertical, VerticalHd];

    public bool IsVertical => Height > Width;
    public string Aspect => IsVertical ? "9:16" : "16:9";
    /// <summary>e.g. "1080p 16:9 at 30 FPS"; vertical profiles name the long edge.</summary>
    public string Details => $"{Height}p {Aspect} at {Fps} FPS";
    /// <summary>Two-line pill in the command bar, e.g. "720p" / "30FPS".</summary>
    public string PillTop => $"{Height}p";
    public string PillBottom => $"{Fps}FPS";
    public override string ToString() => Name;
}

/// <summary>Centre-split bidirectional drive math for lens sliders (centre = stop, edges = full speed).</summary>
public static class DriveMath
{
    /// <summary>Slider -100..100 split around centre: negative drives down, positive up.</summary>
    public static (double Down, double Up) Split(int slider)
    {
        slider = Math.Clamp(slider, -100, 100);
        return slider < 0 ? (-slider, 0) : (0, slider);
    }

    /// <summary>Next absolute value when a drive amount (0..100) is applied for one tick.</summary>
    public static double DriveTarget(CameraProperty property, int slider, double tickFraction = 0.05)
    {
        var (down, up) = Split(slider);
        double range = property.Max - property.Min;
        double next = property.Value + (up - down) / 100.0 * range * tickFraction;
        return property.Clamp(next);
    }
}
