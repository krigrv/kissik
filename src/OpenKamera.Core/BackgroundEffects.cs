namespace OpenKamera.Core;

public enum BackgroundMode { Off, Blur, Replace, Remove }

/// <param name="BlurStrength">0..1: low = portrait-style softness, high = privacy blur.</param>
/// <param name="Spotlight">0..1: darkens the room and lifts the person. 0 = off.</param>
/// <param name="Retouch">0..1: skin smoothing on the person. 0 = off.</param>
/// <param name="BackgroundId">Id from <see cref="BackgroundLibrary"/> for Replace.</param>
/// <param name="GreenScreen">Use a physical green/blue screen (chroma key) instead of AI segmentation.</param>
public sealed record BackgroundSettings(
    BackgroundMode Mode = BackgroundMode.Off, double BlurStrength = 0.5,
    double Spotlight = 0, double Retouch = 0, string? BackgroundId = null,
    SegmentationQuality Quality = SegmentationQuality.Fast,
    bool GreenScreen = false, string KeyColor = "#00B140", double Similarity = 0.4, double Smoothness = 0.1, double Spill = 0.5)
{
    public static readonly BackgroundSettings Off = new();
    public bool NeedsMask => Mode != BackgroundMode.Off || Spotlight > 0 || Retouch > 0;
}

/// <summary>Composites a frame against its person mask. All buffers are BGRA32, premultiplied.</summary>
public static class BackgroundEffects
{
    /// <summary>How much background colour spills onto the person's edge (0 = pasted-on look).</summary>
    public const float LightWrap = 0.35f;

    public static void Apply(byte[] px, int w, int h, float[] alpha, BackgroundSettings s, byte[]? background = null)
    {
        if (s.Retouch > 0) Retouch(px, w, h, alpha, (float)s.Retouch);

        if (s.Mode is BackgroundMode.Blur or BackgroundMode.Replace or BackgroundMode.Remove)
        {
            // The room right behind the edge, so edge pixels can drop it (unmixing) instead of carrying it onto the
            // new background as a halo. Blur also needs the big blurred room; both come from one masked downsample.
            int radius = s.Mode == BackgroundMode.Blur ? 2 + (int)Math.Round(s.BlurStrength * 14) : 0; // on a 1/4-scale image
            var (person, room, blur, bw, bh) = MaskedBlur(px, w, h, alpha, radius);
            byte[]? bg = s.Mode == BackgroundMode.Replace && background is { } r && r.Length == px.Length ? r : null;
            if (s.Mode != BackgroundMode.Replace || bg is not null) Composite(px, w, h, alpha, person, room, blur, bw, bh, bg, s.Mode == BackgroundMode.Remove);
        }

        if (s.Spotlight > 0) Spotlight(px, w, h, alpha, (float)s.Spotlight);
    }

    /// <summary>
    /// Puts the person over the new background. An edge pixel of the camera is a·F + (1 − a)·room, so blending the raw
    /// pixel drags the old room along the outline as a fringe. The person colour F is estimated instead (blur-fusion,
    /// Germer et al. 2020): F = F̄ + a·(I − a·F̄ − (1 − a)·R̄) from the nearby person F̄ and nearby room R̄; the extra
    /// factor a keeps the room's texture out of faint hair. Room light still wraps the edge a little (LightWrap). The quarter-size grids are sampled bilinearly, inline: a per-pixel delegate made
    /// this the slowest step at 4K. Background: <paramref name="blur"/> (Blur), <paramref name="bg"/> (Replace), or
    /// nothing with a premultiplied alpha output (<paramref name="remove"/>).
    /// </summary>
    private static void Composite(byte[] px, int w, int h, float[] alpha, float[] person, float[] room, float[]? blur, int bw, int bh, byte[]? bg, bool remove)
    {
        float sx = (float)bw / w, sy = (float)bh / h;
        Parallel.For(0, h, y =>
        {
            float fy = Math.Clamp((y + 0.5f) * sy - 0.5f, 0, bh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, bh - 1); float dy = fy - y0;
            int r0 = y0 * bw * 3, r1 = y1 * bw * 3;
            for (int x = 0, i = y * w * 4, ai = y * w; x < w; x++, i += 4, ai++)
            {
                float a = alpha[ai];
                if (a >= 0.999f) { if (remove) px[i + 3] = 255; continue; }
                float fx = Math.Clamp((x + 0.5f) * sx - 0.5f, 0, bw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, bw - 1); float dx = fx - x0;
                int p00 = r0 + x0 * 3, p01 = r0 + x1 * 3, p10 = r1 + x0 * 3, p11 = r1 + x1 * 3;
                float ia = 1 - a;
                for (int c = 0; c < 3; c++)
                {
                    float f = Bilerp(person, p00 + c, p01 + c, p10 + c, p11 + c, dx, dy);
                    f += a * (px[i + c] - a * f - ia * Bilerp(room, p00 + c, p01 + c, p10 + c, p11 + c, dx, dy));
                    float aF = a * Math.Clamp(f, 0, 255);
                    if (remove) { px[i + c] = (byte)aF; continue; }
                    float b = bg is not null ? bg[i + c] : Bilerp(blur!, p00 + c, p01 + c, p10 + c, p11 + c, dx, dy);
                    float fg = aF + (a * b - aF) * LightWrap * ia; // room light spills onto the edge, like a real shot
                    px[i + c] = (byte)Math.Clamp(fg + b * ia, 0, 255);
                }
                px[i + 3] = remove ? (byte)(a * 255) : (byte)255;
            }
        });
    }

    private static float Bilerp(float[] g, int p00, int p01, int p10, int p11, float dx, float dy)
    {
        float top = g[p00] + (g[p01] - g[p00]) * dx, bot = g[p10] + (g[p11] - g[p10]) * dx;
        return top + (bot - top) * dy;
    }

    /// <summary>Box radius (on the 1/4-scale grid, three passes) of the nearby person / room colours used for unmixing.</summary>
    internal const int NearRadius = 2;

    /// <summary>
    /// Masked blurs at quarter scale. Room pixels are weighted by (1 − alpha) so the person does not bleed into the
    /// room (no halo), person pixels by alpha. Returns the nearby person and room colours (small radius) and, when
    /// <paramref name="radius"/> &gt; 0, the blurred room.
    /// </summary>
    internal static (float[] Person, float[] Room, float[]? Blur, int W, int H) MaskedBlur(byte[] px, int w, int h, float[] alpha, int radius)
    {
        int bw = Math.Max(1, w / 4), bh = Math.Max(1, h / 4);
        var acc = new float[bw * bh * 4]; // b, g, r, weight
        var fg = new float[bw * bh * 4];
        Parallel.For(0, bh, y =>
        {
            for (int x = 0; x < bw; x++)
            {
                float sb = 0, sg = 0, sr = 0, sw = 0, pb = 0, pg = 0, pr = 0, pw = 0;
                for (int yy = 0; yy < 4; yy++)
                {
                    int sy = Math.Min(h - 1, y * 4 + yy);
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int sx = Math.Min(w - 1, x * 4 + xx);
                        int si = (sy * w + sx) * 4;
                        float wt = 1 - alpha[sy * w + sx] + 0.02f;
                        sb += px[si] * wt; sg += px[si + 1] * wt; sr += px[si + 2] * wt; sw += wt;
                        float pt = alpha[sy * w + sx] + 0.02f;
                        pb += px[si] * pt; pg += px[si + 1] * pt; pr += px[si + 2] * pt; pw += pt;
                    }
                }
                int di = (y * bw + x) * 4;
                acc[di] = sb; acc[di + 1] = sg; acc[di + 2] = sr; acc[di + 3] = sw;
                fg[di] = pb; fg[di + 1] = pg; fg[di + 2] = pr; fg[di + 3] = pw;
            }
        });
        var near = (float[])acc.Clone();
        for (int pass = 0; pass < 3; pass++)
        {
            BoxH(near, bw, bh, NearRadius); BoxV(near, bw, bh, NearRadius);
            BoxH(fg, bw, bh, NearRadius); BoxV(fg, bw, bh, NearRadius);
        }
        float[]? blur = null;
        if (radius > 0)
        {
            for (int pass = 0; pass < 3; pass++) { BoxH(acc, bw, bh, radius); BoxV(acc, bw, bh, radius); }
            blur = Normalise(acc);
        }
        return (Normalise(fg), Normalise(near), blur, bw, bh);
    }

    private static float[] Normalise(float[] acc)
    {
        var rgb = new float[acc.Length / 4 * 3];
        for (int i = 0, j = 0; i < acc.Length; i += 4, j += 3)
        {
            float wt = Math.Max(1e-4f, acc[i + 3]);
            rgb[j] = acc[i] / wt; rgb[j + 1] = acc[i + 1] / wt; rgb[j + 2] = acc[i + 2] / wt;
        }
        return rgb;
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
