using System.Globalization;

namespace OpenKamera.Core;

/// <summary>Colours as "#RRGGBB" or "#AARRGGBB".</summary>
public static class ColorHex
{
    public static (byte A, byte R, byte G, byte B) Parse(string? hex, (byte, byte, byte, byte) fallback = default)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string h = hex.Trim().TrimStart('#');
        if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return fallback;
        return h.Length switch
        {
            6 => (255, (byte)(v >> 16), (byte)(v >> 8), (byte)v),
            8 => ((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v),
            _ => fallback,
        };
    }

    public static string Format(byte a, byte r, byte g, byte b) => $"#{a:X2}{r:X2}{g:X2}{b:X2}";
}

/// <summary>
/// Builds the final frame: scene background → camera (placed, shaped) → overlay layers.
/// The overlay is a premultiplied BGRA image of every non-camera layer, rendered by the UI.
/// </summary>
public static class SceneCompositor
{
    public static bool CameraFillsCanvas(SceneDocument scene)
    {
        var c = scene.Camera;
        return c.Visible && c.X <= 0 && c.Y <= 0 && c.W >= 1 && c.H >= 1 && c.Shape != "ellipse" && c.CornerRadius <= 0;
    }

    /// <summary>Composes in place into <paramref name="camera"/> when the camera fills the canvas, else into a new buffer.</summary>
    public static byte[] Compose(byte[] camera, int w, int h, SceneDocument scene, byte[]? overlay)
    {
        byte[] output;
        if (CameraFillsCanvas(scene))
        {
            output = camera;
            if (HasTransparency(camera)) UnderlayBackground(output, w, h, scene);
        }
        else
        {
            output = new byte[w * h * 4];
            FillBackground(output, w, h, scene);
            if (scene.Camera.Visible) PlaceCamera(output, camera, w, h, scene.Camera);
        }
        if (overlay is not null && overlay.Length == output.Length) BlendOver(output, overlay);
        return output;
    }

    private static bool HasTransparency(byte[] px)
    {
        for (int i = 3; i < px.Length; i += 4 * 97) if (px[i] < 255) return true;
        return false;
    }

    internal static void FillBackground(byte[] px, int w, int h, SceneDocument scene)
    {
        var top = ColorHex.Parse(scene.Background, (255, 16, 20, 24));
        var bottom = string.IsNullOrEmpty(scene.Background2) ? top : ColorHex.Parse(scene.Background2, top);
        Parallel.For(0, h, y =>
        {
            float t = h > 1 ? (float)y / (h - 1) : 0;
            byte r = (byte)(top.R + (bottom.R - top.R) * t), g = (byte)(top.G + (bottom.G - top.G) * t), b = (byte)(top.B + (bottom.B - top.B) * t);
            for (int x = 0, i = y * w * 4; x < w; x++, i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        });
    }

    /// <summary>Puts a transparent (background-removed) camera frame on the scene background.</summary>
    private static void UnderlayBackground(byte[] px, int w, int h, SceneDocument scene)
    {
        var bg = new byte[px.Length];
        FillBackground(bg, w, h, scene);
        BlendOver(bg, px);
        Buffer.BlockCopy(bg, 0, px, 0, px.Length);
    }

    /// <summary>Cover-fits the camera frame into the layer rectangle, masked to its shape with a soft edge.</summary>
    internal static void PlaceCamera(byte[] dst, byte[] cam, int w, int h, SceneLayer c)
    {
        int rx = (int)Math.Round(c.X * w), ry = (int)Math.Round(c.Y * h);
        int rw = Math.Max(1, (int)Math.Round(c.W * w)), rh = Math.Max(1, (int)Math.Round(c.H * h));
        var (fit, _, _) = OutputFit.Cover(cam, w, h, rw, rh);
        bool ellipse = c.Shape == "ellipse";
        float radius = (float)(c.CornerRadius * h);
        float opacity = (float)Math.Clamp(c.Opacity, 0, 1);
        Parallel.For(0, rh, y =>
        {
            int dy = ry + y;
            if (dy < 0 || dy >= h) return;
            for (int x = 0; x < rw; x++)
            {
                int dx = rx + x;
                if (dx < 0 || dx >= w) continue;
                float cover = ellipse ? EllipseCoverage(x + 0.5f, y + 0.5f, rw, rh) : RoundRectCoverage(x + 0.5f, y + 0.5f, rw, rh, radius);
                if (cover <= 0) continue;
                int s = (y * rw + x) * 4, d = (dy * w + dx) * 4;
                float a = cover * opacity * fit[s + 3] / 255f;
                for (int ch = 0; ch < 3; ch++)
                {
                    // fit is premultiplied: scale its colour by the extra coverage factor.
                    float srcPremul = fit[s + ch] * cover * opacity;
                    dst[d + ch] = (byte)Math.Clamp(srcPremul + dst[d + ch] * (1 - a), 0, 255);
                }
                dst[d + 3] = 255;
            }
        });
    }

    private static float EllipseCoverage(float x, float y, int w, int h)
    {
        float nx = (x - w / 2f) / (w / 2f), ny = (y - h / 2f) / (h / 2f);
        float d = MathF.Sqrt(nx * nx + ny * ny);
        float edge = 1.5f / Math.Min(w, h) * 2; // ~1.5 px soft edge
        return Math.Clamp((1 - d) / edge + 0.5f, 0, 1);
    }

    private static float RoundRectCoverage(float x, float y, int w, int h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2f);
        if (r <= 0.5f) return 1;
        float cx = Math.Clamp(x, r, w - r), cy = Math.Clamp(y, r, h - r);
        float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        return Math.Clamp(r - d + 0.5f, 0, 1);
    }

    /// <summary>Premultiplied source-over: dst = src + dst × (1 − srcA).</summary>
    /// <summary>
    /// Draws a premultiplied overlay over the frame everywhere except where the person is (alpha 1 = person),
    /// so the layer appears between the person and the background.
    /// </summary>
    public static void BlendBehind(byte[] frame, byte[] overlay, float[] personAlpha)
    {
        int n = Math.Min(personAlpha.Length, frame.Length / 4);
        Parallel.For(0, Math.Max(1, n / 4096 + 1), chunk =>
        {
            int start = chunk * 4096, end = Math.Min(n, start + 4096);
            for (int p = start; p < end; p++)
            {
                int i = p * 4; int oa = overlay[i + 3]; if (oa == 0) continue;
                float show = (1 - personAlpha[p]) * oa / 255f;   // how much of the layer to show here
                if (show <= 0) continue;
                float keep = 1 - show;
                for (int c = 0; c < 3; c++)
                {
                    float layer = oa > 0 ? overlay[i + c] * 255f / oa : 0; // un-premultiply
                    frame[i + c] = (byte)Math.Clamp(frame[i + c] * keep + layer * show, 0, 255);
                }
            }
        });
    }

    public static void BlendOver(byte[] dst, byte[] src)
    {
        int n = dst.Length / 4;
        Parallel.For(0, Math.Max(1, n / 4096 + 1), part =>
        {
            int start = part * 4096, end = Math.Min(n, start + 4096);
            for (int p = start; p < end; p++)
            {
                int i = p * 4;
                int sa = src[i + 3];
                if (sa == 0) continue;
                if (sa == 255) { dst[i] = src[i]; dst[i + 1] = src[i + 1]; dst[i + 2] = src[i + 2]; dst[i + 3] = 255; continue; }
                int inv = 255 - sa;
                dst[i] = (byte)Math.Min(255, src[i] + dst[i] * inv / 255);
                dst[i + 1] = (byte)Math.Min(255, src[i + 1] + dst[i + 1] * inv / 255);
                dst[i + 2] = (byte)Math.Min(255, src[i + 2] + dst[i + 2] * inv / 255);
                dst[i + 3] = (byte)Math.Min(255, sa + dst[i + 3] * inv / 255);
            }
        });
    }
}
