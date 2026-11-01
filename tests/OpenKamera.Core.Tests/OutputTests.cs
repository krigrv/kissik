using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

public class OutputTests(ITestOutputHelper output)
{
    // A typical 4K UVC webcam's mode list.
    private static readonly CaptureMode[] Modes =
    [
        new(640, 480, 30, "YUY2"), new(1280, 720, 30, "NV12"), new(1280, 720, 60, "MJPG"),
        new(1920, 1080, 30, "NV12"), new(1920, 1080, 60, "MJPG"), new(2560, 1440, 30, "MJPG"),
        new(3840, 2160, 30, "MJPG"),
    ];

    [Theory]
    [InlineData("Meetings", 1280, 720, 30)]
    [InlineData("Meetings HD", 1920, 1080, 30)]
    [InlineData("Streaming", 1920, 1080, 60)]
    [InlineData("Meetings 1440p", 2560, 1440, 30)]
    [InlineData("4K", 3840, 2160, 30)]
    [InlineData("Vertical", 2560, 1440, 30)]      // a 1280-tall 9:16 crop needs ≥1280 rows → 1440p
    [InlineData("Vertical HD", 3840, 2160, 30)]   // 1920-tall output needs ≥1920 rows → 4K
    public void Chooser_PicksTheModeThatCoversTheProfile(string profileName, int w, int h, int fps)
    {
        var p = OutputProfile.All.Single(x => x.Name == profileName);
        var m = Modes[FormatChooser.Choose(Modes, p)];
        output.WriteLine($"{p.Name} → {m}");
        Assert.Equal((w, h, fps), (m.Width, m.Height, (int)m.Fps));
    }

    [Fact]
    public void Chooser_FallsBackToLargestWhenNothingCovers()
    {
        CaptureMode[] small = [new(640, 480, 30, "YUY2"), new(1280, 720, 30, "NV12")];
        var m = small[FormatChooser.Choose(small, OutputProfile.P2160)];
        Assert.Equal(1280, m.Width);
    }

    [Fact]
    public void Cover_VerticalCropKeepsTheCentre()
    {
        // 4×2 landscape: left half red, right half blue; centre columns are 1 (red) and 2 (blue).
        var src = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++) for (int x = 0; x < 4; x++)
        {
            int i = (y * 4 + x) * 4; src[i + 3] = 255;
            if (x < 2) src[i + 2] = 255; else src[i] = 255;
        }
        var (px, w, h) = OutputFit.Cover(src, 4, 2, 2, 4);
        Assert.Equal((2, 4), (w, h));
        Assert.True(px[2] > 100);       // left output column comes from the red side
        Assert.True(px[4] > 100);       // right output column from the blue side
    }

    [Fact]
    public void Stretch_ScalesWithoutCropping()
    {
        // 2×1: red | blue → 4×2 must keep red on the left half and blue on the right half, nothing cut off.
        byte[] src = [0, 0, 255, 255, 255, 0, 0, 255];
        var (px, w, h) = OutputFit.Stretch(src, 2, 1, 4, 2);
        Assert.Equal((4, 2), (w, h));
        Assert.Equal(255, px[2]);                 // (0,0) red
        Assert.Equal(255, px[(1 * 4 + 3) * 4]);   // (3,1) blue
    }

    [Theory]
    [InlineData(1280, 720, 30)]
    [InlineData(720, 1280, 30)]
    public async Task Recorder_WritesPlayableMp4(int w, int h, int fps)
    {
        string dir = Path.Combine(Path.GetTempPath(), "OpenKameraTests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"rec-{w}x{h}.mp4");
        if (File.Exists(path)) File.Delete(path);

        await using var rec = new CompositeRecorder(w, h, fps);
        await rec.StartAsync(path);
        var frame = new byte[w * h * 4];
        for (int i = 0; i < 60; i++)
        {
            // Moving bar so the encoder has real motion to compress.
            Array.Clear(frame);
            for (int y = 0; y < h; y++) for (int x = i * 8; x < Math.Min(w, i * 8 + 40); x++)
                { int p = (y * w + x) * 4; frame[p] = 255; frame[p + 1] = 200; frame[p + 3] = 255; }
            rec.Push(frame, w, h, TimeSpan.FromSeconds(i / (double)fps));
            await Task.Delay(5);
        }
        await rec.StopAsync();

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(rec.Path);
        var props = await file.Properties.GetVideoPropertiesAsync();
        output.WriteLine($"{rec.Path}: {new FileInfo(rec.Path).Length:N0} bytes, {props.Duration.TotalSeconds:0.00} s, {props.Width}×{props.Height}, written {rec.FramesWritten}, dropped {rec.FramesDropped}");
        Assert.Equal((uint)w, props.Width);
        Assert.Equal((uint)h, props.Height);
        Assert.InRange(props.Duration.TotalSeconds, 1.7, 2.3);
        Assert.Equal(0, rec.FramesDropped);
    }
}
