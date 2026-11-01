namespace OpenKamera.Core;

/// <summary>A capture mode a camera offers.</summary>
public readonly record struct CaptureMode(int Width, int Height, double Fps, string Subtype)
{
    public override string ToString() => $"{Width}×{Height} @ {Fps:0.#} fps {Subtype}";
}

/// <summary>Picks the camera mode that best serves an output profile.</summary>
public static class FormatChooser
{
    /// <summary>
    /// The smallest mode that covers the profile at its frame rate. Vertical profiles are a 9:16 crop of a
    /// landscape frame, so the frame must be at least as tall as the profile. Falls back to the closest
    /// mode when nothing covers it.
    /// </summary>
    public static int Choose(IReadOnlyList<CaptureMode> modes, OutputProfile profile)
    {
        if (modes.Count == 0) return -1;
        // Pixels the camera must deliver so the crop needs no upscaling.
        int needW, needH;
        if (profile.IsVertical) { needH = profile.Height; needW = profile.Height * 9 / 16; }
        else { needW = profile.Width; needH = profile.Height; }

        int best = -1; double bestScore = double.MaxValue;
        for (int i = 0; i < modes.Count; i++)
        {
            var m = modes[i];
            // Landscape cameras: a vertical crop uses the full frame height.
            double coverage = Math.Min((double)m.Width / needW, (double)m.Height / needH);
            double score = 0;
            if (coverage < 1) score += (1 - coverage) * 1000;           // too small: heavy penalty
            else score += (coverage - 1) * 10;                          // bigger than needed: wasted work
            if (m.Fps + 0.5 < profile.Fps) score += (profile.Fps - m.Fps) * 20; // too slow
            else score += (m.Fps - profile.Fps) * 0.5;                  // faster than needed: small cost
            if (m.Subtype is "MJPG") score += 0.3;                      // decode cost vs raw NV12/YUY2
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }
}
