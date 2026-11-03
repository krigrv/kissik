namespace OpenKamera.Core;

/// <summary>
/// Digital stabilisation for handheld or wobbly cameras (phones on a stand, laptop on a knee). It measures how far
/// the picture shifted since the last frame on a tiny grey copy and steers a small crop the other way. The correction
/// slowly relaxes toward centre so a deliberate camera move is followed, not fought.
/// </summary>
public sealed class Stabilizer
{
    private const int W = 96, H = 54, Search = 6;
    private byte[]? _prev;

    /// <summary>Extra zoom that leaves room to move the crop (1.1 = 5 % margin each side).</summary>
    public double Margin { get; set; } = 1.1;
    /// <summary>0..1 per frame: how quickly the correction returns to centre.</summary>
    public double Relax { get; set; } = 0.04;

    /// <summary>Current correction as a fraction of the frame (positive = move the crop right/down).</summary>
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }

    public void Reset() { _prev = null; OffsetX = OffsetY = 0; }

    /// <summary>Feeds the latest output frame (BGRA) and updates the correction.</summary>
    public void Update(byte[] bgra, int w, int h)
    {
        var cur = Thumb(bgra, w, h);
        if (_prev is { } prev)
        {
            var (dx, dy) = Shift(prev, cur);
            // The picture moved by (dx, dy): slide the crop with it to cancel the move.
            double limit = (1 - 1 / Margin) / 2;
            OffsetX = Math.Clamp((OffsetX + dx / (double)W) * (1 - Relax), -limit, limit);
            OffsetY = Math.Clamp((OffsetY + dy / (double)H) * (1 - Relax), -limit, limit);
        }
        _prev = cur;
    }

    /// <summary>Adds the correction to a framing transform.</summary>
    public FrameTransform Apply(FrameTransform t)
    {
        double zoom = Math.Max(1, t.Zoom) * Margin;
        double half = (1 - 1 / zoom) / 2;
        // Keep the user's own pan (in its crop units) and add the correction on top.
        double userHalf = (1 - 1 / Math.Max(1, t.Zoom)) / 2;
        double px = (t.PanX * userHalf + OffsetX) / half, py = (t.PanY * userHalf + OffsetY) / half;
        return t with { Zoom = zoom, PanX = Math.Clamp(px, -1, 1), PanY = Math.Clamp(py, -1, 1) };
    }

    internal static byte[] Thumb(byte[] bgra, int w, int h)
    {
        var g = new byte[W * H];
        for (int y = 0; y < H; y++)
        {
            int sy = (int)((y + 0.5) * h / H);
            for (int x = 0; x < W; x++)
            {
                int i = (sy * w + (int)((x + 0.5) * w / W)) * 4;
                g[y * W + x] = (byte)((bgra[i + 2] * 77 + bgra[i + 1] * 150 + bgra[i] * 29) >> 8);
            }
        }
        return g;
    }

    /// <summary>Whole-pixel shift of <paramref name="cur"/> relative to <paramref name="prev"/> by least absolute difference.</summary>
    internal static (int Dx, int Dy) Shift(byte[] prev, byte[] cur)
    {
        long best = long.MaxValue; (int, int) bestD = (0, 0);
        for (int dy = -Search; dy <= Search; dy++)
            for (int dx = -Search; dx <= Search; dx++)
            {
                long sum = 0;
                for (int y = Search; y < H - Search; y++)
                {
                    int a = y * W, b = (y + dy) * W + dx;
                    for (int x = Search; x < W - Search; x++) sum += Math.Abs(prev[a + x] - cur[b + x]);
                }
                // Prefer no movement when it's a tie (flat walls, noise).
                if (dx != 0 || dy != 0) sum += sum / 50;
                if (sum < best) { best = sum; bestD = (dx, dy); }
            }
        return bestD;
    }
}
