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

    [Fact]
    public void Framer_ZoomsTowardTheTargetFaceSizeSmoothly()
    {
        var f = new AutoFramer { TargetFaceShare = 0.3, MaxZoom = 3, Follow = 0.5 };
        var t = DateTime.UtcNow;
        var face = new FaceBox(0.45, 0.4, 0.1, 0.15); // small face: wants 2× zoom
        double z = 1;
        for (int i = 0; i < 20; i++) z = f.Update(face, t.AddMilliseconds(i * 100)).Zoom;
        Assert.InRange(z, 1.9, 2.1);
    }

    [Fact]
    public void Framer_HoldsDuringABriefMissAndRelaxesAfterTwoSeconds()
    {
        var f = new AutoFramer { Follow = 1 };
        var t = DateTime.UtcNow;
        f.Update(new FaceBox(0.4, 0.4, 0.1, 0.1), t);
        double zoomed = f.Zoom;
        Assert.Equal(zoomed, f.Update(null, t.AddSeconds(1)).Zoom);
        Assert.Equal(1, f.Update(null, t.AddSeconds(3)).Zoom, 6);
    }

    [Fact]
    public void Framer_IgnoresTinyMovements()
    {
        var f = new AutoFramer { Follow = 1 };
        var t = DateTime.UtcNow;
        f.Update(new FaceBox(0.45, 0.4, 0.1, 0.15), t);
        var before = (f.Zoom, f.PanX, f.PanY);
        f.Update(new FaceBox(0.452, 0.401, 0.1, 0.15), t.AddMilliseconds(100));
        Assert.Equal(before, (f.Zoom, f.PanX, f.PanY));
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
