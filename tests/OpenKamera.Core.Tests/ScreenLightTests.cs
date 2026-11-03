namespace OpenKamera.Core.Tests;

public class ScreenLightTests
{
    [Fact]
    public void Kelvin_WarmIsOrangeCoolIsBlueDaylightIsNearWhite()
    {
        var warm = ScreenLight.KelvinToRgb(2700);
        Assert.True(warm.R == 255 && warm.B < 180 && warm.G > warm.B);
        var cool = ScreenLight.KelvinToRgb(9000);
        Assert.True(cool.B == 255 && cool.R < 240);
        var day = ScreenLight.KelvinToRgb(6500);
        Assert.True(day.R > 245 && day.G > 240 && day.B > 240, $"6500 K = {day}");
    }

    [Fact]
    public void Tint_OverridesTemperatureAndBrightnessScales()
    {
        var s = new ScreenLightSettings { Kelvin = 3000, Tint = "#FF3DD6", Brightness = 0.5 };
        var (r, g, b) = ScreenLight.LightColor(s);
        Assert.Equal((128, 30, 107), ((int)r, (int)g, (int)b)); // 0x3D×0.5 = 30.5 → 30 (banker's rounding)
    }

    [Fact]
    public void Looks_HaveUniqueIdsAndDescriptions()
    {
        Assert.Equal(ScreenLight.Looks.Count, ScreenLight.Looks.Select(l => l.Id).Distinct().Count());
        Assert.All(ScreenLight.Looks, l => Assert.True(l.Description.Length > 20));
    }

    [Theory]
    [InlineData(LightShape.SideBars, 0.25, 0.25)]
    [InlineData(LightShape.TopBar, 0.25, 0.25)]
    public void Coverage_MatchesSizeForBars(LightShape shape, double size, double expected) =>
        Assert.Equal(expected, ScreenLight.Coverage(shape, size, 1920, 1080), 2);

    [Fact]
    public void Frame_LeavesTheMiddleClearAndAreasDontOverlap()
    {
        var areas = ScreenLight.Areas(LightShape.Frame, 0.2, 1920, 1080);
        Assert.Equal(4, areas.Count);
        int lit = areas.Sum(a => a.W * a.H);
        int t = (int)Math.Round(1080 * 0.2 / 2);
        Assert.Equal(1920 * 1080 - (1920 - 2 * t) * (1080 - 2 * t), lit);
    }

    [Fact]
    public void Ring_IsAnEllipseMinusAHoleCentredOnScreen()
    {
        var areas = ScreenLight.Areas(LightShape.Ring, 0.3, 1920, 1080);
        Assert.Equal(2, areas.Count);
        Assert.True(areas[0].Ellipse && !areas[0].Subtract);
        Assert.True(areas[1].Subtract);
        Assert.Equal(960, areas[0].X + areas[0].W / 2);
        Assert.Equal(960, areas[1].X + areas[1].W / 2);
        Assert.InRange(ScreenLight.Coverage(LightShape.Ring, 0.3, 1920, 1080), 0.15, 0.3);
    }

    [Fact]
    public void AutoTune_RaisesForDarkFacesLowersForBrightAndHoldsNearTarget()
    {
        Assert.True(ScreenLight.AutoTuneStep(0.5, 60) > 0.5);
        Assert.True(ScreenLight.AutoTuneStep(0.5, 220) < 0.5);
        Assert.Equal(0.5, ScreenLight.AutoTuneStep(0.5, 132));
        Assert.InRange(ScreenLight.AutoTuneStep(0.5, 0) - 0.5, 0, 0.05001); // step limited
    }

    [Fact]
    public void CentreLuma_ReadsTheMiddleOfTheFrame()
    {
        int w = 30, h = 30;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            byte v = (byte)(x >= 10 && x < 20 ? 200 : 10);
            px[i] = px[i + 1] = px[i + 2] = v;
        }
        Assert.InRange(ScreenLight.CentreLuma(px, w, h), 190, 201);
    }

    [Theory]
    [InlineData(LightDirection.Left, -1, 0)]    // arc centred on 9 o'clock
    [InlineData(LightDirection.Right, 1, 0)]    // 3 o'clock
    [InlineData(LightDirection.Top, 0, -1)]     // 12 o'clock (y up is negative)
    [InlineData(LightDirection.Bottom, 0, 1)]   // 6 o'clock
    public void Direction_ArcIsCentredOnThatSide(LightDirection d, int dx, int dy)
    {
        var (start, sweep) = ScreenLight.DirectionArc(d);
        Assert.Equal(180, sweep);
        var (x, y) = ScreenLight.OnCircle(0, 0, 100, start + sweep / 2);
        Assert.Equal(dx * 100, x, 6);
        Assert.Equal(dy * 100, y, 6);
    }

    [Fact]
    public void KeyLeft_LightsTheUpperLeft()
    {
        var (start, sweep) = ScreenLight.DirectionArc(LightDirection.KeyLeft);
        var (x, y) = ScreenLight.OnCircle(0, 0, 100, start + sweep / 2);
        Assert.True(x < 0 && y < 0);
    }

    [Fact]
    public void RingLayout_FollowsDiameterPositionAndThickness()
    {
        var s = new ScreenLightSettings { Diameter = 0.5, CentreX = 0.25, CentreY = 0.75, Size = 0.2 };
        var (cx, cy, r, band) = ScreenLight.RingLayout(s, 1920, 1080);
        Assert.Equal((480.0, 810.0), (cx, cy));
        Assert.Equal(270, r, 6);
        Assert.Equal(54, band, 6);
    }

    [Fact]
    public void Leds_SpreadEvenlyAndRespectTheArc()
    {
        var full = ScreenLight.LedPositions(0, 0, 100, 10, 0, 360);
        Assert.InRange(full.Count, 30, 45);
        var half = ScreenLight.LedPositions(0, 0, 100, 10, 0, 180);
        Assert.All(half, p => Assert.True(p.X >= -0.001)); // right half only
    }

    [Fact]
    public void ArcBandPolygon_HasOuterAndInnerEdges()
    {
        var pts = ScreenLight.ArcBandPolygon(500, 500, 200, 150, 0, 90);
        Assert.True(pts.Count > 10);
        double first = Math.Sqrt(Math.Pow(pts[0].X - 500, 2) + Math.Pow(pts[0].Y - 500, 2));
        double last = Math.Sqrt(Math.Pow(pts[^1].X - 500, 2) + Math.Pow(pts[^1].Y - 500, 2));
        Assert.InRange(first, 199, 201);
        Assert.InRange(last, 149, 151);
    }

    [Fact]
    public void Areas_NeverNegativeOnATinySurface()
    {
        foreach (var shape in Enum.GetValues<LightShape>())
            Assert.All(ScreenLight.Areas(shape, 0.5, 0, 0), a => Assert.True(a.W >= 0 && a.H >= 0, shape.ToString()));
    }
}
