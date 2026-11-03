using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace OpenKamera.Core;

/// <summary>Everything that shapes the output image, captured as one immutable snapshot per frame.</summary>
/// <param name="Overlay">Premultiplied BGRA of the scene's non-camera layers at exactly the output size.</param>
public sealed record FrameRecipe(
    ImageFilters.Adjustments Adjustments, FrameTransform Transform,
    BackgroundSettings Background, int? OutputWidth = null, int? OutputHeight = null,
    SceneDocument? Scene = null, byte[]? Overlay = null, byte[]? BehindOverlay = null, byte[]? UnderOverlay = null,
    IReadOnlyList<PlacedSource>? Placed = null)
{
    public static readonly FrameRecipe Passthrough = new(ImageFilters.Adjustments.Neutral, FrameTransform.Identity, BackgroundSettings.Off);
}

/// <summary>Another picture placed into a scene layer on top of the composed frame (a second camera).</summary>
public sealed record PlacedSource(byte[] Pixels, int Width, int Height, SceneLayer Layer);

/// <summary>
/// What the outputs (meeting camera, stream, recording) get on top of the processed frame: the front overlay at a
/// given size, the scene cross-fade position (0..1, null when not fading), burned-in captions and the privacy shutter.
/// </summary>
public sealed record OutputBake(
    Func<int, int, byte[]?>? Front = null, double? FadeT = null,
    byte[]? Caption = null, int CaptionWidth = 0, int CaptionHeight = 0, ShutterMode Shutter = ShutterMode.Off,
    (int Width, int Height)? FitWithin = null, SceneTransition Transition = SceneTransition.Fade,
    bool Texture = false, bool Bytes = true);

/// <summary>The baked output frame; smaller than the processed frame when <see cref="OutputBake.FitWithin"/> shrank it.
/// <see cref="Pixels"/> is null when only a <see cref="Texture"/> was asked for (OutputBake.Bytes false).</summary>
public sealed record BakedFrame(byte[]? Pixels, int Width, int Height, GpuFrame? Texture = null);

/// <summary>
/// A finished output frame on the GPU, for the hardware encoder. Whoever ends up with it disposes it (the encoder
/// keeps its own reference while it works).
/// </summary>
public sealed class GpuFrame(Vortice.Direct3D11.ID3D11Texture2D texture, int width, int height) : IDisposable
{
    internal Vortice.Direct3D11.ID3D11Texture2D Texture { get; } = texture;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public void Dispose() => Texture.Dispose();
}

/// <summary>A processed frame; Baked is the output bake, or null when it wasn't asked for or the CPU path ran.</summary>
public sealed record FrameOutput(byte[] Pixels, int Width, int Height, BakedFrame? Baked);

/// <summary>Runs framing → output fit → background → Enhance → scene. Used for preview, snapshots and recording.</summary>
public static class FrameProcessor
{
    /// <summary>
    /// Run the effects on the GPU (Direct3D 11 compute) when one is available; the CPU code stays as the fallback.
    /// Set KISSIK_CPU_EFFECTS=1 to force the CPU path.
    /// </summary>
    public static bool UseGpu { get; set; } = Environment.GetEnvironmentVariable("KISSIK_CPU_EFFECTS") is null;

    /// <summary>True when frames are currently going through the GPU path.</summary>
    public static bool GpuActive => UseGpu && GpuEffects.Shared is not null;

    /// <summary>Processes a frame into BGRA pixels at the output size.</summary>
    public static (byte[] Pixels, int Width, int Height) ProcessPixels(SoftwareBitmap frame, FrameRecipe r, BackgroundPipeline? background)
    {
        var o = Process(frame, r, background, null);
        return (o.Pixels, o.Width, o.Height);
    }

    /// <summary>
    /// Processes a frame and, on the GPU, also bakes the outputs' layers (<paramref name="bake"/>) before reading back.
    /// NV12 / YUY2 camera frames go to the GPU as they are; the CPU path converts them first.
    /// </summary>
    /// <param name="previewWithin">On the GPU, return the processed frame shrunk to fit this (the bake keeps full size).</param>
    public static FrameOutput Process(SoftwareBitmap frame, FrameRecipe r, BackgroundPipeline? background, OutputBake? bake,
        (int Width, int Height)? previewWithin = null)
    {
        if (UseGpu && !r.Background.GreenScreen && YuvConvert.IsYuv(frame) && GpuEffects.Shared is { } g && g.ProcessCamera(frame, r, background, bake, previewWithin) is { } fast)
            return fast;
        var bgra = frame.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? frame
            : SoftwareBitmap.Convert(frame, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        try
        {
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyToBuffer(pixels.AsBuffer());
            if (UseGpu && !r.Background.GreenScreen && GpuEffects.Shared is { } gpu && gpu.ProcessOutput(pixels, w, h, r, background, bake, previewWithin) is { } done)
                return done;
            (pixels, w, h) = r.Transform.Apply(pixels, w, h);
            // Fit before the expensive steps so they run at output size, not camera size.
            if (r.OutputWidth is int ow && r.OutputHeight is int oh) (pixels, w, h) = OutputFit.Cover(pixels, w, h, ow, oh);
            background?.NewFrame();
            byte[]? person = r.BehindOverlay is { } bo0 && bo0.Length == pixels.Length && background is not null ? (byte[])pixels.Clone() : null;
            if (background is not null && r.Background.NeedsMask) background.Apply(pixels, w, h, r.Background);
            // Layers marked "behind me": drawn over the background, then the person is put back on top.
            if (person is not null && r.BehindOverlay is { } bo)
            {
                var alpha = background!.PersonMask(person, w, h, r.Background);
                SceneCompositor.BlendBehind(pixels, bo, alpha);
            }
            ImageFilters.Apply(pixels, w, h, r.Adjustments); // camera only: overlays keep their true colours
            if (r.Scene is { } scene) pixels = SceneCompositor.Compose(pixels, w, h, scene, r.Overlay, r.UnderOverlay);
            foreach (var src in r.Placed ?? []) SceneCompositor.PlaceSource(pixels, w, h, src.Pixels, src.Width, src.Height, src.Layer);
            return new FrameOutput(pixels, w, h, null);
        }
        finally { if (!ReferenceEquals(bgra, frame)) bgra.Dispose(); }
    }

    /// <summary>Returns a new Bgra8/Premultiplied bitmap; the caller owns (and disposes) it.</summary>
    public static SoftwareBitmap Process(SoftwareBitmap frame, FrameRecipe r, BackgroundPipeline? background)
    {
        var o = Process(frame, r, background, null);
        return ToBitmap(o.Pixels, o.Width, o.Height);
    }

    public static SoftwareBitmap ToBitmap(byte[] pixels, int w, int h) =>
        SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
}
