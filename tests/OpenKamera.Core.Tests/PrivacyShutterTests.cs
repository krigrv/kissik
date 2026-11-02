namespace OpenKamera.Core.Tests;

public class PrivacyShutterTests
{
    private static byte[] Noise(int w, int h)
    {
        var r = new Random(1); var px = new byte[w * h * 4]; r.NextBytes(px);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        return px;
    }

    [Fact]
    public void Off_ReturnsTheSameFrame()
    {
        var px = Noise(64, 36);
        Assert.Same(px, PrivacyShutter.Apply(px, 64, 36, ShutterMode.Off));
    }

    [Fact]
    public void Blur_HidesDetailAndLeavesInputAlone()
    {
        var px = Noise(240, 136); var copy = (byte[])px.Clone();
        var o = PrivacyShutter.Apply(px, 240, 136, ShutterMode.Blur);
        Assert.Equal(copy, px);
        Assert.Equal(o[0], o[4]); // neighbours in one block are identical
    }

    [Fact]
    public void Away_IsDarkWithABrightPauseSign()
    {
        int w = 320, h = 180; var px = Noise(w, h);
        var o = PrivacyShutter.Apply(px, w, h, ShutterMode.Away);
        Assert.True(o[0] < 90);
        int bh = h / 6, bw = bh / 4; int x = w / 2 - bw / 2 - bw + 1, y = h / 2;
        Assert.Equal(235, o[(y * w + x) * 4]);
    }
}
