namespace OpenKamera.Core.Tests;

public class FramingAndKeyTests
{
    [Fact]
    public void PansFor_PutsTheFaceAtTheFramingPoint()
    {
        var f = new AutoFramer { FaceLine = 0.5 };
        var (px, py) = f.PansFor(0.5, 0.5, 2);
        Assert.Equal(0, px, 6); Assert.Equal(0, py, 6);
        // Face on the far right: the crop slides right as far as it can.
        Assert.Equal(1, f.PansFor(0.95, 0.5, 2).PanX, 6);
        // Verify with FrameTransform's own formula: crop centre lands on the face.
        double c = 0.5;
        var (p2, _) = f.PansFor(0.6, 0.5, 2);
        double left = (1 - c) / 2 * (1 + p2);
        Assert.Equal(0.6, left + c / 2, 6);
    }

    private static (double Zoom, double PanX, double PanY) Run(AutoFramer f, IReadOnlyList<FaceBox> faces, DateTime t0, double seconds)
    {
        (double, double, double) r = default;
        for (int i = 0; i < seconds * 30; i++) { var t = t0.AddSeconds(i / 30.0); if (i % 5 == 0) f.SetFaces(faces, t); r = f.Step(t); }
        return r;
    }

    [Fact]
    public void Framer_SettlesOnTheTargetFaceSize()
    {
        var f = new AutoFramer { TargetFaceShare = 0.3, MaxZoom = 3 };
        var z = Run(f, [new FaceBox(0.45, 0.4, 0.1, 0.15)], DateTime.UtcNow, 3).Zoom; // small face: wants 2×
        Assert.InRange(z, 1.9, 2.1);
    }

    [Fact]
    public void Framer_MovesSmoothlyWithoutJumpsOrOvershoot()
    {
        var f = new AutoFramer { TargetFaceShare = 0.3, MaxZoom = 3 };
        var t0 = DateTime.UtcNow; double prev = 1, maxStep = 0, peak = 0;
        for (int i = 0; i < 120; i++)
        {
            var t = t0.AddSeconds(i / 30.0);
            if (i % 5 == 0) f.SetFaces([new FaceBox(0.45, 0.4, 0.1, 0.15)], t);
            double z = f.Step(t).Zoom; maxStep = Math.Max(maxStep, Math.Abs(z - prev)); peak = Math.Max(peak, z); prev = z;
        }
        Assert.True(maxStep < 0.08, $"largest per-frame jump {maxStep:F3}");
        Assert.True(peak <= 2.02, $"overshoot to {peak:F3}");
    }

    [Fact]
    public void Framer_HoldsDuringABriefMissAndRelaxesAfterTwoSeconds()
    {
        var f = new AutoFramer();
        var t = DateTime.UtcNow;
        var zoomed = Run(f, [new FaceBox(0.4, 0.4, 0.1, 0.1)], t, 3).Zoom;
        Assert.True(zoomed > 1.5);
        Assert.Equal(zoomed, Run(f, [], t.AddSeconds(3), 1).Zoom, 3);   // 1 s without a face: holds
        Assert.Equal(1, Run(f, [], t.AddSeconds(4), 4).Zoom, 2);        // then eases back out
    }

    [Fact]
    public void Framer_IgnoresTinyMovements()
    {
        var f = new AutoFramer();
        var t = DateTime.UtcNow;
        Run(f, [new FaceBox(0.45, 0.4, 0.1, 0.15)], t, 4);
        var before = (f.Zoom, f.PanX, f.PanY);
        Run(f, [new FaceBox(0.452, 0.401, 0.1, 0.15)], t.AddSeconds(4), 1);
        Assert.Equal(before, (f.Zoom, f.PanX, f.PanY));
    }

    [Fact]
    public void Framer_KeepsAGroupInShotAndIgnoresFacesFarBehind()
    {
        var two = new AutoFramer { MaxZoom = 3 };
        var (z, _, _) = Run(two, [new FaceBox(0.15, 0.35, 0.12, 0.18), new FaceBox(0.7, 0.35, 0.12, 0.18)], DateTime.UtcNow, 3);
        double c = 1 / z;
        Assert.True(c >= 0.82 - 0.15 + 0.1, $"crop {c:F2} must hold both faces"); // both faces span 0.15..0.82
        var one = new AutoFramer { MaxZoom = 3 };
        var (z1, _, _) = Run(one, [new FaceBox(0.45, 0.35, 0.1, 0.15), new FaceBox(0.9, 0.2, 0.02, 0.03)], DateTime.UtcNow, 3);
        Assert.InRange(z1, 1.9, 2.1); // the tiny far-away face doesn't widen the shot
    }

    private static byte[] Two(int r1, int g1, int b1, int r2, int g2, int b2) => [(byte)b1, (byte)g1, (byte)r1, 255, (byte)b2, (byte)g2, (byte)r2, 255];

    [Fact]
    public void ChromaKey_GreenIsScreenSkinIsPerson()
    {
        var px = Two(0, 177, 64, 200, 150, 120); // studio green, then a skin tone
        var a = ChromaKey.Mask(px, 2, 1, "#00B140", 0.4, 0.1, 0);
        Assert.True(a[0] < 0.05, $"green alpha {a[0]}");
        Assert.True(a[1] > 0.95, $"skin alpha {a[1]}");
    }

    [Fact]
    public void ChromaKey_KeysShadowedGreenToo()
    {
        var px = Two(0, 90, 35, 0, 230, 90); // dark and bright green cloth
        var a = ChromaKey.Mask(px, 2, 1, "#00B140", 0.4, 0.1, 0);
        Assert.True(a[0] < 0.3 && a[1] < 0.3, $"{a[0]} {a[1]}");
    }

    [Fact]
    public void ChromaKey_DespillTakesGreenOutOfEdges()
    {
        var px = Two(150, 200, 140, 150, 200, 140); // greenish edge pixel
        ChromaKey.Mask(px, 2, 1, "#00B140", 0.2, 0.3, 1);
        Assert.True(px[1] < 200, "green channel reduced");
    }
}
