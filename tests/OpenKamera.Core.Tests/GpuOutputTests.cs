using System.Runtime.InteropServices.WindowsRuntime;
using OpenKamera.Core;
using Windows.Graphics.Imaging;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>The GPU's camera input (NV12 / YUY2) and output bake must match their CPU counterparts exactly.</summary>
public class GpuOutputTests(ITestOutputHelper log)
{
    private const int W = 322, H = 182; // not a multiple of 4 or 8: exercises row strides, padding and edge blocks

    [Theory]
    [InlineData(BitmapPixelFormat.Nv12)]
    [InlineData(BitmapPixelFormat.Yuy2)]
    public void CameraYuvMatchesCpu(BitmapPixelFormat format)
    {
        if (GpuEffects.Shared is not { } gpu) { log.WriteLine("No Direct3D 11 GPU: skipped."); return; }
        // A smooth picture, as a camera sees it (Windows interpolates chroma, so pure noise would differ there).
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                px[i] = (byte)(x * 255 / W); px[i + 1] = (byte)(y * 255 / H); px[i + 2] = (byte)(128 + 100 * Math.Sin(x / 20.0 + y / 30.0)); px[i + 3] = 255;
            }
        using var src = SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(), BitmapPixelFormat.Bgra8, W, H, BitmapAlphaMode.Premultiplied);
        using var bmp = SoftwareBitmap.Convert(src, format);
        var cpu = YuvConvert.Read(bmp, (ptr, bytes, stride, uv) =>
        {
            var copy = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(ptr, copy, 0, bytes);
            return format == BitmapPixelFormat.Nv12 ? YuvConvert.Nv12ToBgra(copy, W, H, stride, uv) : YuvConvert.Yuy2ToBgra(copy, W, H, stride);
        });
        var g = gpu.ConvertCamera(bmp);
        Assert.NotNull(g);
        Assert.Equal(cpu, g);

        // And close to Windows' own conversion, so switching the camera to YUV doesn't change the picture.
        using var win = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var wpx = new byte[W * H * 4];
        win.CopyToBuffer(wpx.AsBuffer());
        double sum = 0;
        for (int i = 0; i < wpx.Length; i++) if (i % 4 != 3) sum += Math.Abs(wpx[i] - g![i]);
        double mean = sum / (W * H * 3);
        log.WriteLine($"{format}: mean difference from SoftwareBitmap.Convert {mean:F2} levels");
        Assert.True(mean < 3, $"mean difference {mean:F2}");
    }

    [Theory]
    [InlineData(ShutterMode.Off)]
    [InlineData(ShutterMode.Blur)]
    [InlineData(ShutterMode.Away)]
    public void OutputBakeMatchesCpu(ShutterMode shutter)
    {
        if (GpuEffects.Shared is not { } gpu) { log.WriteLine("No Direct3D 11 GPU: skipped."); return; }
        byte[] a = Opaque(1), b = Opaque(2), front = Layer(3, W, H);
        const int cw = 120, ch = 30;
        byte[] caption = Layer(4, cw, ch);
        var first = new OutputBake(Front: (w, h) => w == W && h == H ? front : null);
        var outA = gpu.ProcessOutput(a, W, H, FrameRecipe.Passthrough, null, first);
        Assert.NotNull(outA?.Baked);
        var cpuA = (byte[])a.Clone();
        SceneCompositor.BlendOver(cpuA, front);
        Assert.Equal(cpuA, outA!.Baked);

        gpu.BeginFade();
        var second = first with { FadeT = 0.3, Caption = caption, CaptionWidth = cw, CaptionHeight = ch, Shutter = shutter };
        var outB = gpu.ProcessOutput(b, W, H, FrameRecipe.Passthrough, null, second);
        Assert.Equal(b, outB!.Pixels); // the un-baked frame (preview) is untouched
        var cpuB = (byte[])b.Clone();
        SceneCompositor.BlendOver(cpuB, front);
        SceneCompositor.CrossFade(cpuB, cpuA, 0.3);
        CaptionBurn.Blend(cpuB, W, H, caption, cw, ch);
        cpuB = PrivacyShutter.Apply(cpuB, W, H, shutter);
        Assert.Equal(cpuB, outB.Baked);
    }

    [Fact]
    public void NothingToBakeReturnsTheFrame()
    {
        if (GpuEffects.Shared is not { } gpu) { log.WriteLine("No Direct3D 11 GPU: skipped."); return; }
        var o = gpu.ProcessOutput(Opaque(5), W, H, FrameRecipe.Passthrough, null, new OutputBake());
        Assert.Same(o!.Pixels, o.Baked);
    }

    private static byte[] Opaque(int seed)
    {
        var px = new byte[W * H * 4];
        new Random(seed).NextBytes(px);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        return px;
    }

    /// <summary>Premultiplied BGRA with clear, solid and part-transparent pixels.</summary>
    private static byte[] Layer(int seed, int w, int h)
    {
        var rnd = new Random(seed);
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = rnd.Next(3) switch { 0 => 0, 1 => 255, _ => rnd.Next(256) };
            for (int c = 0; c < 3; c++) px[i + c] = (byte)rnd.Next(a + 1);
            px[i + 3] = (byte)a;
        }
        return px;
    }
}
