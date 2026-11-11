using OpenKamera.Core;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>The GPU frame path must match the CPU path (still the fallback) to within a level or two per channel.</summary>
[Collection("Timing")]
public class GpuEffectsTests(ITestOutputHelper log)
{
    private const int W = 320, H = 180;

    [Fact] public void Blur() => Compare(Recipe(bg: new(BackgroundMode.Blur, 0.7)));
    [Fact] public void BlurLow() => Compare(Recipe(bg: new(BackgroundMode.Blur, 0.0)));
    [Fact] public void Replace() => Compare(Recipe(bg: new(BackgroundMode.Replace, BackgroundId: "x")), replacement: Pattern(7));
    [Fact] public void ReplaceWithNoPictureReady_MatchesTheCpu() => Compare(Recipe(bg: new(BackgroundMode.Replace, BackgroundId: "x")), replacement: null);
    [Fact] public void Remove() => Compare(Recipe(bg: new(BackgroundMode.Remove)));
    [Fact] public void SpotlightAndRetouch() => Compare(Recipe(bg: new(BackgroundMode.Blur, 0.3, Spotlight: 0.8, Retouch: 0.9)));

    [Fact]
    public void Adjustments() => Compare(Recipe(adj: new(Brightness: 1.1, Contrast: 1.2, Saturation: 0.8, Sharpness: 0.7,
        Gamma: 1.3, Hue: 25, Vibrance: 0.4, Temperature: 0.3, Tint: -0.2)));

    [Theory]
    [InlineData(FilterPreset.BlackAndWhite)] [InlineData(FilterPreset.Warm)] [InlineData(FilterPreset.Cool)]
    [InlineData(FilterPreset.Bleach)] [InlineData(FilterPreset.Cinematic)] [InlineData(FilterPreset.Vivid)]
    [InlineData(FilterPreset.Fade)] [InlineData(FilterPreset.GoldenHour)] [InlineData(FilterPreset.TealOrange)]
    [InlineData(FilterPreset.FilmPrint)] [InlineData(FilterPreset.Punchy)] [InlineData(FilterPreset.Noir)] [InlineData(FilterPreset.Pastel)]
    public void Looks(FilterPreset f) => Compare(Recipe(adj: new(Filter: f)));

    [Fact]
    public void Lut()
    {
        // A 5³ LUT that swaps red and blue and lifts the blacks.
        var sb = new System.Text.StringBuilder("LUT_3D_SIZE 5\n");
        for (int b = 0; b < 5; b++) for (int g = 0; g < 5; g++) for (int r = 0; r < 5; r++)
            sb.Append($"{b / 4f * 0.9f + 0.05f:F4} {g / 4f:F4} {r / 4f * 0.8f:F4}\n");
        Compare(Recipe(adj: new(Lut: Lut3D.Parse(sb.ToString()), LutStrength: 0.75)));
    }

    [Fact]
    public void ScenePlacedCameraWithOverlay()
    {
        var scene = new SceneDocument { Background = "#203040", Background2 = "#805010" };
        scene.Layers.Add(new SceneLayer { Kind = "camera", X = 0.55, Y = 0.1, W = 0.4, H = 0.5, Shape = "ellipse", Opacity = 0.9 });
        Compare(Recipe(adj: new(Saturation: 1.3)) with { Scene = scene, Overlay = Overlay() });
    }

    [Fact]
    public void SceneLayersUnderCamera()
    {
        var scene = new SceneDocument { Background = "#203040" };
        scene.Layers.Add(new SceneLayer { Kind = "camera", X = 0.2, Y = 0.15, W = 0.5, H = 0.6, CornerRadius = 0.04, Opacity = 0.8 });
        Compare(Recipe() with { Scene = scene, UnderOverlay = Overlay(), Overlay = Overlay() });
    }

    [Fact]
    public void SceneRoundedCamera()
    {
        var scene = new SceneDocument();
        scene.Layers.Add(new SceneLayer { Kind = "camera", X = 0.1, Y = 0.2, W = 0.5, H = 0.6, CornerRadius = 0.05 });
        Compare(Recipe() with { Scene = scene });
    }

    [Fact]
    public void SceneUnderRemovedBackground()
    {
        var scene = new SceneDocument { Background = "#336699" };
        scene.Layers.Add(new SceneLayer { Kind = "camera", X = 0, Y = 0, W = 1, H = 1 });
        Compare(Recipe(bg: new(BackgroundMode.Remove)) with { Scene = scene, Overlay = Overlay() });
    }

    [Fact] public void BehindMe() => Compare(Recipe(bg: new(BackgroundMode.Blur, 0.5)) with { BehindOverlay = Overlay() });

    [Fact]
    public void FrameProcessorFallsBackWhenGpuOff()
    {
        bool was = FrameProcessor.UseGpu;
        try { FrameProcessor.UseGpu = false; Assert.False(FrameProcessor.GpuActive); }
        finally { FrameProcessor.UseGpu = was; }
    }

    [Theory]
    [InlineData(false, 0, 1.0)] [InlineData(true, 90, 1.5)] [InlineData(false, 180, 2.0)] [InlineData(true, 270, 1.0)]
    public void WholeFramePathMatches(bool mirror, int rotation, double zoom) =>
        CompareWhole(Recipe(bg: new(BackgroundMode.Blur, 0.5), adj: new(Contrast: 1.1, Sharpness: 0.4))
            with { Transform = new(mirror, rotation, zoom, 0.3, -0.2), OutputWidth = 288, OutputHeight = 162 });

    [Theory]
    [InlineData(false, 0, 1.0)] [InlineData(true, 90, 1.5)] [InlineData(false, 180, 2.0)] [InlineData(true, 270, 1.0)]
    public void FramingAndFitMatch(bool mirror, int rotation, double zoom) =>
        CompareWhole(Recipe(adj: new(Saturation: 1.2)) with { Transform = new(mirror, rotation, zoom, 0.3, -0.2), OutputWidth = 288, OutputHeight = 162 });

    [Fact]
    public void WholeFramePathReplaceAndScene()
    {
        var scene = new SceneDocument { Background = "#203040" };
        scene.Layers.Add(new SceneLayer { Kind = "camera", X = 0.5, Y = 0.1, W = 0.45, H = 0.6, Shape = "ellipse" });
        CompareWhole(Recipe(bg: new(BackgroundMode.Replace, BackgroundId: BackgroundLibrary.BuiltIn[0].Id))
            with { Scene = scene, OutputWidth = W, OutputHeight = H });
    }

    [Theory]
    [InlineData("rect")]
    [InlineData("ellipse")]
    public void SecondCameraPlacedOnTop(string shape)
    {
        var scene = new SceneDocument { Background = "#203040" };
        scene.Layers.Add(new SceneLayer { Kind = "camera", X = 0, Y = 0, W = 1, H = 1 });
        var layer = new SceneLayer { Kind = "camera2", X = 0.6, Y = 0.55, W = 0.35, H = 0.4, Shape = shape, CornerRadius = 0.03, Opacity = 0.85 };
        // A second camera of a different shape and size from the frame.
        const int sw = 96, sh = 128;
        var second = new byte[sw * sh * 4];
        for (int i = 0; i < second.Length; i += 4) { second[i] = (byte)(i * 7); second[i + 1] = (byte)(i / 13); second[i + 2] = 200; second[i + 3] = 255; }
        CompareWhole(Recipe() with { Scene = scene, Placed = [new PlacedSource(second, sw, sh, layer)] });
    }

    /// <summary>FrameProcessor end to end, real segmentation included: GPU path against the CPU path.</summary>
    private void CompareWhole(FrameRecipe r)
    {
        if (GpuEffects.Shared is null) { log.WriteLine("No Direct3D 11 GPU: skipped."); return; }
        if (!File.Exists(PersonSegmenter.ModelPath(SegmentationQuality.Fast))) { log.WriteLine("No model: skipped."); return; }
        var raw = Frame();
        using var bmp = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(raw),
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, W, H, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        bool was = FrameProcessor.UseGpu;
        try
        {
            FrameProcessor.UseGpu = true;
            using var gb = new BackgroundPipeline();
            var g = FrameProcessor.ProcessPixels(bmp, r, gb);
            FrameProcessor.UseGpu = false;
            using var cb = new BackgroundPipeline();
            var c = FrameProcessor.ProcessPixels(bmp, r, cb);
            Assert.Null(gb.LastError);
            Assert.Equal((c.Width, c.Height), (g.Width, g.Height));
            if (!r.Background.NeedsMask) { Diff(c.Pixels, g.Pixels); return; }
            // Two runs of the model (two DirectML sessions) can differ in the last bits, and the edge ramp turns that
            // into visible steps on a few outline pixels, so only a thin band there may differ.
            int off = 0; long sum = 0;
            for (int i = 0; i < c.Pixels.Length; i++) { int d = Math.Abs(c.Pixels[i] - g.Pixels[i]); sum += d; if (d > 1) off++; }
            log.WriteLine($"mean diff {(double)sum / c.Pixels.Length:F3}, {off} of {c.Pixels.Length} channels differ by more than 1");
            Assert.True(off <= c.Pixels.Length / 200, $"{off} channels differ by more than 1");
            Assert.True(sum < c.Pixels.Length / 20, "mean difference above 0.05 of a level");
        }
        finally { FrameProcessor.UseGpu = was; }
    }

    private void Diff(byte[] c, byte[] g)
    {
        Assert.Equal(c.Length, g.Length);
        int max = 0, off = 0;
        for (int i = 0; i < c.Length; i++)
        {
            int d = Math.Abs(c[i] - g[i]);
            max = Math.Max(max, d);
            if (d > 1) off++;
        }
        log.WriteLine($"max diff {max}, {off} of {c.Length} channels differ by more than 1");
        Assert.True(max <= 2, $"max channel difference {max}");
        Assert.True(off <= c.Length / 1000, $"{off} channels differ by more than 1");
    }

    private static FrameRecipe Recipe(BackgroundSettings? bg = null, ImageFilters.Adjustments? adj = null) =>
        FrameRecipe.Passthrough with { Background = bg ?? BackgroundSettings.Off, Adjustments = adj ?? ImageFilters.Adjustments.Neutral };

    private void Compare(FrameRecipe r, byte[]? replacement = null)
    {
        var gpu = GpuEffects.Shared;
        if (gpu is null) { log.WriteLine("No Direct3D 11 GPU: skipped."); return; }
        var px = Frame();
        var mask = Mask();
        var g = gpu.Process((byte[])px.Clone(), W, H, r, mask, replacement);
        Assert.NotNull(g);
        var c = Cpu((byte[])px.Clone(), r, mask, replacement);
        Assert.Equal(c.Length, g!.Length);
        int max = 0, off = 0;
        for (int i = 0; i < c.Length; i++)
        {
            int d = Math.Abs(c[i] - g[i]);
            max = Math.Max(max, d);
            if (d > 1) off++;
        }
        log.WriteLine($"max diff {max}, {off} of {c.Length} channels differ by more than 1 ({gpu.AdapterName})");
        Assert.True(max <= 2, $"max channel difference {max}");
        Assert.True(off <= c.Length / 1000, $"{off} channels differ by more than 1");
    }

    /// <summary>The CPU path for the same steps as <see cref="GpuEffects.Process"/>, as FrameProcessor runs it.</summary>
    private static byte[] Cpu(byte[] px, FrameRecipe r, PersonMask mask, byte[]? replacement)
    {
        var alpha = MaskOps.Matte(mask, px, W, H);
        if (r.Background.NeedsMask) BackgroundEffects.Apply(px, W, H, alpha, r.Background, replacement);
        if (r.BehindOverlay is { } bo) SceneCompositor.BlendBehind(px, bo, alpha);
        ImageFilters.Apply(px, W, H, r.Adjustments);
        if (r.Scene is { } scene) px = SceneCompositor.Compose(px, W, H, scene, r.Overlay, r.UnderOverlay);
        return px;
    }

    /// <summary>A textured room with a skin-toned "person" in the middle.</summary>
    private static byte[] Frame()
    {
        var px = Pattern(1);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float dx = (x - W / 2f) / (W * 0.2f), dy = (y - H * 0.55f) / (H * 0.4f);
                if (dx * dx + dy * dy >= 1) continue;
                int i = (y * W + x) * 4;
                px[i] = (byte)(120 + (x * 3 + y) % 20); px[i + 1] = (byte)(150 + (x + y * 2) % 15); px[i + 2] = (byte)(205 + x % 10);
            }
        return px;
    }

    private static byte[] Pattern(int seed)
    {
        var rng = new Random(seed);
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            int p = i / 4, x = p % W, y = p / W;
            px[i] = (byte)((x * 5 + y * 3 + rng.Next(30)) & 255);
            px[i + 1] = (byte)((x * 2 + y * 7) & 255);
            px[i + 2] = (byte)((x * y / 50 + rng.Next(20)) & 255);
            px[i + 3] = 255;
        }
        return px;
    }

    /// <summary>A soft model-resolution mask over the person, with fractional edges.</summary>
    private static PersonMask Mask()
    {
        const int mw = 64, mh = 48;
        var a = new float[mw * mh];
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                float dx = (x + 0.5f - mw / 2f) / (mw * 0.2f), dy = (y + 0.5f - mh * 0.55f) / (mh * 0.4f);
                a[y * mw + x] = Math.Clamp(1.6f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
            }
        return new PersonMask(a, mw, mh);
    }

    /// <summary>A premultiplied overlay: an opaque bar, a half-transparent box and empty space.</summary>
    private static byte[] Overlay()
    {
        var o = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                if (y > H - 20) { o[i] = 200; o[i + 1] = 40; o[i + 2] = 10; o[i + 3] = 255; }
                else if (x < 100 && y < 80) { o[i] = 60; o[i + 1] = 90; o[i + 2] = 30; o[i + 3] = 128; }
            }
        return o;
    }
}
