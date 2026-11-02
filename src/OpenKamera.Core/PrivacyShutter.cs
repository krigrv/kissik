namespace OpenKamera.Core;

public enum ShutterMode { Off, Blur, Away }

/// <summary>
/// One-click privacy for meetings and recordings without disconnecting: Blur hides detail but shows you're there;
/// Away shows a dim "paused" card. The preview stays normal so you can still see yourself.
/// </summary>
public static class PrivacyShutter
{
    /// <summary>Returns a new BGRA frame for the outside world (input untouched).</summary>
    public static byte[] Apply(byte[] px, int w, int h, ShutterMode mode)
    {
        if (mode == ShutterMode.Off) return px;
        var dst = Pixelate(px, w, h, Math.Max(8, w / 24));
        if (mode == ShutterMode.Away)
        {
            for (int i = 0; i < dst.Length; i += 4) { dst[i] = (byte)(dst[i] / 4 + 10); dst[i + 1] = (byte)(dst[i + 1] / 4 + 10); dst[i + 2] = (byte)(dst[i + 2] / 4 + 12); }
            DrawPause(dst, w, h);
        }
        return dst;
    }

    internal static byte[] Pixelate(byte[] px, int w, int h, int block)
    {
        var dst = new byte[px.Length];
        Parallel.For(0, (h + block - 1) / block, by =>
        {
            int y0 = by * block, y1 = Math.Min(h, y0 + block);
            for (int x0 = 0; x0 < w; x0 += block)
            {
                int x1 = Math.Min(w, x0 + block); long b = 0, g = 0, r = 0, n = 0;
                for (int y = y0; y < y1; y += 2) for (int x = x0; x < x1; x += 2) { int i = (y * w + x) * 4; b += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
                byte B = (byte)(b / n), G = (byte)(g / n), R = (byte)(r / n);
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y * w + x) * 4; dst[i] = B; dst[i + 1] = G; dst[i + 2] = R; dst[i + 3] = 255; }
            }
        });
        return dst;
    }

    /// <summary>Two rounded-ish white bars in the middle: the universal "paused" sign.</summary>
    private static void DrawPause(byte[] px, int w, int h)
    {
        int bh = h / 6, bw = Math.Max(4, bh / 4), gap = bw, cx = w / 2, cy = h / 2;
        foreach (int left in new[] { cx - gap / 2 - bw, cx + gap / 2 })
            for (int y = cy - bh / 2; y < cy + bh / 2; y++)
                for (int x = left; x < left + bw; x++)
                {
                    int i = (y * w + x) * 4; px[i] = px[i + 1] = px[i + 2] = 235; px[i + 3] = 255;
                }
    }
}
