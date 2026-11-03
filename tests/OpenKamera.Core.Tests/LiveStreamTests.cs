namespace OpenKamera.Core.Tests;

[Collection("Encoding")]
public class LiveStreamTests
{
    [Fact]
    public void Args_ScaleDown4KAndUseTeeForSeveralDestinations()
    {
        var one = LiveStream.BuildArgs(3840, 2160, 30, null, ["rtmp://a/live2/KEY"]);
        Assert.Contains("-vf scale=1920:1080", one);
        Assert.Contains("-f flv \"rtmp://a/live2/KEY\"", one);
        Assert.Contains("anullsrc", one);
        var two = LiveStream.BuildArgs(1280, 720, 30, @"\\.\pipe\x", ["rtmp://a/k1", "rtmp://b/k2"]);
        Assert.Contains("-f tee", two);
        Assert.Contains("[f=flv:onfail=ignore]rtmp://b/k2", two);
        Assert.DoesNotContain("scale=", two);
    }

    [Fact]
    public void Bitrate_IsSensible()
    {
        Assert.InRange(LiveStream.VideoKbps(1920, 1080, 30), 5500, 6500);
        Assert.Equal(1500, LiveStream.VideoKbps(320, 180, 15));
        Assert.Equal(9000, LiveStream.VideoKbps(1920, 1080, 60));
    }

    [Fact]
    public void Key_IsStoredEncrypted()
    {
        var d = new StreamDestination { Url = "rtmp://live.twitch.tv/app", Key = "live_123_secret" };
        Assert.DoesNotContain("secret", d.ProtectedKey);
        Assert.Equal("live_123_secret", d.Key);
        Assert.Equal("rtmp://live.twitch.tv/app/live_123_secret", d.FullUrl);
    }

    [Fact]
    public async Task Streams_ToAFile_WithSound()
    {
        if (LiveStream.FindFfmpeg() is null) return; // FFmpeg not installed on this machine
        string dir = Path.Combine(Path.GetTempPath(), "OpenKameraTests"); Directory.CreateDirectory(dir);
        string outFile = Path.Combine(dir, $"stream-{Guid.NewGuid():N}.flv");
        await using var s = new LiveStream(640, 360, 30);
        await s.StartAsync([outFile], withAudio: Environment.GetEnvironmentVariable("OK_NOAUDIO") is null);
        var frame = new byte[640 * 360 * 4];
        for (int i = 0; i < 150; i++)
        {
            s.Push((byte[])frame.Clone(), 640, 360);
            s.PushAudio(new float[1600 * 2]);
            await Task.Delay(33);
        }
        await s.StopAsync();
        Assert.True(File.Exists(outFile), $"no file; sent {s.FramesSent} dropped {s.FramesDropped}\n{s.Log}");
        Assert.True(new FileInfo(outFile).Length > 10_000, $"small file; sent {s.FramesSent} dropped {s.FramesDropped}\n{s.Log}");
        Assert.True(s.FramesSent > 75, $"sent {s.FramesSent}, dropped {s.FramesDropped}\n{s.Log}");
    }
}
