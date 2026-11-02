namespace OpenKamera.Core;

public enum CheckLevel { Good, Warn, Bad }

public sealed record CheckResult(string Item, CheckLevel Level, string Message);

/// <summary>What the app measured just now; anything unknown is null.</summary>
public sealed record CheckInputs(
    bool CameraConnected, double Fps, FaceBox? Face, double? FaceLuma, double? ImageLuma,
    bool MicOpen, double? MicPeak, bool MeetingCameraInstalled, string? CameraHeldBy);

/// <summary>
/// "Am I ready for this call?" in one click: turns measurements into plain-language results with the fix,
/// ordered worst first.
/// </summary>
public static class MeetingCheck
{
    public static IReadOnlyList<CheckResult> Run(CheckInputs i)
    {
        var r = new List<CheckResult>();

        if (!i.CameraConnected)
            r.Add(new("Camera", CheckLevel.Bad, i.CameraHeldBy is { } app ? $"Not connected: {app} is using it. Close it there and press Connect." : "Not connected. Press Connect."));
        else if (i.Fps < 20)
            r.Add(new("Camera", CheckLevel.Warn, $"Only {i.Fps:0} fps. Pick a lower profile or close heavy effects for smoother video."));
        else r.Add(new("Camera", CheckLevel.Good, $"Connected, {i.Fps:0} fps."));

        if (i.CameraConnected)
        {
            if (i.Face is not { } f)
                r.Add(new("Framing", CheckLevel.Warn, "No face found. Sit in front of the camera, or check the lens isn't covered."));
            else
            {
                bool centred = Math.Abs(f.CentreX - 0.5) < 0.18 && f.CentreY is > 0.25 and < 0.6;
                bool size = f.H is > 0.15 and < 0.6;
                r.Add(!centred ? new("Framing", CheckLevel.Warn, "You're off-centre. Turn on Auto framing or move toward the middle.")
                    : !size ? new("Framing", CheckLevel.Warn, f.H <= 0.15 ? "You look small in the picture. Move closer or turn on Smart framing." : "You're very close. Sit back a little.")
                    : new("Framing", CheckLevel.Good, "Nicely framed."));
            }

            if (i.FaceLuma is { } fl)
            {
                double bg = i.ImageLuma ?? fl;
                r.Add(fl < 70 ? new("Light", CheckLevel.Bad, "Your face is dark. Turn on Light or face a window.")
                    : fl + 25 < bg ? new("Light", CheckLevel.Warn, "The background is brighter than your face (backlit). Turn on Light or Spotlight.")
                    : fl > 225 ? new("Light", CheckLevel.Warn, "Your face is very bright. Lower the Light brightness.")
                    : new("Light", CheckLevel.Good, "Your face is well lit."));
            }
        }

        if (!i.MicOpen) r.Add(new("Microphone", CheckLevel.Warn, "Not open in KISSIK. Turn on Microphone in the Audio tab to test it."));
        else if (i.MicPeak is { } p && p < 0.02) r.Add(new("Microphone", CheckLevel.Warn, "Very quiet. Say something, or check the mic isn't muted on the device."));
        else if (i.MicPeak is { } q && q > 0.98) r.Add(new("Microphone", CheckLevel.Warn, "Clipping. Lower the volume or move back from the mic."));
        else r.Add(new("Microphone", CheckLevel.Good, "Hearing you."));

        r.Add(i.MeetingCameraInstalled ? new("Meeting camera", CheckLevel.Good, "Installed. Pick \"KISSIK\" in Zoom, Teams or Meet.")
            : new("Meeting camera", CheckLevel.Bad, "Not installed. Install it from the Meeting camera menu."));

        return r.OrderByDescending(x => x.Level).ToList();
    }

    /// <summary>Average brightness (0..255) of a box in a BGRA frame; box in normalised coords.</summary>
    public static double Luma(byte[] bgra, int w, int h, FaceBox box)
    {
        int x0 = Math.Clamp((int)(box.X * w), 0, w - 1), x1 = Math.Clamp((int)((box.X + box.W) * w), x0 + 1, w);
        int y0 = Math.Clamp((int)(box.Y * h), 0, h - 1), y1 = Math.Clamp((int)((box.Y + box.H) * h), y0 + 1, h);
        long sum = 0, n = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2) { int i = (y * w + x) * 4; sum += (bgra[i + 2] * 77 + bgra[i + 1] * 150 + bgra[i] * 29) >> 8; n++; }
        return n == 0 ? 0 : sum / (double)n;
    }
}
