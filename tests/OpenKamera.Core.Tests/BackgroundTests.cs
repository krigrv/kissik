using System.Diagnostics;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

[Collection("Timing")]
public class BackgroundTests(ITestOutputHelper output)
{
    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return px;
    }

    /// <summary>Left half is "person" (alpha 1), right half is background (alpha 0).</summary>
    private static float[] HalfMask(int w, int h)
    {
        var a = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w / 2; x++) a[y * w + x] = 1;
        return a;
    }

    [Theory]
    [InlineData(SegmentationQuality.Fast)]
    [InlineData(SegmentationQuality.High)]
    public void Segmenter_ReturnsMaskInRange(SegmentationQuality q)
    {
        using var seg = new PersonSegmenter(q, preferGpu: false);
        var mask = seg.Segment(Solid(320, 180, 90, 120, 160), 320, 180);
        Assert.Equal(mask.Width * mask.Height, mask.Alpha.Length);
        Assert.All(mask.Alpha, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void Upsample_MatchesFrameSizeAndKeepsHardEdges()
    {
        var m = new PersonMask([1, 0, 1, 0], 2, 2);
        var up = MaskOps.Upsample(m, 8, 8);
        Assert.Equal(64, up.Length);
        Assert.True(up[0] > 0.9f && up[7] < 0.1f);
    }

    [Fact]
    public void ExposureCurve_LiftsDarkFramesAndLeavesGoodOnesAlone()
    {
        var dark = Enumerable.Range(0, 1000).Select(i => (byte)(5 + i % 40)).ToArray();
        var curve = PersonSegmenter.ExposureCurve(dark, dark, dark);
        Assert.NotNull(curve);
        Assert.True(curve![25] > 100, $"midtone of a dark frame should be lifted, got {curve[25]}");

        var good = Enumerable.Range(0, 1000).Select(i => (byte)(i % 256)).ToArray();
        Assert.Null(PersonSegmenter.ExposureCurve(good, good, good));
    }

    [Fact]
    public void DropSmallRegions_RemovesStrayBlobButKeepsSecondPerson()
    {
        // 10×4: big region (cols 0-3), similar-size region (cols 6-8, rows 0-3 → 12 vs 16), tiny blob at (9,0).
        int w = 10, h = 4;
        var a = new float[w * h];
        for (int y = 0; y < h; y++) { for (int x = 0; x < 4; x++) a[y * w + x] = 1; for (int x = 6; x < 9; x++) a[y * w + x] = 1; }
        a[0 * w + 9] = 0; a[3 * w + 9] = 1; a[3 * w + 8] = 0; // isolate a 1-pixel blob at (9,3)
        MaskOps.DropSmallRegions(a, w, h, 0.35f);
        Assert.Equal(1, a[0]);            // main person kept
        Assert.Equal(1, a[0 * w + 7]);    // second person kept
        Assert.Equal(0, a[3 * w + 9]);    // stray blob removed
    }

    [Fact]
    public void Replace_KeepsPersonAndSwapsBackground()
    {
        int w = 8, h = 4;
        var px = Solid(w, h, 0, 0, 200);   // red frame
        var bg = Solid(w, h, 200, 0, 0);   // blue background
        BackgroundEffects.Apply(px, w, h, HalfMask(w, h), new BackgroundSettings(BackgroundMode.Replace), bg);
        Assert.Equal(200, px[2]);                  // left pixel still red
        Assert.Equal(200, px[(w - 1) * 4]);        // right pixel now blue
        Assert.Equal(0, px[(w - 1) * 4 + 2]);
    }

    [Fact]
    public void Remove_MakesBackgroundTransparent()
    {
        int w = 8, h = 4;
        var px = Solid(w, h, 50, 60, 70);
        BackgroundEffects.Apply(px, w, h, HalfMask(w, h), new BackgroundSettings(BackgroundMode.Remove));
        Assert.Equal(255, px[3]);
        Assert.Equal(0, px[(w - 1) * 4 + 3]);
    }

    [Fact]
    public void Blur_DoesNotBleedPersonColourIntoBackground()
    {
        // Person = white on the left, room = black on the right. A naive blur would turn the room grey.
        int w = 64, h = 32;
        var px = Solid(w, h, 0, 0, 0);
        for (int y = 0; y < h; y++) for (int x = 0; x < w / 2; x++) { int i = (y * w + x) * 4; px[i] = px[i + 1] = px[i + 2] = 255; }
        BackgroundEffects.Apply(px, w, h, HalfMask(w, h), new BackgroundSettings(BackgroundMode.Blur, BlurStrength: 1));
        int near = (h / 2 * w + w / 2 + 3) * 4; // just inside the room, next to the person
        Assert.True(px[near] < 40, $"halo: {px[near]}");
    }

    [Fact]
    public void Spotlight_DarkensRoomOnly()
    {
        int w = 8, h = 2;
        var px = Solid(w, h, 120, 120, 120);
        BackgroundEffects.Apply(px, w, h, HalfMask(w, h), new BackgroundSettings(Spotlight: 1));
        Assert.True(px[0] >= 120);
        Assert.True(px[(w - 1) * 4] < 70);
    }

    [Theory]
    [InlineData(224, 172, 150, true)]   // light skin
    [InlineData(141, 85, 36, true)]     // dark skin
    [InlineData(40, 90, 200, false)]    // blue shirt
    [InlineData(30, 160, 60, false)]    // green
    public void SkinDetector(int r, int g, int b, bool expected) =>
        Assert.Equal(expected, BackgroundEffects.IsSkin(r, g, b));

    [Fact]
    public void Library_GeneratesEveryBuiltIn()
    {
        foreach (var bg in BackgroundLibrary.BuiltIn)
        {
            var px = BackgroundLibrary.Generate(bg.Id, 64, 36);
            Assert.NotNull(px);
            Assert.Equal(64 * 36 * 4, px!.Length);
            Assert.True(px.Where((_, i) => i % 4 == 3).All(a => a == 255), bg.Id);
        }
    }

    /// <summary>
    /// Speed gate from the plan: ≥ 15 fps at 720p for the Fast path. Logs both qualities; GPU when present.
    /// </summary>
    [Fact]
    public void Pipeline_720p_Throughput()
    {
        using var pipe = new BackgroundPipeline();
        var frame = Solid(1280, 720, 80, 110, 150);
        foreach (var q in new[] { SegmentationQuality.Fast, SegmentationQuality.High })
        {
            var s = new BackgroundSettings(BackgroundMode.Blur, Quality: q);
            pipe.Apply((byte[])frame.Clone(), 1280, 720, s); // warm-up
            var sw = Stopwatch.StartNew();
            const int n = 20;
            for (int i = 0; i < n; i++) pipe.Apply((byte[])frame.Clone(), 1280, 720, s);
            double fps = n / sw.Elapsed.TotalSeconds;
            output.WriteLine($"{q}: {fps:0.0} fps at 720p (gpu={pipe.UsesGpu}, error={pipe.LastError ?? "none"})");
            Assert.Null(pipe.LastError);
            if (q == SegmentationQuality.Fast) Assert.True(fps >= 15, $"Fast path too slow: {fps:0.0} fps");
        }
    }

    /// <summary>Real-photo check, only when OPENKAMERA_SAMPLE points at a local portrait PNG (never committed).</summary>
    [Fact]
    public async Task RealPortrait_PersonIsInFrontOfBackground()
    {
        string? path = Environment.GetEnvironmentVariable("OPENKAMERA_SAMPLE");
        if (path is null || !File.Exists(path)) { output.WriteLine("skipped: OPENKAMERA_SAMPLE not set"); return; }
        var (px, w, h) = await BackgroundLibrary.DecodeAsync(path);
        string? outDir = Environment.GetEnvironmentVariable("OPENKAMERA_SAMPLE_OUT"); // optional local visual check
        var shares = new List<double>();
        foreach (var q in new[] { SegmentationQuality.Fast, SegmentationQuality.High })
        {
            using var seg = new PersonSegmenter(q);
            var m = seg.Segment(px, w, h);
            double personShare = m.Alpha.Count(a => a > 0.5f) / (double)m.Alpha.Length;
            output.WriteLine($"{q}: {personShare:P0} of the frame is person");
            shares.Add(personShare);
            if (outDir is null) continue;
            var grey = new byte[m.Width * m.Height * 4];
            for (int i = 0; i < m.Alpha.Length; i++) { byte v = (byte)(m.Alpha[i] * 255); grey[i * 4] = grey[i * 4 + 1] = grey[i * 4 + 2] = v; grey[i * 4 + 3] = 255; }
            await SavePngAsync(Path.Combine(outDir, $"mask-{q}.png"), grey, m.Width, m.Height);
        }
        Assert.All(shares, s => Assert.InRange(s, 0.05, 0.9));

        if (outDir is null) return;
        using var pipe = new BackgroundPipeline();
        foreach (var (name, s) in new (string, BackgroundSettings)[]
        {
            ("blur-portrait", new(BackgroundMode.Blur, BlurStrength: 0.3, Quality: SegmentationQuality.High)),
            ("blur-privacy", new(BackgroundMode.Blur, BlurStrength: 1, Quality: SegmentationQuality.High)),
            ("replace-bokeh", new(BackgroundMode.Replace, BackgroundId: "midnight-bokeh", Quality: SegmentationQuality.High)),
            ("replace-fast", new(BackgroundMode.Replace, BackgroundId: "soft-daylight", Quality: SegmentationQuality.Fast)),
            ("spotlight-retouch", new(Spotlight: 0.8, Retouch: 0.8, Quality: SegmentationQuality.High)),
        })
        {
            var copy = (byte[])px.Clone();
            pipe.Apply(copy, w, h, s);
            await SavePngAsync(Path.Combine(outDir, name + ".png"), copy, w, h);
        }
    }

    private static async Task SavePngAsync(string path, byte[] px, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)w, (uint)h, 96, 96, px);
        await enc.FlushAsync();
    }
}
