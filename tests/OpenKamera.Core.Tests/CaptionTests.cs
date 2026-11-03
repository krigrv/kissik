namespace OpenKamera.Core.Tests;

public class CaptionTests
{
    [Fact]
    public void ShowsLastTwoLinesWithPartialWordsLast()
    {
        var c = new CaptionBuffer { MaxCharsPerLine = 20 };
        var t = DateTime.UtcNow;
        c.Final("hello everyone and welcome to the show", t);
        c.Partial("today we talk cameras", t);
        var lines = c.Text(t).Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("cameras", lines[1]);
        Assert.All(lines, l => Assert.True(l.Length <= 20));
    }

    [Fact]
    public void ClearsAfterSilence()
    {
        var c = new CaptionBuffer();
        var t = DateTime.UtcNow;
        c.Final("hi", t);
        Assert.Equal("hi", c.Text(t.AddSeconds(1)));
        Assert.Equal("", c.Text(t.AddSeconds(5)));
    }

    [Fact]
    public void Burn_BlendsCentredNearTheBottom()
    {
        int w = 100, h = 100;
        var dst = new byte[w * h * 4];
        var src = new byte[10 * 4 * 4]; for (int i = 0; i < src.Length; i++) src[i] = 255;
        CaptionBurn.Blend(dst, w, h, src, 10, 4, 0.1);
        int y = 100 - 4 - 10, x = 45;
        Assert.Equal(255, dst[(y * w + x) * 4]);
        Assert.Equal(0, dst[(10 * w + x) * 4]);
    }
}

public class BehindLayerTests
{
    [Fact]
    public void BlendBehind_ShowsLayerOnBackgroundButNotOnPerson()
    {
        // Two pixels: background (alpha 0) and person (alpha 1); an opaque red layer covers both.
        var frame = new byte[] { 10, 10, 10, 255, 50, 60, 70, 255 };
        var red = new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 };
        SceneCompositor.BlendBehind(frame, red, [0f, 1f]);
        Assert.Equal(255, frame[2]);                  // background pixel is now red
        Assert.Equal(new byte[] { 50, 60, 70 }, frame[4..7]); // person pixel unchanged
    }
}

public class CrossFadeTests
{
    [Fact]
    public void CrossFade_GoesFromOldToNew()
    {
        var from = new byte[] { 0, 0, 0, 255 };
        var a = new byte[] { 200, 200, 200, 255 }; SceneCompositor.CrossFade(a, from, 0);
        Assert.Equal(0, a[0]);
        var b = new byte[] { 200, 200, 200, 255 }; SceneCompositor.CrossFade(b, from, 0.5);
        Assert.InRange(b[0], 95, 105);
        var c = new byte[] { 200, 200, 200, 255 }; SceneCompositor.CrossFade(c, from, 1);
        Assert.Equal(200, c[0]);
    }
}

public class SecondCameraTests
{
    [Fact]
    public void PlaceSource_PutsASmallerSourceIntoTheBox()
    {
        int w = 100, h = 100;
        var dst = new byte[w * h * 4];
        var src = new byte[20 * 10 * 4]; for (int i = 0; i < src.Length; i += 4) { src[i + 2] = 255; src[i + 3] = 255; } // red 20×10
        SceneCompositor.PlaceSource(dst, w, h, src, 20, 10, new SceneLayer { Kind = "camera2", X = 0.5, Y = 0.5, W = 0.4, H = 0.4 });
        Assert.Equal(255, dst[((70 * w) + 70) * 4 + 2]); // inside the box: red
        Assert.Equal(0, dst[((10 * w) + 10) * 4 + 2]);   // outside: untouched
    }
}

public class ExposureCheckTests
{
    [Fact]
    public void MarksClippedAndCrushedAreas()
    {
        int w = 24, h = 2; var px = new byte[w * h * 4];
        for (int x = 0; x < w; x++) for (int y = 0; y < h; y++) { int i = (y * w + x) * 4; byte v = x < 12 ? (byte)255 : (byte)0; px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        var (clip, crush) = ExposureCheck.Apply(px, w, h);
        Assert.Equal(0.5, clip, 2); Assert.Equal(0.5, crush, 2);
        Assert.Equal(230, px[2]);         // first stripe on the bright half is red
        Assert.Equal(230, px[12 * 4]);    // first stripe on the dark half is blue
    }
}

public class ColourVisionTests
{
    [Fact]
    public void RedAndGreenLookAlikeForDeuteranopia()
    {
        var px = new byte[] { 0, 0, 200, 255, 0, 120, 0, 255 }; // red, green
        ColourVision.Apply(px, ColourVisionType.Deuteranopia);
        int dr = Math.Abs(px[2] - px[6]), dg = Math.Abs(px[1] - px[5]);
        Assert.True(dr + dg < 100, $"{dr} {dg}"); // far closer than the original 200+120 difference
    }

    [Fact]
    public void GreyStaysGrey()
    {
        var px = new byte[] { 128, 128, 128, 255 };
        ColourVision.Apply(px, ColourVisionType.Tritanopia);
        Assert.InRange(px[0], 120, 136); Assert.InRange(px[2], 120, 136);
    }
}
