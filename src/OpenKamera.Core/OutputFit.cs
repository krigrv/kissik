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

    /// <summary>
    /// Digital zoom: the window starting at (<paramref name="left"/>, <paramref name="top"/>) and 1/zoom of the
    /// frame wide and high, scaled back up to the full frame with smooth (bilinear) sampling. Picking the nearest
    /// pixel instead made zoomed edges stair-stepped and made slow auto-framing moves jump a pixel at a time.
    /// </summary>
    public static byte[] Zoom(byte[] src, int w, int h, double left, double top, double zoom)
    {
        var (lox, hix) = ZoomRange(left, w, zoom);
        var (loy, hiy) = ZoomRange(top, h, zoom);
        return Resample(src, w, h, w, h, zoom, zoom, left, top, lox, hix, loy, hiy).Pixels;
    }

    /// <summary>Sampling stays inside the zoom window (and the frame), so nothing from outside it bleeds into the edge.</summary>
    public static (double Lo, double Hi) ZoomRange(double start, int size, double zoom)
    {
        double lo = Math.Clamp(start, 0, size - 1), hi = Math.Clamp(start + size / zoom - 1, lo, size - 1);
        return (lo, hi);
    }

    private static (byte[] Pixels, int Width, int Height) Resample(byte[] src, int w, int h, int outW, int outH, double scaleX, double scaleY)
    {
        if (w == outW && h == outH) return (src, w, h);
        double cropW = outW / scaleX, cropH = outH / scaleY;
        return Resample(src, w, h, outW, outH, scaleX, scaleY, (w - cropW) / 2, (h - cropH) / 2, 0, w - 1, 0, h - 1);
    }

    private static (byte[] Pixels, int Width, int Height) Resample(byte[] src, int w, int h, int outW, int outH, double scaleX, double scaleY,
        double left, double top, double loX, double hiX, double loY, double hiY)
    {
        var dst = new byte[outW * outH * 4];
        Parallel.For(0, outH, oy =>
        {
            double fy = Math.Clamp(top + (oy + 0.5) / scaleY - 0.5, loY, hiY);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, h - 1); double dy = fy - y0;
            for (int ox = 0; ox < outW; ox++)
            {
                double fx = Math.Clamp(left + (ox + 0.5) / scaleX - 0.5, loX, hiX);
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
