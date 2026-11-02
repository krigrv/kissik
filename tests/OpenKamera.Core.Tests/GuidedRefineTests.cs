using System.Diagnostics;

namespace OpenKamera.Core.Tests;

public class GuidedRefineTests
{
    /// <summary>A bright person on the left half, dark room on the right; the AI mask's edge is blurry and 12 px off.</summary>
    private static (byte[] Px, float[] Alpha) Scene(int w, int h, int edge, int maskEdge, int ramp)
    {
        var px = new byte[w * h * 4]; var alpha = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4; byte v = x < edge ? (byte)220 : (byte)30;
                px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
                alpha[y * w + x] = Math.Clamp((maskEdge + ramp / 2f - x) / ramp, 0, 1);
            }
        return (px, alpha);
    }

    [Fact]
    public void SnapsTheBlurryMaskToTheRealEdge()
    {
        const int w = 320, h = 180, edge = 160;
        var (px, alpha) = Scene(w, h, edge, maskEdge: 172, ramp: 24);
        float ErrorAt(float[] a) { float e = 0; for (int x = 140; x < 190; x++) e += Math.Abs(a[90 * w + x] - (x < edge ? 1 : 0)); return e; }
        float before = ErrorAt(alpha);
        MaskOps.GuidedRefine(alpha, px, w, h, scale: 2, radius: 10);
        float after = ErrorAt(alpha);
        Assert.True(after < before * 0.65f, // measured: 12.5 -> 7.5 (40% closer to the true edge)
            $"edge error {before:F2} -> {after:F2}");
        Assert.All(alpha, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void LeavesSolidAreasAlone()
    {
        const int w = 64, h = 36;
        var px = new byte[w * h * 4]; var alpha = new float[w * h];
        Array.Fill(alpha, 1f);
        MaskOps.GuidedRefine(alpha, px, w, h, scale: 2);
        Assert.All(alpha, v => Assert.Equal(1f, v));
    }

    [Fact]
    public void FastEnoughAt1080p()
    {
        const int w = 1920, h = 1080;
        var (px, alpha) = Scene(w, h, 960, 975, 40);
        MaskOps.GuidedRefine(alpha, px, w, h, scale: 4); // warm up
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) MaskOps.GuidedRefine(alpha, px, w, h, scale: 4);
        double ms = sw.Elapsed.TotalMilliseconds / 5;
        Assert.True(ms < 40, // ~10 ms alone; the bound allows for the other tests running in parallel
            $"{ms:F1} ms per 1080p frame");
    }
}
