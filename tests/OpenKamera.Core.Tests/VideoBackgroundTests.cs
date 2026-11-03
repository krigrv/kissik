namespace OpenKamera.Core.Tests;

[Collection("Encoding")]
public class VideoBackgroundTests
{
    [Fact]
    public async Task PlaysAVideoFileAsScaledFrames()
    {
        // Make a short red clip with the app's own recorder, then play it back as a background.
        string file = Path.Combine(Path.GetTempPath(), "OpenKameraTests", $"bgvideo-{Guid.NewGuid():N}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var red = new byte[320 * 180 * 4];
        for (int i = 0; i < red.Length; i += 4) { red[i] = 30; red[i + 1] = 30; red[i + 2] = 220; red[i + 3] = 255; }
        await using (var rec = new CompositeRecorder(320, 180, 15))
        {
            await rec.StartAsync(file);
            for (int i = 0; i < 30; i++) { rec.Push(red, 320, 180, TimeSpan.FromSeconds(i / 15.0)); await Task.Delay(5); }
            await rec.StopAsync();
        }
        using var bg = new VideoBackground(file, 160, 90);
        for (int i = 0; i < 60 && bg.Latest is null && bg.Error is null; i++) await Task.Delay(50);
        Assert.Null(bg.Error);
        var f = bg.Latest;
        Assert.NotNull(f);
        Assert.Equal(160 * 90 * 4, f!.Length);
        int mid = (45 * 160 + 80) * 4;
        Assert.True(f[mid + 2] > 150 && f[mid] < 90, $"expected red, got B{f[mid]} G{f[mid + 1]} R{f[mid + 2]}");
        Assert.Equal(255, f[mid + 3]);
    }

    [Fact]
    public void LibraryTreatsVideosSeparately()
    {
        Assert.True(BackgroundLibrary.IsVideo("user:rain.mp4"));
        Assert.False(BackgroundLibrary.IsVideo("user:room.jpg"));
        Assert.Null(BackgroundLibrary.Render("user:rain.mp4", 64, 36));
    }
}
