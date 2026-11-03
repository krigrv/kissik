namespace OpenKamera.Core.Tests;

public class AutoPictureTests
{
    private static byte[] Fill(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return px;
    }

    private static byte[] Apply(byte[] px, int w, int h, AutoPicture.Result r)
    {
        var copy = (byte[])px.Clone();
        ImageFilters.Apply(copy, w, h, new ImageFilters.Adjustments(Gamma: r.Gamma, Contrast: r.Contrast, Temperature: r.Temperature, Tint: r.Tint));
        return copy;
    }

    [Fact]
    public void DarkRoomGetsBrightenedToAMidTone()
    {
        var px = Fill(160, 90, 50, 50, 50); // dim, neutral
        var r = AutoPicture.Analyze(px, 160, 90);
        Assert.True(r.Gamma > 1.3, $"gamma {r.Gamma}");
        var after = Apply(px, 160, 90, r);
        Assert.InRange(after[45 * 160 * 4 + 80 * 4 + 1] / 255.0, 0.42, 0.62);
    }

    [Fact]
    public void BlueCastIsWarmedAndGreenCastIsRemoved()
    {
        var blue = AutoPicture.Analyze(Fill(160, 90, 110, 130, 170), 160, 90);
        Assert.True(blue.Temperature > 0.2, $"temperature {blue.Temperature}");
        var green = AutoPicture.Analyze(Fill(160, 90, 120, 160, 120), 160, 90);
        Assert.True(green.Tint > 0.2, $"tint {green.Tint}");
        var after = Apply(Fill(160, 90, 120, 160, 120), 160, 90, green);
        Assert.True(after[1] - after[2] < 160 - 120, "green lead shrinks");
    }

    [Fact]
    public void BalancedPictureIsLeftAlone()
    {
        var px = Fill(160, 90, 133, 133, 133);
        var r = AutoPicture.Analyze(px, 160, 90);
        Assert.Equal(1, r.Gamma);
        Assert.Equal(0, r.Temperature);
        Assert.Equal(0, r.Tint);
    }
}
