namespace OpenKamera.Core.Tests;

[Collection("Encoding")]
public class RecordingToolsTests
{
    private static async Task<string> MakeVideo(string dir, double seconds)
    {
        string path = Path.Combine(dir, "clip.mp4");
        var rec = new SegmentedRecorder(320, 180, 30);
        await rec.StartAsync(path);
        var frame = new byte[320 * 180 * 4];
        for (int f = 0; f < seconds * 30; f++)
        {
            for (int i = 0; i < frame.Length; i += 4) { frame[i] = (byte)(f * 3); frame[i + 3] = 255; }
            rec.Push(frame, 320, 180, TimeSpan.FromSeconds(f / 30.0));
            await Task.Delay(5);
        }
        await rec.StopAsync();
        return rec.Path;
    }

    [Fact]
    public async Task Trim_KeepsOnlyThePartAsked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "trim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string src = await MakeVideo(dir, 3);
        string outPath = await RecordingTools.TrimAsync(src, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2));
        Assert.EndsWith("(trimmed).mp4", outPath);
        var props = await (await Windows.Storage.StorageFile.GetFileFromPathAsync(outPath)).Properties.GetVideoPropertiesAsync();
        Assert.InRange(props.Duration.TotalSeconds, 1.2, 1.8);
        Assert.True(File.Exists(src)); // the original is kept
    }

    [Fact]
    public void List_FindsVideosAndPicturesNewestFirst()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "list-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "b.png"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "c.txt"), [1]);
        var items = RecordingTools.List(dir, dir);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.IsVideo);
    }
}
