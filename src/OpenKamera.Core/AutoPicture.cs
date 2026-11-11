namespace OpenKamera.Core;

/// <summary>
/// One-press "best picture": measures an unadjusted frame and returns adjustments that fix exposure (face area to a
/// natural mid-tone), colour cast (neutral, kept slightly warm for skin) and flat light. Pure maths; no UI.
/// </summary>
public static class AutoPicture
{
    /// <summary>Face-area brightness to aim for (0..1 after the tone curve).</summary>
    public const double TargetLuma = 0.52;

    /// <summary>The brightest part of the face may reach this, no more, so skin never blows out.</summary>
    public const double HighlightCeiling = 0.9;

    /// <summary>Below this whole-frame brightness the picture is too dark and noisy to judge colour or add detail.</summary>
    public const double DarkFrame = 0.15;

    /// <summary>Darkest tones are pulled no lower than this, and hazy, lifted blacks are brought down to it.</summary>
    public const double BlackTarget = 0.04;

    /// <summary>The brightest things in the room (98th percentile) may reach this, so walls and lamps keep their detail.</summary>
    public const double RoomCeiling = 0.95;

    /// <summary>Most gamma may lift the black point by this much; more reads as a milky, washed-out picture.</summary>
    public const double MaxBlackLift = 0.06;

    public sealed record Result(double Gamma, double Contrast, double Temperature, double Tint, double Sharpness, double Vibrance, string Summary,
        bool TooDark = false, double Brightness = 1.0);

    /// <param name="face">The face found in the frame, if any; without one the usual face area of a webcam shot is used.</param>
    public static Result Analyze(byte[] bgra, int w, int h, FaceBox? face = null)
    {
        int fx0, fx1, fy0, fy1;
        if (face is { } f && f.W > 0.02 && f.H > 0.02)
        {
            fx0 = Math.Clamp((int)(f.X * w), 0, w - 1); fx1 = Math.Clamp((int)((f.X + f.W) * w), fx0 + 1, w);
            fy0 = Math.Clamp((int)(f.Y * h), 0, h - 1); fy1 = Math.Clamp((int)((f.Y + f.H) * h), fy0 + 1, h);
        }
        else { fx0 = w * 3 / 10; fx1 = w * 7 / 10; fy0 = h * 15 / 100; fy1 = h * 60 / 100; }

        double faceSum = 0; long faceN = 0;
        var faceHist = new int[256];
        var allHist = new int[256];
        double rs = 0, gs = 0, bs = 0, allSum = 0; long n = 0, allN = 0;
        int step = Math.Max(1, Math.Min(w, h) / 180); // ~sample grid; plenty for averages
        for (int y = 0; y < h; y += step)
            for (int x = 0; x < w; x += step)
            {
                int i = (y * w + x) * 4;
                double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
                double l = 0.299 * r + 0.587 * g + 0.114 * b;
                allSum += l; allN++; allHist[(int)Math.Round(l * 255)]++;
                if (l > 0.03 && l < 0.97) { rs += r; gs += g; bs += b; n++; } // clipped pixels say nothing about colour
                if (x >= fx0 && x < fx1 && y >= fy0 && y < fy1) { faceSum += l; faceN++; faceHist[(int)Math.Round(l * 255)]++; }
            }
        double m = faceN > 0 ? faceSum / faceN : 0.5;
        double frame = allN > 0 ? allSum / allN : 0.5;
        bool dark = frame < DarkFrame;
        double black = Percentile(allHist, allN, 0.02);   // the picture's black point
        double p95 = Percentile(faceHist, faceN, 0.95);    // the bright part of the face
        double bright = Percentile(allHist, allN, 0.98);   // the brightest things in the room

        var (brightness, contrast, gamma) = Exposure(m, black, p95, bright, dark);

        // Colour: gray-world, corrected by 60% so warm rooms stay a little warm (skin looks healthier). In a dark,
        // noisy frame the averages are mostly sensor noise, so the correction fades out instead of guessing a cast.
        double trust = Math.Clamp((frame - DarkFrame) / 0.15, 0, 1) * 0.6;
        double R = n > 0 ? rs / n : 0.5, G = n > 0 ? gs / n : 0.5, B = n > 0 ? bs / n : 0.5;
        double temp = Math.Clamp((B - R) / 0.2 * trust, -1, 1);                 // Temperature adds ±0.1 to R and B
        double tint = Math.Clamp((G - (R + B) / 2) / 0.15 * trust, -1, 1);      // Tint moves G against R and B
        if (Math.Abs(temp) < 0.05) temp = 0;
        if (Math.Abs(tint) < 0.05) tint = 0;

        // Sharpening and vibrance amplify noise, so a dark frame gets neither.
        double sharpness = dark ? 0 : 0.25, vibrance = dark ? 0 : 0.12;

        double faceAfter = ToneOf(m, brightness, contrast, gamma);
        var notes = new List<string>();
        if (faceAfter > m + 0.03) notes.Add("brightened your face"); else if (faceAfter < m - 0.03) notes.Add("toned down the glare");
        if (temp > 0) notes.Add("warmed a cold cast"); else if (temp < 0) notes.Add("cooled a warm cast");
        if (tint != 0) notes.Add(tint > 0 ? "removed a green tint" : "removed a pink tint");
        if (contrast > 1.05) notes.Add("added depth to flat light");
        string summary = dark
            ? (notes.Count == 0 ? "It's too dark to fix by adjusting the picture." : "Best picture: " + string.Join(", ", notes) + ".")
              + " Turn on Light, or put a lamp in front of you, for a cleaner picture."
            : notes.Count == 0 ? "Your picture already looks balanced; added a touch of clarity." : "Best picture: " + string.Join(", ", notes) + ".";
        return new Result(gamma, contrast, Math.Round(temp, 2), Math.Round(tint, 2), sharpness, vibrance, summary, dark, brightness);
    }

    /// <summary>
    /// Brightness, contrast and gamma that take the face to <see cref="TargetLuma"/>. Most of the lift is a straight line
    /// anchored at the black point (blacks stay dark, hazy blacks are pulled down), so the picture keeps its depth; gamma
    /// only finishes the job, and never by enough to turn the shadows milky. The bright part of the face stays under
    /// <see cref="HighlightCeiling"/>. A dark, noisy frame gets a plain gain with no contrast stretch.
    /// </summary>
    private static (double Brightness, double Contrast, double Gamma) Exposure(double m, double black, double hi, double room, bool dark)
    {
        if (m is <= 0.02 or >= 0.98) return (1, 1, 1);
        // A wall or lamp that was not clipped before must not clip after. A window that already was can't be saved,
        // and must not stop the face from being brightened.
        bool guardRoom = room < HighlightCeiling;

        // A line y = bt + a·(x − s): the black point s goes to bt, the face m to the target.
        bool stretch = !dark && m - black >= 0.1;
        double s = stretch ? black : 0, bt = stretch ? Math.Min(black, BlackTarget) : 0;
        double a = (TargetLuma - bt) / (m - s);
        if (a > 1 && hi > m) a = Math.Max(1, Math.Min(a, (HighlightCeiling - bt) / (hi - s))); // keep skin from blowing out
        if (a > 1 && guardRoom && room > s) a = Math.Max(1, Math.Min(a, (RoomCeiling - bt) / (room - s)));
        if (s < 0.5) a = Math.Min(a, (2 - 2 * bt) / (1 - 2 * s));                               // Brightness tops out at 2
        a = Math.Clamp(a, 0.5, 4);

        // The tone curve is ((x − ½)·c + ½)·k, so c·k = a and k·(1 − c)/2 = bt − a·s.
        double k = Math.Clamp(a + 2 * (bt - a * s), 0.2, 2);
        double c = Math.Clamp(a / k, 0.2, 2);

        // Gamma closes any gap that is left, within limits.
        double mid = Math.Clamp(((m - 0.5) * c + 0.5) * k, 0.01, 0.99);
        double g = Math.Log(mid) / Math.Log(TargetLuma);
        double low = Math.Max(Math.Clamp(((black - 0.5) * c + 0.5) * k, 0, 1), 0.005);
        if (g > 1) g = Math.Min(g, Math.Log(low) / Math.Log(Math.Min(0.99, low + MaxBlackLift)));     // shadows stay put
        double high = Math.Clamp(((hi - 0.5) * c + 0.5) * k, 0, 1);
        if (g > 1 && high is > 0.02 and < HighlightCeiling) g = Math.Min(g, Math.Log(high) / Math.Log(HighlightCeiling));
        else if (g > 1 && high >= HighlightCeiling) g = 1;
        double roomAfter = Math.Clamp(((room - 0.5) * c + 0.5) * k, 0, 1);
        if (g > 1 && guardRoom && roomAfter is > 0.02 and < RoomCeiling) g = Math.Min(g, Math.Log(roomAfter) / Math.Log(RoomCeiling));
        else if (g > 1 && guardRoom && roomAfter >= RoomCeiling) g = 1;
        g = Math.Clamp(g, 0.8, 1.6);

        k = Math.Round(k, 2); c = Math.Round(c, 2); g = Math.Round(g, 2);
        if (Math.Abs(k - 1) < 0.03) k = 1;
        if (Math.Abs(c - 1) < 0.03) c = 1;
        if (Math.Abs(g - 1) < 0.05) g = 1;
        return (k, c, g);
    }

    /// <summary>Where a luma value lands after the tone curve (same maths as ImageFilters).</summary>
    private static double ToneOf(double x, double brightness, double contrast, double gamma)
    {
        double y = Math.Clamp(((x - 0.5) * contrast + 0.5) * brightness, 0, 1);
        return gamma != 1 ? Math.Pow(y, 1 / gamma) : y;
    }

    private static double Percentile(int[] hist, long count, double p)
    {
        if (count <= 0) return 0;
        long want = (long)Math.Ceiling(count * p), seen = 0;
        for (int v = 0; v < hist.Length; v++) { seen += hist[v]; if (seen >= want) return v / 255.0; }
        return 1;
    }
}
