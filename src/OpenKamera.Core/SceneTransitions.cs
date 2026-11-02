namespace OpenKamera.Core;

public enum SceneTransition { Fade, Slide, Zoom, Cut }

/// <summary>
/// Moves from the old scene's last frame to the new scene. All ease in and out (smoothstep) over
/// <see cref="Duration"/>. Works on BGRA frames of equal size; <paramref name="t"/> runs 0..1.
/// </summary>
public static class SceneTransitions
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(0.45);

    public static void Apply(byte[] dst, byte[] from, int w, int h, double t, SceneTransition kind)
    {
        if (from.Length != dst.Length || t >= 1 || kind == SceneTransition.Cut) return;
        double e = Math.Clamp(t, 0, 1); e = e * e * (3 - 2 * e);
        switch (kind)
        {
            case SceneTransition.Fade: SceneCompositor.CrossFade(dst, from, t); break;
            case SceneTransition.Slide: Slide(dst, from, w, h, e); break;
            case SceneTransition.Zoom: Zoom(dst, from, w, h, e); break;
        }
    }

    /// <summary>The new scene pushes in from the right; the old one slides out to the left.</summary>
    private static void Slide(byte[] dst, byte[] from, int w, int h, double e)
    {
        int shift = (int)Math.Round(w * (1 - e)); // columns of the old scene still visible
        if (shift <= 0) return;
        var fresh = (byte[])dst.Clone();
        Parallel.For(0, h, y =>
        {
            int row = y * w * 4;
            int oldCols = shift, newCols = w - shift;
            // left part: old frame, shifted left by newCols
            Buffer.BlockCopy(from, row + newCols * 4, dst, row, oldCols * 4);
            // right part: new frame's left edge
            Buffer.BlockCopy(fresh, row, dst, row + oldCols * 4, newCols * 4);
        });
    }

    /// <summary>The old scene grows and fades away, revealing the new one: a gentle push-through.</summary>
    private static void Zoom(byte[] dst, byte[] from, int w, int h, double e)
    {
        double s = 1 + 0.25 * e;              // old frame scales up to 125 %
        int k = (int)Math.Clamp((1 - e) * 256, 0, 256);
        float cx = w / 2f, cy = h / 2f;
        Parallel.For(0, h, y =>
        {
            int sy = (int)Math.Clamp(cy + (y - cy) / s, 0, h - 1);
            for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
            {
                int sx = (int)Math.Clamp(cx + (x - cx) / s, 0, w - 1), j = (sy * w + sx) * 4;
                for (int c = 0; c < 3; c++) dst[i + c] = (byte)((from[j + c] * k + dst[i + c] * (256 - k)) >> 8);
            }
        });
    }
}
