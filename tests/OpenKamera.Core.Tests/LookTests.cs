using OpenKamera.Core;

namespace OpenKamera.Core.Tests;

public class LookTests
{
    private static byte[] Pixel(byte b, byte g, byte r) => [b, g, r, 255];

    [Fact]
    public void BlackAndWhite_MakesChannelsEqual()
    {
        var px = Pixel(30, 120, 220);
        ImageFilters.Apply(px, new ImageFilters.Adjustments(Filter: FilterPreset.BlackAndWhite));
        Assert.Equal(px[0], px[1]);
        Assert.Equal(px[1], px[2]);
    }

    [Fact]
    public void Warm_RaisesRedLowersBlue()
    {
        var px = Pixel(128, 128, 128);
        ImageFilters.Apply(px, new ImageFilters.Adjustments(Filter: FilterPreset.Warm));
        Assert.True(px[2] > 128 && px[0] < 128);
    }

    [Fact]
    public void Temperature_PositiveIsWarmer()
    {
        var px = Pixel(128, 128, 128);
        ImageFilters.Apply(px, new ImageFilters.Adjustments(Temperature: 1));
        Assert.True(px[2] > px[0]);
    }

    [Fact]
    public void Hue_360IsIdentityAnd180SwapsWarmToCool()
    {
        var a = Pixel(40, 60, 200);
        ImageFilters.Apply(a, new ImageFilters.Adjustments(Hue: 180));
        Assert.True(a[0] > a[2], "red-ish pixel should turn blue/cyan after 180°");

        var b = Pixel(40, 60, 200);
        ImageFilters.Apply(b, new ImageFilters.Adjustments(Hue: 0.0001));
        Assert.InRange(b[2], 199, 201);
    }

    [Fact]
    public void Gamma_AboveOneBrightensMidtones()
    {
        var px = Pixel(64, 64, 64);
        ImageFilters.Apply(px, new ImageFilters.Adjustments(Gamma: 2.0));
        Assert.True(px[0] > 100);
    }

    [Fact]
    public void Sharpness_IncreasesEdgeContrast()
    {
        // 3×3 image, bright centre on mid-grey: sharpening pushes the centre brighter.
        var img = new byte[3 * 3 * 4];
        for (int i = 0; i < img.Length; i += 4) { img[i] = img[i + 1] = img[i + 2] = 100; img[i + 3] = 255; }
        int c = (1 * 3 + 1) * 4;
        img[c] = img[c + 1] = img[c + 2] = 150;
        ImageFilters.Apply(img, 3, 3, new ImageFilters.Adjustments(Sharpness: 1));
        Assert.True(img[c] > 150);
    }

    private const string IdentityCube2 = """
        TITLE "Identity"
        LUT_3D_SIZE 2
        0 0 0
        1 0 0
        0 1 0
        1 1 0
        0 0 1
        1 0 1
        0 1 1
        1 1 1
        """;

    [Fact]
    public void Lut_ParsesAndIdentityPreservesColour()
    {
        var lut = Lut3D.Parse(IdentityCube2);
        Assert.Equal("Identity", lut.Name);
        Assert.Equal(2, lut.Size);
        var (r, g, b) = lut.Sample(0.25f, 0.5f, 0.75f);
        Assert.Equal(0.25f, r, 3); Assert.Equal(0.5f, g, 3); Assert.Equal(0.75f, b, 3);
    }

    [Fact]
    public void Lut_InvertAtHalfStrengthBlends()
    {
        var lut = Lut3D.Parse("""
            LUT_3D_SIZE 2
            1 1 1
            0 1 1
            1 0 1
            0 0 1
            1 1 0
            0 1 0
            1 0 0
            0 0 0
            """);
        var full = Pixel(0, 0, 255);
        ImageFilters.Apply(full, new ImageFilters.Adjustments(Lut: lut, LutStrength: 1));
        Assert.Equal(new byte[] { 255, 255, 0 }, full[..3]);

        var half = Pixel(0, 0, 255);
        ImageFilters.Apply(half, new ImageFilters.Adjustments(Lut: lut, LutStrength: 0.5));
        Assert.InRange(half[2], 126, 129);
    }

    [Fact]
    public void Lut_RejectsWrongEntryCount()
    {
        Assert.Throws<FormatException>(() => Lut3D.Parse("LUT_3D_SIZE 2\n0 0 0\n"));
    }

    // 2×1 image: left red, right blue.
    private static byte[] RedBlue() => [0, 0, 255, 255, 255, 0, 0, 255];

    [Fact]
    public void Transform_MirrorSwapsLeftAndRight()
    {
        var (px, w, h) = new FrameTransform(Mirror: true).Apply(RedBlue(), 2, 1);
        Assert.Equal((2, 1), (w, h));
        Assert.Equal(255, px[0]); // left is now blue
        Assert.Equal(255, px[6]); // right is now red
    }

    [Fact]
    public void Transform_Rotate90SwapsDimensions()
    {
        var (px, w, h) = new FrameTransform(Rotation: 90).Apply(RedBlue(), 2, 1);
        Assert.Equal((1, 2), (w, h));
        Assert.Equal(255, px[2]); // top is the original left (red)
        Assert.Equal(255, px[4]); // bottom is the original right (blue)
    }

    [Fact]
    public void Transform_ZoomIsSmooth_NotNearestPixel()
    {
        // A 4-pixel ramp 0, 80, 160, 240 zoomed 2x about its centre shows the middle half, stretched.
        byte[] ramp = new byte[16];
        for (int x = 0; x < 4; x++) { ramp[x * 4] = ramp[x * 4 + 1] = ramp[x * 4 + 2] = (byte)(x * 80); ramp[x * 4 + 3] = 255; }
        var (px, w, h) = new FrameTransform(Zoom: 2).Apply(ramp, 4, 1);
        Assert.Equal((4, 1), (w, h));
        int[] got = [px[0], px[4], px[8], px[12]];
        // Nearest gave 80, 80, 160, 160 (each pixel doubled). Smooth sampling gives in-between values.
        Assert.Equal(80, got[0]); Assert.Equal(160, got[3]);
        Assert.InRange(got[1], 81, 119); Assert.InRange(got[2], 121, 159);
        Assert.True(got[0] < got[1] && got[1] < got[2] && got[2] < got[3]);
    }

    [Fact]
    public void Transform_ZoomThenMirror_IsTheMirrorOfTheZoom()
    {
        var src = new byte[8 * 2 * 4];
        new Random(3).NextBytes(src);
        var zoomed = new FrameTransform(Zoom: 1.6, PanX: 0.4, PanY: -0.5).Apply(src, 8, 2).Pixels;
        var both = new FrameTransform(Mirror: true, Zoom: 1.6, PanX: 0.4, PanY: -0.5).Apply(src, 8, 2).Pixels;
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 8; x++)
                for (int c = 0; c < 4; c++)
                    Assert.Equal(zoomed[(y * 8 + (7 - x)) * 4 + c], both[(y * 8 + x) * 4 + c]);
    }

    [Fact]
    public void Transform_ZoomPanCropsToOneSide()
    {
        var (px, _, _) = new FrameTransform(Zoom: 2, PanX: 1).Apply(RedBlue(), 2, 1);
        Assert.Equal(255, px[0]); Assert.Equal(255, px[4]); // both output pixels are blue
    }
}
