namespace OpenKamera.Core.Tests;

public class StabilizerTests
{
    private static byte[] Pattern(int w, int h, int ox, int oy)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int v = (((x - ox) / 40 + (y - oy) / 40) & 1) * 200 + ((x - ox) * 3 + (y - oy) * 7) % 50;
                int i = (y * w + x) * 4; px[i] = px[i + 1] = px[i + 2] = (byte)Math.Clamp(v, 0, 255); px[i + 3] = 255;
            }
        return px;
    }

    [Fact]
    public void Shift_FindsTheMove()
    {
        int w = 960, h = 540; // 10 px in the thumb = 100 px here
        var a = Stabilizer.Thumb(Pattern(w, h, 100, 100), w, h);
        var b = Stabilizer.Thumb(Pattern(w, h, 130, 90), w, h);
        Assert.Equal((3, -1), Stabilizer.Shift(a, b));
    }

    [Fact]
    public void StillPicture_NeedsNoCorrection()
    {
        var s = new Stabilizer();
        var f = Pattern(960, 540, 100, 100);
        s.Update(f, 960, 540); s.Update(f, 960, 540);
        Assert.Equal(0, s.OffsetX); Assert.Equal(0, s.OffsetY);
    }

    [Fact]
    public void Shake_IsFollowedAndRelaxes()
    {
        var s = new Stabilizer();
        s.Update(Pattern(960, 540, 100, 100), 960, 540);
        s.Update(Pattern(960, 540, 120, 100), 960, 540);
        Assert.True(s.OffsetX > 0);
        double first = s.OffsetX;
        var still = Pattern(960, 540, 120, 100);
        for (int i = 0; i < 30; i++) s.Update(still, 960, 540);
        Assert.True(s.OffsetX < first / 2);
    }

    [Fact]
    public void Apply_AddsMarginAndKeepsCentredWhenStill()
    {
        var t = new Stabilizer().Apply(FrameTransform.Identity);
        Assert.Equal(1.1, t.Zoom, 6); Assert.Equal(0, t.PanX, 6);
    }
}
