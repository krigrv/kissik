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
        ImageFilters.Apply(copy, w, h, new ImageFilters.Adjustments(Brightness: r.Brightness, Gamma: r.Gamma, Contrast: r.Contrast, Temperature: r.Temperature, Tint: r.Tint));
        return copy;
    }

    [Fact]
    public void DarkRoomGetsBrightenedToAMidTone()
    {
        var px = Fill(160, 90, 50, 50, 50); // dim, neutral
        var r = AutoPicture.Analyze(px, 160, 90);
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

    // A dark room with the face low in the frame and lit by the screen: the old fixed centre box saw only dark wall,
    // asked for maximum brightening and blew the face out.
    private static byte[] DarkRoomLowFace(int w, int h, out FaceBox face)
    {
        var px = Fill(w, h, 18, 20, 26); // dim, slightly blue room
        int x0 = w * 4 / 10, x1 = w * 6 / 10, y0 = h * 6 / 10, y1 = h * 9 / 10;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4; bool lit = (x + y) % 4 == 0; // a lit cheek among mid-tones
                px[i] = (byte)(lit ? 190 : 95); px[i + 1] = (byte)(lit ? 200 : 105); px[i + 2] = (byte)(lit ? 215 : 120);
            }
        face = new FaceBox(0.4, 0.6, 0.2, 0.3);
        return px;
    }

    [Fact]
    public void FaceIsMeasuredWhereItIsAndNeverBlownOut()
    {
        var px = DarkRoomLowFace(160, 90, out var face);
        var r = AutoPicture.Analyze(px, 160, 90, face);
        var after = Apply(px, 160, 90, r);
        int lit = (75 * 160 + 80) * 4; // a lit face pixel ((x+y)%4==0)
        double litLuma = (0.299 * after[lit + 2] + 0.587 * after[lit + 1] + 0.114 * after[lit]) / 255.0;
        Assert.True(litLuma <= AutoPicture.HighlightCeiling + 0.03, $"lit skin {litLuma:F2} after gamma {r.Gamma}");
        Assert.True(r.Gamma < 2.2, $"gamma {r.Gamma}: the face is not as dark as the wall");
    }

    [Fact]
    public void DarkFrameGetsNoColourGuessOrNoiseBoost()
    {
        var px = DarkRoomLowFace(160, 90, out var face);
        var r = AutoPicture.Analyze(px, 160, 90, face);
        Assert.True(r.TooDark);
        Assert.Equal(0, r.Temperature); // the "blue" is mostly noise in a frame this dark
        Assert.Equal(0, r.Sharpness);
        Assert.Equal(0, r.Vibrance);
        Assert.Equal(1.0, r.Contrast);
        Assert.Contains("Turn on Light", r.Summary);
    }

    [Fact]
    public void NearBlackFrameSaysItIsTooDarkInsteadOfBalanced()
    {
        var r = AutoPicture.Analyze(Fill(160, 90, 3, 3, 4), 160, 90);
        Assert.True(r.TooDark);
        Assert.Equal(1, r.Gamma);
        Assert.DoesNotContain("balanced", r.Summary);
        Assert.Contains("too dark", r.Summary);
    }

    private static double Luma(byte[] px, int i) => (0.299 * px[i + 2] + 0.587 * px[i + 1] + 0.114 * px[i]) / 255.0;

    // A dim, flat room: hazy dark jacket, a dim face, a brighter wall. A big gamma lift turned this milky
    // (the jacket went from 27 to 68 of 255); the fix keeps the blacks down and the wall unclipped.
    [Fact]
    public void DimFlatRoomIsBrightenedWithoutWashingOut()
    {
        const int w = 160, h = 90;
        var px = Fill(w, h, 150, 152, 158); // wall, about 0.6
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (y > h * 6 / 10) { px[i] = 32; px[i + 1] = 26; px[i + 2] = 25; }                                    // jacket, about 0.1
                else if (x >= w * 4 / 10 && x < w * 6 / 10 && y >= h * 2 / 10) { px[i] = 50; px[i + 1] = 62; px[i + 2] = 80; } // face, about 0.25
            }
        var r = AutoPicture.Analyze(px, w, h, new FaceBox(0.4, 0.2, 0.2, 0.4));
        var after = Apply(px, w, h, r);
        int jacket = (80 * w + 20) * 4, face = (40 * w + 80) * 4, wall = (10 * w + 10) * 4;
        Assert.True(Luma(after, face) > Luma(px, face) + 0.05, $"face {Luma(px, face):F2} -> {Luma(after, face):F2}");
        Assert.True(Luma(after, jacket) <= Luma(px, jacket) + 0.02, $"blacks lifted {Luma(px, jacket):F2} -> {Luma(after, jacket):F2}");
        Assert.True(Luma(after, wall) <= AutoPicture.RoomCeiling + 0.01, $"wall clipped {Luma(after, wall):F2}");
    }

    [Fact]
    public void BalancedPictureIsLeftAlone()
    {
        var px = Fill(160, 90, 133, 133, 133);
        var r = AutoPicture.Analyze(px, 160, 90);
        Assert.Equal(1, r.Gamma);
        Assert.Equal(1, r.Brightness);
        Assert.Equal(1, r.Contrast);
        Assert.Equal(0, r.Temperature);
        Assert.Equal(0, r.Tint);
    }
}
