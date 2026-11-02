using Windows.Media.Editing;
using Windows.Storage;

namespace OpenKamera.Core.Tests;

[Collection("Timing")]
public class ReplayAndTransitionTests
{
    private static byte[] Solid(int w, int h, byte v) { var p = new byte[w * h * 4]; for (int i = 0; i < p.Length; i += 4) { p[i] = p[i + 1] = p[i + 2] = v; p[i + 3] = 255; } return p; }

    [Fact]
    public async Task ReplaySavesAboutTheLastThirtySeconds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "replay-" + Guid.NewGuid().ToString("N")[..6]);
        string output = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "replay-" + Guid.NewGuid().ToString("N")[..6] + ".mp4");
        await using var buf = new ReplayBuffer(dir, 320, 180, 10, withAudio: false, keep: TimeSpan.FromSeconds(30), piece: TimeSpan.FromSeconds(10));
        await buf.StartAsync();
        for (int i = 0; i < 450; i++) // 45 s at 10 fps
        {
            buf.Push(Solid(320, 180, (byte)(i % 255)), 320, 180, TimeSpan.FromSeconds(i / 10.0));
            if (i % 20 == 0) await Task.Delay(15);
            if (i % 100 == 0) await Task.Delay(400); // real time: the next piece opens while frames keep coming
        }
        Assert.True(await buf.SaveAsync(output));
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(output));
        Assert.InRange(clip.OriginalDuration.TotalSeconds, 20, 41);
        Assert.True(Directory.GetFiles(dir, "part*.mp4").Length <= 5, "old pieces are deleted");
        File.Delete(output);
    }

    [Fact]
    public void SlideShowsHalfOfEachSceneMidway()
    {
        int w = 100, h = 2;
        var fresh = Solid(w, h, 200); var old = Solid(w, h, 20);
        SceneTransitions.Apply(fresh, old, w, h, 0.5, SceneTransition.Slide); // smoothstep(0.5) = 0.5
        Assert.Equal(20, fresh[10 * 4]);   // left: the old scene leaving
        Assert.Equal(200, fresh[90 * 4]);  // right: the new scene arriving
    }

    [Fact]
    public void TransitionsEndOnTheNewSceneAndCutIsInstant()
    {
        foreach (var kind in Enum.GetValues<SceneTransition>())
        {
            var fresh = Solid(40, 20, 200);
            SceneTransitions.Apply(fresh, Solid(40, 20, 20), 40, 20, 1.0, kind);
            Assert.All(fresh.Where((_, i) => i % 4 != 3), v => Assert.Equal(200, v));
        }
        var cut = Solid(40, 20, 200);
        SceneTransitions.Apply(cut, Solid(40, 20, 20), 40, 20, 0.1, SceneTransition.Cut);
        Assert.Equal(200, cut[0]);
    }
}
