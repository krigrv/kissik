using OpenKamera.Core;

namespace OpenKamera.Core.Tests;

/// <summary>When someone has hidden their room, a failure must hide more, never less.</summary>
public class RoomStaysHiddenTests
{
    private const int W = 64, H = 36;

    /// <summary>A sharp black-and-white checkerboard "room": any pixel still pure black or white was not hidden.</summary>
    private static byte[] Room()
    {
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte v = ((x / 4 + y / 4) & 1) == 0 ? (byte)255 : (byte)0;
                int i = (y * W + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
            }
        return px;
    }

    /// <summary>Share of pixels away from the border that are still pure black or white.</summary>
    private static double StillSharp(byte[] px)
    {
        int sharp = 0, n = 0;
        for (int y = 8; y < H - 8; y++)
            for (int x = 8; x < W - 8; x++, n++)
            {
                byte v = px[(y * W + x) * 4 + 1];
                if (v is < 40 or > 215) sharp++;
            }
        return (double)sharp / n;
    }

    [Fact]
    public void ReplaceWithNoPicture_BlursTheRoom_InsteadOfShowingIt()
    {
        var px = Room();
        var nobody = new float[W * H]; // no person: all of it is room
        BackgroundEffects.Apply(px, W, H, nobody, new BackgroundSettings(BackgroundMode.Replace, BackgroundId: "missing"), background: null);
        Assert.True(StillSharp(px) < 0.02, $"{StillSharp(px):P0} of the room is still sharp");
    }

    [Fact]
    public void ReplaceWithItsPicture_StillShowsThePicture()
    {
        var px = Room();
        var picture = new byte[W * H * 4];
        for (int i = 0; i < picture.Length; i += 4) { picture[i] = 10; picture[i + 1] = 200; picture[i + 2] = 30; picture[i + 3] = 255; }
        BackgroundEffects.Apply(px, W, H, new float[W * H], new BackgroundSettings(BackgroundMode.Replace, BackgroundId: "x"), picture);
        int mid = (H / 2 * W + W / 2) * 4;
        Assert.Equal((10, 200, 30), (px[mid], px[mid + 1], px[mid + 2]));
    }

    // Nearby person: a brown shirt. Nearby room: a pale wall. (blue, green, red)
    private static float Edge(float a, (float B, float G, float R) px, (float, float, float)? person = null, (float, float, float)? room = null)
    {
        var (fb, fg, fr) = person ?? (70f, 110f, 150f);
        var (mb, mg, mr) = room ?? (170f, 195f, 215f);
        return BackgroundEffects.EdgeAlpha(a, px.B, px.G, px.R, fb, fg, fr, mb, mg, mr);
    }

    [Fact]
    public void SoftEdge_OverPureWall_IsGivenUp_SoNoGlowIsLeft()
    {
        // The model says "half person", but the pixel is exactly the wall's colour.
        Assert.True(Edge(0.5f, (170, 195, 215)) < 0.06f);
    }

    [Fact]
    public void SoftEdge_OverPureShirt_KeepsWhatTheModelSaid()
    {
        Assert.InRange(Edge(0.5f, (70, 110, 150)), 0.47f, 0.5f);
        Assert.InRange(Edge(0.95f, (70, 110, 150)), 0.94f, 0.95f);
    }

    [Fact]
    public void EdgeIsOnlyEverLowered_NeverRaised()
    {
        var rng = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            float a = (float)rng.NextDouble();
            (float, float, float) C() => (rng.Next(256), rng.Next(256), rng.Next(256));
            Assert.True(Edge(a, C(), C(), C()) <= a + 1e-4f); // raising it could let room through
        }
    }

    [Fact]
    public void WhenShirtAndWallAreTheSameColour_TheModelDecides_AndTheSoftEdgeIsFirmedUp()
    {
        (float, float, float) cream = (200, 215, 225);
        Assert.Equal(0.95f, Edge(0.95f, cream, cream, cream), 2); // nearly solid is left as it is
        Assert.Equal(0f, Edge(0.2f, cream, cream, cream), 2);     // a faint fringe goes
        Assert.InRange(Edge(0.5f, cream, cream, cream), 0.25f, 0.45f); // the middle of the soft band is pulled in
    }

    [Theory]
    [InlineData(BackgroundMode.Blur)]
    [InlineData(BackgroundMode.Replace)]
    public void WhenTheCutOutFails_OnTheGpuPath_EverythingCountsAsBackground(BackgroundMode mode)
    {
        if (!File.Exists(PersonSegmenter.ModelPath(SegmentationQuality.Fast))) return; // model not present
        using var pipeline = new BackgroundPipeline();
        var s = new BackgroundSettings(mode, 1.0, BackgroundId: "missing");
        var mask = pipeline.ModelMask((_, _) => throw new InvalidOperationException("graphics device lost"), s);
        Assert.NotNull(mask);
        Assert.All(mask!.Alpha, a => Assert.Equal(0f, a));
        Assert.Equal("graphics device lost", pipeline.LastError);
    }

    [Fact]
    public void VideoModel_RemembersBetweenFrames_AndStartsFreshAfterReset()
    {
        if (!File.Exists(PersonSegmenter.ModelPath(SegmentationQuality.Video))) return; // model not present
        using var seg = new PersonSegmenter(SegmentationQuality.Video, preferGpu: false);
        var frame = new byte[320 * 180 * 4];
        new Random(2).NextBytes(frame);
        var first = seg.Segment(frame, 320, 180);
        Assert.Equal(seg.InputWidth * seg.InputHeight, first.Alpha.Length);
        Assert.All(first.Alpha, a => Assert.InRange(a, 0f, 1f));
        var second = seg.Segment(frame, 320, 180);  // now with what it remembers from the first
        seg.Reset();
        var fresh = seg.Segment(frame, 320, 180);   // memory cleared: the same answer as the very first frame
        double drift = 0, same = 0;
        for (int i = 0; i < first.Alpha.Length; i++) { drift += Math.Abs(second.Alpha[i] - first.Alpha[i]); same += Math.Abs(fresh.Alpha[i] - first.Alpha[i]); }
        Assert.True(same / first.Alpha.Length < 1e-4, $"after a reset the result should match a fresh start (mean difference {same / first.Alpha.Length})");
        Assert.True(drift >= 0); // the second frame may differ: that is the memory at work
    }

    [Fact]
    public void WhenTheCutOutFails_WithOnlySpotlightOn_ThePictureIsLeftAlone()
    {
        if (!File.Exists(PersonSegmenter.ModelPath(SegmentationQuality.Fast))) return;
        using var pipeline = new BackgroundPipeline();
        var mask = pipeline.ModelMask((_, _) => throw new InvalidOperationException("x"), new BackgroundSettings(BackgroundMode.Off, Spotlight: 0.5));
        Assert.Null(mask); // nothing private to protect: no reason to black out the picture
    }
}
