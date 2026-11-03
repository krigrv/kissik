using OpenKamera.Core;
using Windows.Media.Editing;
using Windows.Storage;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>Recording straight from GPU textures: the texture holds the baked frame, and the MP4 plays back.</summary>
[Collection("Timing")]
[Trait("Category", "Hardware")] // real-time encoding needs a real GPU; cloud machines only have a software one
public class GpuRecorderTests(ITestOutputHelper log)
{
    private const int W = 640, H = 360, Fps = 30;

    [Fact]
    public void TextureHoldsTheBakedFrame()
    {
        if (GpuEffects.Shared is not { VideoSupport: true } gpu) { log.WriteLine("No video-capable GPU: skipped."); return; }
        var px = Frame(3);
        var o = gpu.ProcessOutput(px, W, H, FrameRecipe.Passthrough, null, new OutputBake(Shutter: ShutterMode.Blur, Texture: true));
        using var tex = o!.Baked!.Texture;
        Assert.NotNull(tex);
        Assert.Equal(o.Baked.Pixels, gpu.ReadTexture(tex!));
    }

    [Fact]
    public void TexturesOnlyNoBytes()
    {
        if (GpuEffects.Shared is not { VideoSupport: true } gpu) { log.WriteLine("No video-capable GPU: skipped."); return; }
        var o = gpu.ProcessOutput(Frame(4), W, H, FrameRecipe.Passthrough, null, new OutputBake(Texture: true, Bytes: false));
        Assert.Null(o!.Baked!.Pixels);
        o.Baked.Texture!.Dispose();
    }

    [Fact]
    public async Task RecordsTexturesToAPlayableMp4()
    {
        if (!GpuRecorder.Available) { log.WriteLine("No video-capable GPU: skipped."); return; }
        var gpu = GpuEffects.Shared!;
        string path = TempMp4();
        try
        {
            var rec = new GpuRecorder(W, H, Fps, withAudio: true);
            await rec.StartAsync(path);
            for (int i = 0; i < 60; i++)
            {
                var o = gpu.ProcessOutput(Frame(i), W, H, FrameRecipe.Passthrough, null, new OutputBake(Texture: true, Bytes: false));
                var t = TimeSpan.FromSeconds(i / (double)Fps);
                rec.PushTexture(o!.Baked!.Texture!, t);
                rec.PushAudio(new float[AudioEngine.SampleRate / Fps * AudioEngine.Channels], t);
                await Task.Delay(5); // the encoder keeps up, as it does live
            }
            await rec.StopAsync();
            await rec.DisposeAsync();
            log.WriteLine($"{rec.FramesWritten} frames, {rec.FramesDropped} dropped, {rec.AudioBlocksWritten} audio blocks, {new FileInfo(path).Length} bytes");
            Assert.True(rec.FramesWritten >= 55, $"{rec.FramesWritten} frames written");
            var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
            Assert.InRange(clip.OriginalDuration.TotalSeconds, 1.7, 2.3);
            var props = clip.GetVideoEncodingProperties();
            Assert.Equal((uint)W, props.Width); Assert.Equal((uint)H, props.Height);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task RecordsBytesToo()
    {
        if (!GpuRecorder.Available) { log.WriteLine("No video-capable GPU: skipped."); return; }
        string path = TempMp4();
        try
        {
            var rec = new GpuRecorder(W + 1, H + 1, Fps); // odd sizes are trimmed to even
            await rec.StartAsync(path);
            for (int i = 0; i < 30; i++) { rec.Push(Frame(i, W + 1, H + 1), W + 1, H + 1, TimeSpan.FromSeconds(i / (double)Fps)); await Task.Delay(5); }
            await rec.StopAsync();
            await rec.DisposeAsync();
            var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
            Assert.InRange(clip.OriginalDuration.TotalSeconds, 0.8, 1.2);
            Assert.Equal((uint)W, clip.GetVideoEncodingProperties().Width);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task SegmentedRecordingOnTheGpuJoinsItsPieces()
    {
        if (!GpuRecorder.Available) { log.WriteLine("No video-capable GPU: skipped."); return; }
        var gpu = GpuEffects.Shared!;
        string path = TempMp4();
        var rec = new SegmentedRecorder(W, H, Fps) { SegmentLength = TimeSpan.FromSeconds(0.5), PreferGpu = true };
        try
        {
            await rec.StartAsync(path);
            Assert.True(rec.TakesTextures);
            for (int i = 0; i < 45; i++)
            {
                var o = gpu.ProcessOutput(Frame(i), W, H, FrameRecipe.Passthrough, null, new OutputBake(Texture: true, Bytes: false));
                rec.PushTexture(o!.Baked!.Texture!, TimeSpan.FromSeconds(i / (double)Fps));
                await Task.Delay(10);
            }
            await rec.StopAsync();
            log.WriteLine($"{rec.Parts} pieces, {rec.FramesWritten} frames");
            Assert.True(rec.OnGpu);
            Assert.True(rec.Parts >= 2);
            var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(rec.Path));
            Assert.InRange(clip.OriginalDuration.TotalSeconds, 1.2, 1.8);
        }
        finally { try { File.Delete(rec.Path); } catch { } }
    }

    [Fact]
    public async Task ColoursSurviveTheEncoder()
    {
        if (!GpuRecorder.Available) { log.WriteLine("No video-capable GPU: skipped."); return; }
        var gpu = GpuEffects.Shared!;
        string path = TempMp4();
        try
        {
            var solid = new byte[W * H * 4];
            for (int i = 0; i < solid.Length; i += 4) { solid[i] = 40; solid[i + 1] = 120; solid[i + 2] = 220; solid[i + 3] = 255; }
            var rec = new GpuRecorder(W, H, Fps);
            await rec.StartAsync(path);
            for (int i = 0; i < 20; i++)
            {
                var o = gpu.ProcessOutput(solid, W, H, FrameRecipe.Passthrough, null, new OutputBake(Texture: true, Bytes: false));
                rec.PushTexture(o!.Baked!.Texture!, TimeSpan.FromSeconds(i / (double)Fps));
                await Task.Delay(5);
            }
            await rec.StopAsync();
            await rec.DisposeAsync();
            var comp = new MediaComposition();
            comp.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path)));
            using var thumb = await comp.GetThumbnailAsync(TimeSpan.FromSeconds(0.3), W, H, VideoFramePrecision.NearestFrame);
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumb);
            using var bmp = await dec.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(px));
            int c = (bmp.PixelHeight / 2 * bmp.PixelWidth + bmp.PixelWidth / 2) * 4;
            log.WriteLine($"decoded B {px[c]} G {px[c + 1]} R {px[c + 2]}");
            Assert.InRange(px[c], 28, 52); Assert.InRange(px[c + 1], 108, 132); Assert.InRange(px[c + 2], 208, 232);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>
    /// As the app records a webcam: 720p30 NV12 camera frames through the GPU frame path, the finished frame handed to
    /// the recorder as a texture, and the audio mix pushed from its own thread every 10 ms. 15 seconds must give about
    /// 450 frames, and Stop must finish and join the file.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)] // the mix goes quiet after a second (mic unplugged, audio engine stalled)
    public async Task FifteenSecondsOfCameraNv12At30(bool withAudio, bool audioStops)
    {
        if (!GpuRecorder.Available) { log.WriteLine("No video-capable GPU: skipped."); return; }
        const int cw = 1280, ch = 720, fps = 30;
        var rgb = Frame(0, cw, ch);
        using var src = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(rgb),
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, cw, ch, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        using var nv12 = Windows.Graphics.Imaging.SoftwareBitmap.Convert(src, Windows.Graphics.Imaging.BitmapPixelFormat.Nv12);
        string path = TempMp4();
        var rec = new SegmentedRecorder(cw, ch, fps, withAudio) { PreferGpu = true };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var stopAudio = new CancellationTokenSource();
        try
        {
            await rec.StartAsync(path);
            Assert.True(rec.OnGpu);
            var audio = Task.Run(async () =>
            {
                var block = new float[AudioEngine.SampleRate / 100 * AudioEngine.Channels];
                while (!stopAudio.IsCancellationRequested)
                {
                    if (!audioStops || clock.Elapsed < TimeSpan.FromSeconds(1)) rec.PushAudio(block, clock.Elapsed);
                    await Task.Delay(10);
                }
            });
            var bake = new OutputBake(Texture: true, Bytes: false);
            var recipe = FrameRecipe.Passthrough with { OutputWidth = cw, OutputHeight = ch };
            for (int i = 0; i < 15 * fps; i++)
            {
                var due = TimeSpan.FromSeconds(i / (double)fps);
                while (clock.Elapsed < due) await Task.Delay(1);
                var o = FrameProcessor.Process(nv12, recipe, null, bake, (1920, 1080));
                Assert.NotNull(o.Baked?.Texture);
                rec.PushTexture(o.Baked!.Texture!, clock.Elapsed);
            }
            stopAudio.Cancel(); await audio;
            var stop = rec.StopAsync();
            Assert.True(await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(30))) == stop, "Stop did not finish");
            await stop;
            log.WriteLine($"{rec.FramesWritten} frames written, {rec.FramesDropped} dropped, {rec.Parts} pieces, {new FileInfo(rec.Path).Length} bytes");
            Assert.InRange(rec.FramesWritten, 440, 450);
            Assert.False(Directory.Exists(rec.Path + SegmentedRecorder.PartsSuffix), "the pieces were not joined");
            var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(rec.Path));
            // Every frame must be in the file: the encoder used to drop ~3% on capture jitter, and the MP4 was cut where
            // the audio ended.
            Assert.InRange(clip.OriginalDuration.TotalSeconds, 14.9, 15.1);
        }
        finally
        {
            stopAudio.Cancel();
            try { File.Delete(rec.Path); } catch { }
            try { Directory.Delete(rec.Path + SegmentedRecorder.PartsSuffix, true); } catch { }
        }
    }

    private static string TempMp4() => Path.Combine(Path.GetTempPath(), $"kissik-gpu-rec-{Guid.NewGuid():N}.mp4");

    private static byte[] Frame(int i, int w = W, int h = H)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                px[p] = (byte)(x + i * 4); px[p + 1] = (byte)(y * 2); px[p + 2] = (byte)((x ^ y) + i); px[p + 3] = 255;
            }
        return px;
    }
}
