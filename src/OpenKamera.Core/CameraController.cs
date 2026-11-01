using Windows.Media.Devices;

namespace OpenKamera.Core;

/// <summary>
/// Hardware camera controls through VideoDeviceController, which Windows maps to the
/// standard UVC CameraControl / VideoProcAmp property sets (IKsControl::KsProperty).
/// Ranges always come from the device.
/// </summary>
public sealed class CameraController(VideoDeviceController device)
{
    private (string Name, MediaDeviceControl? Control)[] Map() =>
    [
        ("Brightness", Safe(() => device.Brightness)),
        ("Contrast", Safe(() => device.Contrast)),
        ("Hue", Safe(() => device.Hue)),
        ("Exposure", Safe(() => device.Exposure)),
        ("Focus", Safe(() => device.Focus)),
        ("Zoom", Safe(() => device.Zoom)),
        ("Pan", Safe(() => device.Pan)),
        ("Tilt", Safe(() => device.Tilt)),
        ("WhiteBalance", Safe(() => device.WhiteBalance)),
        ("BacklightCompensation", Safe(() => device.BacklightCompensation)),
    ];

    private static MediaDeviceControl? Safe(Func<MediaDeviceControl?> get)
    {
        try { return get(); } catch { return null; }
    }

    public IReadOnlyList<CameraProperty> GetProperties()
    {
        var list = new List<CameraProperty>();
        foreach (var (name, control) in Map())
        {
            if (control?.Capabilities is not { } cap || !cap.Supported) continue;
            control.TryGetValue(out double value);
            bool canAuto = control.TryGetAuto(out bool a);
            list.Add(new CameraProperty(name, cap.Min, cap.Max, cap.Step,
                cap.Default, value, canAuto, canAuto && a));
        }
        return list;
    }

    public bool TrySet(string name, double value, bool auto)
    {
        var control = Map().FirstOrDefault(m => m.Name == name).Control;
        if (control is null) return false;
        if (auto) return control.TrySetAuto(true);
        return control.TrySetValue(GetProperties().First(p => p.Name == name).Clamp(value));
    }

    /// <summary>One bidirectional drive tick for Focus/Zoom; returns the applied value.</summary>
    public double? Drive(string name, int slider)
    {
        var prop = GetProperties().FirstOrDefault(p => p.Name == name);
        if (prop is null || slider == 0) return prop?.Value;
        double next = DriveMath.DriveTarget(prop, slider);
        return TrySet(name, next, auto: false) ? next : prop.Value;
    }
}
