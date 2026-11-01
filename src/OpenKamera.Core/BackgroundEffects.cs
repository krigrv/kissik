namespace OpenKamera.Core;

public enum BackgroundMode { Off, Blur, Replace, Remove }

/// <param name="BlurStrength">0..1: low = portrait-style softness, high = privacy blur.</param>
/// <param name="Spotlight">0..1: darkens the room and lifts the person. 0 = off.</param>
/// <param name="Retouch">0..1: skin smoothing on the person. 0 = off.</param>
/// <param name="BackgroundId">Id from <see cref="BackgroundLibrary"/> for Replace.</param>
public sealed record BackgroundSettings(
    BackgroundMode Mode = BackgroundMode.Off, double BlurStrength = 0.5,
    double Spotlight = 0, double Retouch = 0, string? BackgroundId = null,
    SegmentationQuality Quality = SegmentationQuality.Fast)
{
    public static readonly BackgroundSettings Off = new();
    public bool NeedsMask => Mode != BackgroundMode.Off || Spotlight > 0 || Retouch > 0;
}

/// <summary>Composites a frame against its person mask. All buffers are BGRA32, premultiplied.</summary>
public static class BackgroundEffects
{
    public static void Apply(byte[] px, int w, int h, float[] alpha, BackgroundSettings s, byte[]? background = null)
    {
        if (s.Retouch > 0) Retouch(px, w, h, alpha, (float)s.Retouch);

        switch (s.Mode)
        {
            case BackgroundMode.Blur:
                {
                    int radius = 2 + (int)Math.Round(s.BlurStrength * 14); // on a 1/4-scale image
                    var (blur, bw, bh) = MaskedBlur(px, w, h, alpha, radius);
                    Composite(px, w, h, alpha, (x, y) => SampleQuarter(blur, bw, bh, x, y, w, h));
                    break;
                }
            case BackgroundMode.Replace when background is { } bg && bg.Length == px.Length:
                Composite(px, w, h, alpha, (x, y) => { int i = (y * w + x) * 4; return (bg[i], bg[i + 1], bg[i + 2]); });
                break;
            case BackgroundMode.Remove:
                Parallel.For(0, h, y =>
                {
                    for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
                    {
                        float a = alpha[y * w + x];
                        px[i] = (byte)(px[i] * a); px[i + 1] = (byte)(px[i + 1] * a); px[i + 2] = (byte)(px[i + 2] * a);
                        px[i + 3] = (byte)(a * 255);
                    }
                });
                break;
        }

        if (s.Spotlight > 0) Spotlight(px, w, h, alpha, (float)s.Spotlight);
    }

    private static void Composite(byte[] px, int w, int h, float[] alpha, Func<int, int, (byte B, byte G, byte R)> bg)
    {
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
            {
                float a = alpha[y * w + x];
                if (a >= 0.999f) continue;
                var (b, g, r) = bg(x, y);
                px[i] = (byte)(px[i] * a + b * (1 - a));
                px[i + 1] = (byte)(px[i + 1] * a + g * (1 - a));
                px[i + 2] = (byte)(px[i + 2] * a + r * (1 - a));
                px[i + 3] = 255;
            }
        });
    }

    /// <summary>
    /// Background-only blur at quarter scale. Each pixel is weighted by (1 − alpha) so the person
    /// does not bleed into the blurred room (no halo).
    /// </summary>
    internal static (float[] Rgb, int W, int H) MaskedBlur(byte[] px, int w, int h, float[] alpha, int radius)
    {
        int bw = Math.Max(1, w / 4), bh = Math.Max(1, h / 4);
        var acc = new float[bw * bh * 4]; // b, g, r, weight
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                float sb = 0, sg = 0, sr = 0, sw = 0;
                for (int yy = 0; yy < 4; yy++)
                {
                    int sy = Math.Min(h - 1, y * 4 + yy);
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int sx = Math.Min(w - 1, x * 4 + xx);
                        int si = (sy * w + sx) * 4;
                        float wt = 1 - alpha[sy * w + sx] + 0.02f;
                        sb += px[si] * wt; sg += px[si + 1] * wt; sr += px[si + 2] * wt; sw += wt;
                    }
                }
                int di = (y * bw + x) * 4;
                acc[di] = sb; acc[di + 1] = sg; acc[di + 2] = sr; acc[di + 3] = sw;
            }
        for (int pass = 0; pass < 3; pass++) { BoxH(acc, bw, bh, radius); BoxV(acc, bw, bh, radius); }
        var rgb = new float[bw * bh * 3];
        for (int i = 0, j = 0; i < acc.Length; i += 4, j += 3)
        {
            float wt = Math.Max(1e-4f, acc[i + 3]);
            rgb[j] = acc[i] / wt; rgb[j + 1] = acc[i + 1] / wt; rgb[j + 2] = acc[i + 2] / wt;
        }
        return (rgb, bw, bh);
    }

    private static void BoxH(float[] a, int w, int h, int r)
    {
        Parallel.For(0, h, y =>
        {
            var row = new float[w * 4];
            Array.Copy(a, y * w * 4, row, 0, w * 4);
            for (int c = 0; c < 4; c++)
            {
                float sum = 0; int n = 0;
                for (int x = -r; x <= r; x++) { int xx = Math.Clamp(x, 0, w - 1); sum += row[xx * 4 + c]; n++; }
                for (int x = 0; x < w; x++)
                {
                    a[(y * w + x) * 4 + c] = sum / n;
                    int add = Math.Min(w - 1, x + r + 1), rem = Math.Max(0, x - r);
                    sum += row[add * 4 + c] - row[rem * 4 + c];
                }
            }
        });
    }

    private static void BoxV(float[] a, int w, int h, int r)
    {
        Parallel.For(0, w, x =>
        {
            var col = new float[h * 4];
            for (int y = 0; y < h; y++) for (int c = 0; c < 4; c++) col[y * 4 + c] = a[(y * w + x) * 4 + c];
            for (int c = 0; c < 4; c++)
            {
                float sum = 0; int n = 0;
                for (int y = -r; y <= r; y++) { int yy = Math.Clamp(y, 0, h - 1); sum += col[yy * 4 + c]; n++; }
                for (int y = 0; y < h; y++)
                {
                    a[(y * w + x) * 4 + c] = sum / n;
                    int add = Math.Min(h - 1, y + r + 1), rem = Math.Max(0, y - r);
                    sum += col[add * 4 + c] - col[rem * 4 + c];
                }
            }
        });
    }

    private static (byte, byte, byte) SampleQuarter(float[] rgb, int bw, int bh, int x, int y, int w, int h)
    {
        float fx = Math.Clamp((x + 0.5f) * bw / w - 0.5f, 0, bw - 1), fy = Math.Clamp((y + 0.5f) * bh / h - 0.5f, 0, bh - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, bw - 1), y1 = Math.Min(y0 + 1, bh - 1);
        float dx = fx - x0, dy = fy - y0;
        byte Ch(int c)
        {
            float a = rgb[(y0 * bw + x0) * 3 + c] * (1 - dx) + rgb[(y0 * bw + x1) * 3 + c] * dx;
            float b = rgb[(y1 * bw + x0) * 3 + c] * (1 - dx) + rgb[(y1 * bw + x1) * 3 + c] * dx;
            return (byte)Math.Clamp(a * (1 - dy) + b * dy, 0, 255);
        }
        return (Ch(0), Ch(1), Ch(2));
    }

    private static void Spotlight(byte[] px, int w, int h, float[] alpha, float s)
    {
        float dim = 1 - 0.55f * s, lift = 1 + 0.12f * s;
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
            {
                float a = alpha[y * w + x];
                float k = dim + (lift - dim) * a;
                px[i] = Sat(px[i] * k); px[i + 1] = Sat(px[i + 1] * k); px[i + 2] = Sat(px[i + 2] * k);
            }
        });
    }

    /// <summary>Smooths skin-toned pixels on the person only, keeping edges and eyes sharp.</summary>
    private static void Retouch(byte[] px, int w, int h, float[] alpha, float amount)
    {
        var src = (byte[])px.Clone();
        const int r = 3;
        Parallel.For(r, h - r, y =>
        {
            for (int x = r; x < w - r; x++)
            {
                int i = (y * w + x) * 4;
                float a = alpha[y * w + x];
                if (a < 0.3f) continue;
                int B = src[i], G = src[i + 1], R = src[i + 2];
                if (!IsSkin(R, G, B)) continue;
                // Edge-aware average: only neighbours with a similar tone contribute.
                float sb = 0, sg = 0, sr = 0, sw = 0;
                for (int dy = -r; dy <= r; dy += 1)
                    for (int dx = -r; dx <= r; dx += 1)
                    {
                        int j = ((y + dy) * w + x + dx) * 4;
                        int diff = Math.Abs(src[j] - B) + Math.Abs(src[j + 1] - G) + Math.Abs(src[j + 2] - R);
                        if (diff > 40) continue;
                        float wt = 1f - diff / 48f;
                        sb += src[j] * wt; sg += src[j + 1] * wt; sr += src[j + 2] * wt; sw += wt;
                    }
                float k = amount * a * 0.85f;
                px[i] = Sat(B + (sb / sw - B) * k); px[i + 1] = Sat(G + (sg / sw - G) * k); px[i + 2] = Sat(R + (sr / sw - R) * k);
            }
        });
    }

    /// <summary>Broad YCbCr skin range covering light to dark skin tones.</summary>
    internal static bool IsSkin(int r, int g, int b)
    {
        float cb = 128 - 0.168736f * r - 0.331264f * g + 0.5f * b;
        float cr = 128 + 0.5f * r - 0.418688f * g - 0.081312f * b;
        return cb is >= 77 and <= 135 && cr is >= 133 and <= 180;
    }

    private static byte Sat(float v) => (byte)Math.Clamp(v, 0, 255);
}
