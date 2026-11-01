namespace OpenKamera.Core;

/// <summary>Scales a frame to the output profile size, centre-cropping whatever does not fit (cover).</summary>
public static class OutputFit
{
    public static (byte[] Pixels, int Width, int Height) Cover(byte[] src, int w, int h, int outW, int outH)
    {
        double scale = Math.Max((double)outW / w, (double)outH / h);
        return Resample(src, w, h, outW, outH, scale, scale);
    }

    /// <summary>Resizes to exactly outW×outH with no cropping (for same-aspect images such as overlays).</summary>
    public static (byte[] Pixels, int Width, int Height) Stretch(byte[] src, int w, int h, int outW, int outH) =>
        Resample(src, w, h, outW, outH, (double)outW / w, (double)outH / h);

    private static (byte[] Pixels, int Width, int Height) Resample(byte[] src, int w, int h, int outW, int outH, double scaleX, double scaleY)
    {
        if (w == outW && h == outH) return (src, w, h);
        double cropW = outW / scaleX, cropH = outH / scaleY;
        double left = (w - cropW) / 2, top = (h - cropH) / 2;
        var dst = new byte[outW * outH * 4];
        Parallel.For(0, outH, oy =>
        {
            double fy = Math.Clamp(top + (oy + 0.5) / scaleY - 0.5, 0, h - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, h - 1); double dy = fy - y0;
            for (int ox = 0; ox < outW; ox++)
            {
                double fx = Math.Clamp(left + (ox + 0.5) / scaleX - 0.5, 0, w - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, w - 1); double dx = fx - x0;
                int a = (y0 * w + x0) * 4, b = (y0 * w + x1) * 4, c = (y1 * w + x0) * 4, d = (y1 * w + x1) * 4;
                int o = (oy * outW + ox) * 4;
                for (int ch = 0; ch < 4; ch++)
                {
                    double top0 = src[a + ch] + (src[b + ch] - src[a + ch]) * dx;
                    double bot0 = src[c + ch] + (src[d + ch] - src[c + ch]) * dx;
                    dst[o + ch] = (byte)Math.Round(top0 + (bot0 - top0) * dy);
                }
            }
        });
        return (dst, outW, outH);
    }
}
