using System.Runtime.InteropServices.WindowsRuntime;
using OpenKamera.Core;
using Windows.Graphics.Imaging;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>Phone pictures as NV12 for the GPU: turned on the CPU (bytes only), coloured on the GPU, same result as before.</summary>
[Collection("Timing")]
public class PhoneGpuTests(ITestOutputHelper log)
{
    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void Nv12TurnedThenGpuMatchesTheCpuConversion(int rotation)
    {
        if (GpuEffects.Shared is not { } gpu) { log.WriteLine("No GPU: skipped."); return; }
        const int w = 64, h = 36, pitch = 80; // a padded decoder buffer, as Media Foundation hands it over
        var nv12 = new byte[pitch * h * 3 / 2];
        new Random(rotation + 1).NextBytes(nv12);
        var cpu = PhoneVideo.Nv12ToBgra(nv12, pitch, w, h, pitch * h, rotation, out int cw, out int chh);

        var packed = new byte[w * h * 3 / 2];
        PhoneVideo.Nv12Rotate(nv12, pitch, w, h, pitch * h, rotation, packed, out int ow, out int oh);
        Assert.Equal((cw, chh), (ow, oh));
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(packed.AsBuffer(), BitmapPixelFormat.Nv12, ow, oh);
        YuvConvert.MarkBt709(bmp);
        Assert.Equal(cpu, gpu.ConvertCamera(bmp));
    }

    [Fact]
    public void DecoderHandsOverNv12ThatMatchesItsBgra()
    {
        if (GpuEffects.Shared is not { } gpu) { log.WriteLine("No GPU: skipped."); return; }
        var bgra = Decode(nv12: false);
        var yuv = Decode(nv12: true);
        if (bgra is null || yuv is null) { log.WriteLine("No H.264 decoder: skipped."); return; }
        Assert.Equal(bgra.Count, yuv.Count);
        for (int i = 0; i < bgra.Count; i++)
        {
            Assert.True(yuv[i].Nv12);
            Assert.Equal((bgra[i].Width, bgra[i].Height), (yuv[i].Width, yuv[i].Height));
            using var bmp = SoftwareBitmap.CreateCopyFromBuffer(yuv[i].Bgra.AsBuffer(0, yuv[i].Width * yuv[i].Height * 3 / 2), BitmapPixelFormat.Nv12, yuv[i].Width, yuv[i].Height);
            YuvConvert.MarkBt709(bmp);
            Assert.Equal(bgra[i].Bgra.AsSpan(0, bgra[i].Width * bgra[i].Height * 4).ToArray(), gpu.ConvertCamera(bmp));
        }
    }

    [Fact]
    public void SessionTurnsNv12IntoAFrameTheGpuPathTakes()
    {
        if (!FrameProcessor.GpuActive) { log.WriteLine("No GPU: skipped."); return; }
        var session = new CaptureSession();
        session.StartExternalAsync().GetAwaiter().GetResult();
        SoftwareBitmap? got = null;
        session.FrameAvailable += b => got = b;
        session.PushBgra(new DecodedFrame(new byte[16 * 8 * 3 / 2], 16, 8, 0, Nv12: true));
        Assert.NotNull(got);
        Assert.Equal(BitmapPixelFormat.Nv12, got!.BitmapPixelFormat);
        Assert.True(YuvConvert.IsBt709(got));
        var o = FrameProcessor.Process(got, FrameRecipe.Passthrough, null, null);
        Assert.Equal((16, 8), (o.Width, o.Height));
    }

    /// <summary>Decodes the test clip turned 90 degrees; null without a decoder.</summary>
    private static List<DecodedFrame>? Decode(bool nv12)
    {
        H264Decoder dec;
        try { dec = H264Decoder.Create(); }
        catch (InvalidOperationException) { return null; }
        using var _ = dec;
        dec.Rotation = 90;
        dec.OutputNv12 = nv12;
        var frames = new List<DecodedFrame>();
        using var done = new ManualResetEventSlim();
        dec.FrameDecoded += f =>
        {
            // The decoder reuses its buffer: keep a copy.
            var copy = f with { Bgra = (byte[])f.Bgra.Clone() };
            lock (frames) { frames.Add(copy); if (frames.Count == 30) done.Set(); }
        };
        long t = 0;
        foreach (var (data, key) in PhoneVideo.SplitAccessUnits(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "test-320x240.h264"))))
            dec.Push(new PhoneVideoPacket(key, t += 33_333, data));
        done.Wait(TimeSpan.FromSeconds(10));
        lock (frames) return [.. frames];
    }
}
