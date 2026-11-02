namespace OpenKamera.Core;

/// <summary>Built-in colour looks (Enhance → Filters).</summary>
// New looks are added at the end so saved choices keep their meaning.
public enum FilterPreset { None, BlackAndWhite, Warm, Cool, Bleach, Cinematic, Vivid, Fade, GoldenHour, TealOrange, FilmPrint, Punchy, Noir, Pastel }

/// <summary>Software image adjustments on BGRA32 pixel data (Enhance panel).</summary>
public static class ImageFilters
{
    /// <param name="Hue">Degrees, −180..180.</param>
    /// <param name="Vibrance">−1..1; boosts low-saturation colours more than saturated ones.</param>
    /// <param name="Temperature">−1 (cool) .. 1 (warm).</param>
    /// <param name="Tint">−1 (green) .. 1 (magenta).</param>
    /// <param name="Sharpness">0..2, unsharp-mask amount.</param>
    public sealed record Adjustments(
        double Brightness = 1.0, double Contrast = 1.0,
        double Saturation = 1.0, double Sharpness = 0.0,
        double Gamma = 1.0, double Hue = 0.0, double Vibrance = 0.0,
        double Temperature = 0.0, double Tint = 0.0,
        FilterPreset Filter = FilterPreset.None,
        Lut3D? Lut = null, double LutStrength = 1.0)
    {
        public static readonly Adjustments Neutral = new();
        public bool IsNeutral =>
            Brightness == 1.0 && Contrast == 1.0 && Saturation == 1.0 && Sharpness == 0.0 &&
            Gamma == 1.0 && Hue == 0.0 && Vibrance == 0.0 && Temperature == 0.0 && Tint == 0.0 &&
            Filter == FilterPreset.None && (Lut is null || LutStrength <= 0);
        internal bool NeedsColour => this with { Sharpness = 0.0 } is { IsNeutral: false };
    }

    /// <summary>Colour adjustments only (no sharpening). Kept for callers without dimensions.</summary>
    public static void Apply(Span<byte> bgra, Adjustments adj)
    {
        if (!adj.NeedsColour) return;
        var tone = BuildToneCurve(adj);
        for (int i = 0; i + 3 < bgra.Length; i += 4) ApplyPixel(bgra.Slice(i, 4), adj, tone);
    }

    /// <summary>Full chain: tone, colour, filter, LUT, then sharpening.</summary>
    public static void Apply(byte[] bgra, int width, int height, Adjustments adj)
    {
        if (adj.IsNeutral) return;
        if (adj.NeedsColour)
        {
            var tone = BuildToneCurve(adj);
            int stride = width * 4;
            Parallel.For(0, height, y =>
            {
                var row = bgra.AsSpan(y * stride, stride);
                for (int x = 0; x < stride; x += 4) ApplyPixel(row.Slice(x, 4), adj, tone);
            });
        }
        if (adj.Sharpness > 0) Sharpen(bgra, width, height, adj.Sharpness);
    }

    /// <summary>Brightness, contrast and gamma folded into one 256-entry curve.</summary>
    internal static float[] BuildToneCurve(Adjustments adj)
    {
        var curve = new float[256];
        for (int v = 0; v < 256; v++)
        {
            double x = ((v / 255.0 - 0.5) * adj.Contrast + 0.5) * adj.Brightness;
            x = Math.Clamp(x, 0, 1);
            if (adj.Gamma != 1.0) x = Math.Pow(x, 1.0 / adj.Gamma);
            curve[v] = (float)x;
        }
        return curve;
    }

    private static void ApplyPixel(Span<byte> px, Adjustments adj, float[] tone)
    {
        float b = tone[px[0]], g = tone[px[1]], r = tone[px[2]];

        if (adj.Temperature != 0) { float t = (float)adj.Temperature * 0.1f; r += t; b -= t; }
        if (adj.Tint != 0) { float t = (float)adj.Tint * 0.1f; g -= t; r += t * 0.5f; b += t * 0.5f; }

        if (adj.Hue != 0) RotateHue(ref r, ref g, ref b, adj.Hue);

        float gray = 0.299f * r + 0.587f * g + 0.114f * b;
        float sat = (float)adj.Saturation;
        if (adj.Vibrance != 0)
        {
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            sat *= 1f + (float)adj.Vibrance * (1f - (max - min));
        }
        if (sat != 1f) { r = gray + (r - gray) * sat; g = gray + (g - gray) * sat; b = gray + (b - gray) * sat; }

        if (adj.Filter != FilterPreset.None) ApplyPreset(adj.Filter, ref r, ref g, ref b);

        if (adj.Lut is { } lut && adj.LutStrength > 0)
        {
            var (lr, lg, lb) = lut.Sample(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
            float s = (float)Math.Clamp(adj.LutStrength, 0, 1);
            r += (lr - r) * s; g += (lg - g) * s; b += (lb - b) * s;
        }

        px[0] = ToByte(b); px[1] = ToByte(g); px[2] = ToByte(r);
    }

    private static void RotateHue(ref float r, ref float g, ref float b, double degrees)
    {
        var m = HueMatrix(degrees);
        float nr = r * m[0] + g * m[1] + b * m[2], ng = r * m[3] + g * m[4] + b * m[5], nb = r * m[6] + g * m[7] + b * m[8];
        r = nr; g = ng; b = nb;
    }

    /// <summary>Row-major 3×3 rotation about the grey axis in RGB space.</summary>
    internal static float[] HueMatrix(double degrees)
    {
        double a = degrees * Math.PI / 180.0;
        float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
        const float k = 1f / 3f; float sq = MathF.Sqrt(k);
        return [c + (1 - c) * k, k * (1 - c) - sq * s, k * (1 - c) + sq * s,
                k * (1 - c) + sq * s, c + k * (1 - c), k * (1 - c) - sq * s,
                k * (1 - c) - sq * s, k * (1 - c) + sq * s, c + k * (1 - c)];
    }

    private static void ApplyPreset(FilterPreset f, ref float r, ref float g, ref float b)
    {
        float gray = 0.299f * r + 0.587f * g + 0.114f * b;
        switch (f)
        {
            case FilterPreset.BlackAndWhite:
                r = g = b = Curve(gray, 1.15f); break;
            case FilterPreset.Warm:
                r = r * 1.08f + 0.02f; g *= 1.02f; b *= 0.88f; break;
            case FilterPreset.Cool:
                r *= 0.9f; g *= 1.0f; b = b * 1.1f + 0.02f; break;
            case FilterPreset.Bleach:
                // Bleach bypass: blend toward high-contrast luminance, desaturated.
                float l = Curve(gray, 1.4f);
                r = r * 0.45f + l * 0.55f; g = g * 0.45f + l * 0.55f; b = b * 0.45f + l * 0.55f; break;
            case FilterPreset.Cinematic:
                // Teal shadows, warm highlights.
                r = Curve(r, 1.15f) + (gray - 0.5f) * 0.08f;
                b = Curve(b, 1.15f) - (gray - 0.5f) * 0.12f;
                g = Curve(g, 1.1f); break;
            case FilterPreset.Vivid:
                r = gray + (r - gray) * 1.35f; g = gray + (g - gray) * 1.35f; b = gray + (b - gray) * 1.35f;
                r = Curve(r, 1.1f); g = Curve(g, 1.1f); b = Curve(b, 1.1f); break;
            case FilterPreset.GoldenHour:
                // Late-afternoon sun: warm, slightly lifted shadows, gentle contrast.
                r = Curve(r * 1.1f + 0.03f, 1.05f); g = Curve(g * 1.03f + 0.015f, 1.05f); b = Curve(b * 0.85f, 1.05f); break;
            case FilterPreset.TealOrange:
                // Skin pushed toward orange, shadows and blues toward teal.
                float t = gray - 0.5f;
                r = r + t * 0.10f + 0.02f; g = g + 0.01f; b = b - t * 0.16f;
                r = Curve(r, 1.08f); g = Curve(g, 1.05f); b = Curve(b, 1.08f); break;
            case FilterPreset.FilmPrint:
                // Soft roll-off, warm mids, cool blacks: a print-film feel.
                r = 0.06f + r * 0.9f; g = 0.05f + g * 0.88f; b = 0.08f + b * 0.82f;
                r = gray + (r - gray) * 0.9f; g = gray + (g - gray) * 0.9f; b = gray + (b - gray) * 0.9f; break;
            case FilterPreset.Punchy:
                r = Curve(gray + (r - gray) * 1.2f, 1.22f); g = Curve(gray + (g - gray) * 1.2f, 1.22f); b = Curve(gray + (b - gray) * 1.2f, 1.22f); break;
            case FilterPreset.Noir:
                float n = Curve(gray, 1.45f) - 0.03f; r = g = b = n; break;
            case FilterPreset.Pastel:
                r = 0.15f + r * 0.8f; g = 0.15f + g * 0.8f; b = 0.17f + b * 0.8f;
                r = gray + (r - gray) * 0.7f; g = gray + (g - gray) * 0.7f; b = gray + (b - gray) * 0.7f; break;
            case FilterPreset.Fade:
                r = r * 0.82f + 0.1f; g = g * 0.82f + 0.1f; b = b * 0.82f + 0.12f;
                r = gray + (r - gray) * 0.8f; g = gray + (g - gray) * 0.8f; b = gray + (b - gray) * 0.8f; break;
        }
    }

    private static float Curve(float v, float contrast) => (v - 0.5f) * contrast + 0.5f;

    /// <summary>3×3 unsharp mask on the colour channels.</summary>
    private static void Sharpen(byte[] bgra, int width, int height, double amount)
    {
        if (width < 3 || height < 3) return;
        var src = (byte[])bgra.Clone();
        int stride = width * 4;
        float k = (float)amount;
        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * stride + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    int blur = (src[i - stride + c] + src[i + stride + c] + src[i - 4 + c] + src[i + 4 + c]) >> 2;
                    bgra[i + c] = ToByte((src[i + c] + (src[i + c] - blur) * k) / 255f);
                }
            }
        });
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);
}
