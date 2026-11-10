using OpenKamera.Core;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

[Collection("Timing")]
public class RecordingTimingTests(ITestOutputHelper log)
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Feeds 10 ms blocks stamped by <paramref name="stamp"/>; returns where each written block landed.</summary>
    private static List<(int Block, TimeSpan At)> Run(int blocks, Func<int, TimeSpan?> stamp)
    {
        var tl = new AudioTimeline();
        var placed = new List<(int, TimeSpan)>();
        for (int i = 0; i < blocks; i++)
        {
            if (stamp(i) is not { } at) continue; // this block never arrived
            if (!tl.Place(at, out var gap)) continue;
            tl.Advance(gap);
            placed.Add((i, tl.End));
            tl.Advance(Ms(10));
        }
        return placed;
    }

    [Fact]
    public void SteadySound_WithJitter_IsSeamless()
    {
        var rng = new Random(1);
        var placed = Run(1000, i => Ms(i * 10 + rng.Next(-8, 9)));
        Assert.Equal(1000, placed.Count); // nothing left out
        for (int i = 1; i < placed.Count; i++) Assert.Equal(Ms(10), placed[i].At - placed[i - 1].At); // no silence slipped in
    }

    [Fact]
    public void AfterABreakInTheSound_LaterSoundIsStillInStep()
    {
        // Sound stops for 3 s (the audio engine restarting), then carries on.
        var placed = Run(1000, i => i is >= 200 and < 500 ? null : Ms(i * 10));
        var after = placed.First(p => p.Block == 600);
        Assert.InRange((after.At - Ms(6000)).TotalMilliseconds, -60, 60); // used to be 3 s early
    }

    [Fact]
    public void SoundArrivingForAMomentAlreadyCovered_IsLeftOut_NotPushedLater()
    {
        // The writer filled 500 ms with silence while sound was held up; the held-up sound then arrives late.
        var tl = new AudioTimeline();
        Assert.True(tl.Place(TimeSpan.Zero, out _)); tl.Advance(Ms(10));
        tl.Advance(Ms(500)); // silence written by the recorder
        Assert.False(tl.Place(Ms(10), out _));   // belongs at 10 ms, which is long gone
        Assert.False(tl.Place(Ms(300), out _));
        Assert.True(tl.Place(Ms(505), out var gap)); // back in step
        Assert.Equal(TimeSpan.Zero, gap);
    }

    [Fact]
    public void FirstSound_StartsWhereItsStampSays()
    {
        var tl = new AudioTimeline();
        Assert.True(tl.Place(Ms(35), out var gap));
        Assert.Equal(Ms(35), gap);
    }

    [Fact]
    public void ClocksThatDriftApart_AreCorrectedInSmallSteps()
    {
        // The sound card runs 0.1 % fast: over 10 minutes that is 600 ms. No single correction may be large.
        var placed = Run(60_000, i => Ms(i * 10 * 0.999));
        var last = placed[^1];
        Assert.InRange((last.At - Ms(last.Block * 10 * 0.999)).TotalMilliseconds, -70, 70);
        Assert.True(placed.Count > 59_900); // only about one block in a thousand left out
    }

    [Fact]
    public void Delay_ShiftsSoundLaterByExactlyThatMuch()
    {
        var d = new AudioDelay { Samples = AudioDelay.SamplesFor(100) };
        Assert.Equal(4800 * 2, d.Samples);
        var input = new float[48000 * 2];
        for (int i = 0; i < input.Length; i++) input[i] = i + 1;
        var output = new List<float>();
        for (int at = 0; at < input.Length; at += 960) output.AddRange(d.Process(input[at..(at + 960)]));
        Assert.All(output.Take(9600), s => Assert.Equal(0f, s));
        Assert.Equal(input.Take(1000), output.Skip(9600).Take(1000));
    }

    [Fact]
    public void NoDelay_PassesTheSameBlockThrough()
    {
        var block = new float[] { 1, 2, 3, 4 };
        Assert.Same(block, new AudioDelay().Process(block));
    }

    [Fact]
    [Trait("Category", "Hardware")] // depends on this PC's encoders; cloud machines only have software ones
    public async Task PiecesAreJoinedByCopying_AndEveryFramePlays()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "rec.mp4");
        var rec = new SegmentedRecorder(320, 180, 30, withAudio: true) { SegmentLength = TimeSpan.FromSeconds(1) };
        await rec.StartAsync(path);
        var frame = new byte[320 * 180 * 4];
        var block = new float[1600 * 2];
        for (int f = 0; f < 100; f++)
        {
            Array.Fill(frame, (byte)(f * 2));
            var t = TimeSpan.FromSeconds(f / 30.0);
            rec.Push(frame, 320, 180, t);
            rec.PushAudio(block, t);
            await Task.Delay(8);
        }
        await rec.StopAsync();
        log.WriteLine($"parts {rec.Parts}, copied {SegmentedRecorder.JoinedByCopy}, written {rec.FramesWritten}");
        Assert.True(rec.Parts >= 3);
        Assert.True(SegmentedRecorder.JoinedByCopy, "the pieces were encoded again instead of copied");
        int frames = RecordingJoiner.CountFrames(path);
        log.WriteLine($"frames that decode: {frames}");
        Assert.InRange(frames, rec.FramesWritten - 3, rec.FramesWritten);
        var props = await (await Windows.Storage.StorageFile.GetFileFromPathAsync(path)).Properties.GetVideoPropertiesAsync();
        Assert.InRange(props.Duration.TotalSeconds, 3.0, 3.7);
    }

    /// <summary>
    /// The camera can go quiet for a few seconds mid-recording while the microphone carries on. The sound for that
    /// stretch used to be written far ahead of the picture, which made the file writer wait for picture that the
    /// same thread could no longer deliver: the recording froze there and Stop never finished.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Hardware")]
    public async Task APictureThatGoesQuiet_DoesNotFreezeTheRecording(bool gpu)
    {
        string dir = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "quiet-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "rec.mp4");
        var rec = new SegmentedRecorder(1280, 720, 30, withAudio: true) { PreferGpu = gpu };
        await rec.StartAsync(path);
        log.WriteLine($"GPU encoder: {rec.OnGpu}");
        var frame = new byte[1280 * 720 * 4];
        var block = new float[480 * 2]; // 10 ms
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // 1 s of picture and sound, 6 s of sound alone, then 1 s of both again, in real time.
        while (clock.Elapsed < TimeSpan.FromSeconds(8))
        {
            var t = clock.Elapsed;
            bool picture = t < TimeSpan.FromSeconds(1) || t > TimeSpan.FromSeconds(7);
            if (picture) { Array.Fill(frame, (byte)(t.TotalSeconds * 30)); rec.Push(frame, 1280, 720, t); }
            for (int i = 0; i < 3; i++) rec.PushAudio(block, t + TimeSpan.FromMilliseconds(10 * i));
            await Task.Delay(30);
        }
        var stop = rec.StopAsync();
        Assert.True(await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(30))) == stop, "Stop never finished: the recording froze");
        await stop;
        int frames = RecordingJoiner.CountFrames(path);
        var props = await (await Windows.Storage.StorageFile.GetFileFromPathAsync(path)).Properties.GetVideoPropertiesAsync();
        log.WriteLine($"written {rec.FramesWritten}, decode {frames}, duration {props.Duration.TotalSeconds:F2} s");
        Assert.True(frames >= 30, $"only {frames} frames made it into the file");
        Assert.InRange(props.Duration.TotalSeconds, 7.0, 8.6); // the quiet stretch is kept, so sound stays in step
    }
}
