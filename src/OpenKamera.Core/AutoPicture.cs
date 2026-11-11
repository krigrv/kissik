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

    public sealed record Result(double Gamma, double Contrast, double Temperature, double Tint, double Sharpness, double Vibrance, string Summary, bool TooDark = false);

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

        double faceSum = 0, faceSq = 0; long faceN = 0;
        var faceHist = new int[256];
        double rs = 0, gs = 0, bs = 0, allSum = 0; long n = 0, allN = 0;
        int step = Math.Max(1, Math.Min(w, h) / 180); // ~sample grid; plenty for averages
        for (int y = 0; y < h; y += step)
            for (int x = 0; x < w; x += step)
            {
                int i = (y * w + x) * 4;
                double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
                double l = 0.299 * r + 0.587 * g + 0.114 * b;
                allSum += l; allN++;
                if (l > 0.03 && l < 0.97) { rs += r; gs += g; bs += b; n++; } // clipped pixels say nothing about colour
                if (x >= fx0 && x < fx1 && y >= fy0 && y < fy1) { faceSum += l; faceSq += l * l; faceN++; faceHist[(int)Math.Round(l * 255)]++; }
            }
        double m = faceN > 0 ? faceSum / faceN : 0.5, sd = faceN > 0 ? Math.Sqrt(Math.Max(0, faceSq / faceN - m * m)) : 0.2;
        double frame = allN > 0 ? allSum / allN : 0.5;
        bool dark = frame < DarkFrame;

        // Exposure through gamma (moves mid-tones, keeps blacks and whites): m^(1/g) = target.
        double gamma = m is > 0.02 and < 0.98 ? Math.Log(m) / Math.Log(TargetLuma) : 1;
        // Never lift the bright part of the face (95th percentile) past the ceiling: that is what blows skin out.
        double p95 = Percentile(faceHist, faceN, 0.95);
        if (gamma > 1 && p95 is > 0.02 and < HighlightCeiling) gamma = Math.Min(gamma, Math.Log(p95) / Math.Log(HighlightCeiling));
        gamma = Math.Clamp(gamma, 0.6, 2.2);
        if (Math.Abs(gamma - 1) < 0.05) gamma = 1;

        // Colour: gray-world, corrected by 60% so warm rooms stay a little warm (skin looks healthier). In a dark,
        // noisy frame the averages are mostly sensor noise, so the correction fades out instead of guessing a cast.
        double trust = Math.Clamp((frame - DarkFrame) / 0.15, 0, 1) * 0.6;
        double R = n > 0 ? rs / n : 0.5, G = n > 0 ? gs / n : 0.5, B = n > 0 ? bs / n : 0.5;
        double temp = Math.Clamp((B - R) / 0.2 * trust, -1, 1);                 // Temperature adds ±0.1 to R and B
        double tint = Math.Clamp((G - (R + B) / 2) / 0.15 * trust, -1, 1);      // Tint moves G against R and B
        if (Math.Abs(temp) < 0.05) temp = 0;
        if (Math.Abs(tint) < 0.05) tint = 0;

        // Contrast, sharpening and vibrance amplify noise, so a dark frame gets none of them.
        double contrast = dark ? 1.0 : sd < 0.12 ? 1.12 : sd < 0.18 ? 1.05 : 1.0;  // lift flat, hazy light
        double sharpness = dark ? 0 : 0.25, vibrance = dark ? 0 : 0.12;

        var notes = new List<string>();
        if (gamma > 1) notes.Add("brightened your face"); else if (gamma < 1) notes.Add("toned down the glare");
        if (temp > 0) notes.Add("warmed a cold cast"); else if (temp < 0) notes.Add("cooled a warm cast");
        if (tint != 0) notes.Add(tint > 0 ? "removed a green tint" : "removed a pink tint");
        if (contrast > 1) notes.Add("added depth to flat light");
        string summary = dark
            ? (notes.Count == 0 ? "It's too dark to fix by adjusting the picture." : "Best picture: " + string.Join(", ", notes) + ".")
              + " Turn on Light, or put a lamp in front of you, for a cleaner picture."
            : notes.Count == 0 ? "Your picture already looks balanced; added a touch of clarity." : "Best picture: " + string.Join(", ", notes) + ".";
        return new Result(Math.Round(gamma, 2), contrast, Math.Round(temp, 2), Math.Round(tint, 2), sharpness, vibrance, summary, dark);
    }

    private static double Percentile(int[] hist, long count, double p)
    {
        if (count <= 0) return 0;
        long want = (long)Math.Ceiling(count * p), seen = 0;
        for (int v = 0; v < hist.Length; v++) { seen += hist[v]; if (seen >= want) return v / 255.0; }
        return 1;
    }
}
