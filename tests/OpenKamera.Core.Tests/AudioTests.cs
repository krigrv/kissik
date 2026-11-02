using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

[Collection("Encoding")]
public class AudioTests(ITestOutputHelper output)
{
    [Fact]
    public void Peaks_ReadEachChannelOfInterleavedStereo()
    {
        float[] s = [0.1f, -0.8f, -0.5f, 0.2f, 0.3f, 0.1f];
        var (l, r) = AudioEngine.Peaks(s);
        Assert.Equal(0.5f, l);
        Assert.Equal(0.8f, r);
    }

    [Fact]
    public void Decay_AttacksInstantlyAndReleasesSlowly()
    {
        Assert.Equal(0.9f, AudioEngine.Decay(0.2f, 0.9f));
        float next = AudioEngine.Decay(0.9f, 0f);
        Assert.InRange(next, 0.8f, 0.85f);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(6, 1.995)]
    [InlineData(-12, 0.251)]
    [InlineData(40, 3.981)] // clamped to +12 dB
    public void EqGain_ConvertsDecibelsToLinear(double db, double expected) =>
        Assert.Equal(expected, AudioEngine.DbToEqGain(db), 2);

    [Fact]
    public async Task Recorder_WritesVideoWithAnAacTrack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OpenKameraTests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "rec-audio.mp4");
        if (File.Exists(path)) File.Delete(path);

        int w = 640, h = 360, fps = 30;
        await using var rec = new CompositeRecorder(w, h, fps, withAudio: true);
        await rec.StartAsync(path);
        var frame = new byte[w * h * 4];
        int quantum = AudioEngine.SampleRate / 100; // 10 ms
        double phase = 0;
        for (int f = 0; f < 60; f++)
        {
            for (int i = 3; i < frame.Length; i += 4) frame[i] = 255;
            var t = TimeSpan.FromSeconds(f / (double)fps);
            rec.Push(frame, w, h, t);
            // ~3.3 quanta of a 440 Hz tone per video frame
            for (int q = 0; q < 3 + (f % 3 == 0 ? 1 : 0); q++)
            {
                var block = new float[quantum * 2];
                for (int i = 0; i < quantum; i++)
                {
                    float v = (float)(0.3 * Math.Sin(phase)); phase += 2 * Math.PI * 440 / AudioEngine.SampleRate;
                    block[2 * i] = v; block[2 * i + 1] = v;
                }
                rec.PushAudio(block, t);
            }
            await Task.Delay(5);
        }
        await rec.StopAsync();

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(rec.Path);
        var video = await file.Properties.GetVideoPropertiesAsync();
        var music = await file.Properties.GetMusicPropertiesAsync();
        output.WriteLine($"{new FileInfo(rec.Path).Length:N0} bytes, {video.Duration.TotalSeconds:0.00} s, audio blocks {rec.AudioBlocksWritten}, audio bitrate {music.Bitrate}");
        Assert.True(rec.AudioBlocksWritten > 150);
        Assert.InRange(video.Duration.TotalSeconds, 1.7, 2.4);
        Assert.True(music.Bitrate > 0, "no audio track in the MP4");
    }
}

public class VirtualCableTests
{
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", true)]
    [InlineData("Speakers (Realtek(R) Audio)", false)]
    public void IsVirtualCable_RecognisesCables(string name, bool expected) => Assert.Equal(expected, AudioEngine.IsVirtualCable(name));

    [Fact]
    public void FindVirtualCable_PicksTheCableOrNothing()
    {
        Assert.Equal("b", AudioEngine.FindVirtualCable([("a", "Speakers"), ("b", "CABLE Input (VB-Audio Virtual Cable)")])?.Id);
        Assert.Null(AudioEngine.FindVirtualCable([("a", "Speakers")]));
    }
}

public class AudioWatchdogTests
{
    [Fact]
    public void StoppedEngine_NeverNeedsRestart()
    {
        var e = new AudioEngine();
        Assert.False(e.NeedsRestart(DateTime.UtcNow.AddHours(1), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task RunningEngine_HasAHeartbeat_AndAStallIsDetected()
    {
        await using var e = new AudioEngine();
        await e.StartAsync(new AudioMixSettings { MicIds = ["none"] }); // no mic: the graph still ticks
        await Task.Delay(500);
        var now = DateTime.UtcNow;
        Assert.False(e.NeedsRestart(now, TimeSpan.FromSeconds(3)));
        Assert.True(e.NeedsRestart(now.AddSeconds(10), TimeSpan.FromSeconds(3)));
    }
}
