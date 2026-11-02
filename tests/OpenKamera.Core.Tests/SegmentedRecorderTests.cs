namespace OpenKamera.Core.Tests;

[Collection("Encoding")]
public class SegmentedRecorderTests
{
    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "seg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static async Task Feed(SegmentedRecorder rec, int w, int h, int frames, int fps = 30)
    {
        var frame = new byte[w * h * 4];
        for (int f = 0; f < frames; f++)
        {
            byte v = (byte)(f * 4);
            for (int i = 0; i < frame.Length; i += 4) { frame[i] = v; frame[i + 1] = 128; frame[i + 2] = (byte)(255 - v); frame[i + 3] = 255; }
            rec.Push(frame, w, h, TimeSpan.FromSeconds(f / (double)fps));
            await Task.Delay(8);
        }
    }

    [Fact]
    public async Task Pieces_AreJoinedIntoOneFileAndCleanedUp()
    {
        string dir = Dir(), path = Path.Combine(dir, "rec.mp4");
        var rec = new SegmentedRecorder(320, 180, 30) { SegmentLength = TimeSpan.FromSeconds(1) };
        await rec.StartAsync(path);
        await Feed(rec, 320, 180, 90); // 3 s → 3 pieces
        await rec.StopAsync();
        Assert.True(rec.Parts >= 2, $"parts {rec.Parts}");
        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(path + SegmentedRecorder.PartsSuffix));
        var video = await (await Windows.Storage.StorageFile.GetFileFromPathAsync(path)).Properties.GetVideoPropertiesAsync();
        Assert.InRange(video.Duration.TotalSeconds, 2.3, 3.6);
    }

    [Fact]
    public async Task Recover_RescuesPiecesLeftByACrash()
    {
        string dir = Dir(), path = Path.Combine(dir, "crashed.mp4");
        var rec = new SegmentedRecorder(320, 180, 30) { SegmentLength = TimeSpan.FromSeconds(1) };
        await rec.StartAsync(path);
        await Feed(rec, 320, 180, 75);
        // Simulate a crash: the finished pieces are on disk; plant an unreadable "piece being written".
        // Wait until the first piece is finished on disk (slower when other tests are encoding at the same time).
        string parts = path + SegmentedRecorder.PartsSuffix;
        for (int i = 0; i < 100 && !Directory.GetFiles(parts, "part*.mp4").Any(f => new FileInfo(f).Length > 1000 && f.EndsWith("part0001.mp4")); i++) await Task.Delay(100);
        await Task.Delay(1500);
        File.WriteAllBytes(Path.Combine(path + SegmentedRecorder.PartsSuffix, "part9999.mp4"), [0, 0, 0, 1, 2, 3]);
        var saved = await SegmentedRecorder.RecoverAsync(dir);
        Assert.Single(saved);
        Assert.Contains("recovered", saved[0]);
        Assert.True(new FileInfo(saved[0]).Length > 1000);
    }

    [Fact]
    public async Task StoppingRightAfterANewPieceStarts_StillSavesTheRecording()
    {
        string dir = Dir(), path = Path.Combine(dir, "edge.mp4");
        var rec = new SegmentedRecorder(320, 180, 30) { SegmentLength = TimeSpan.FromSeconds(1) };
        await rec.StartAsync(path);
        await Feed(rec, 320, 180, 31); // just past 1 s: a second, empty piece opens
        await Task.Delay(300);
        await rec.StopAsync();
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 1000);
    }
}
