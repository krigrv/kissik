using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace OpenKamera.Core;

/// <summary>Everything that shapes the output image, captured as one immutable snapshot per frame.</summary>
/// <param name="Overlay">Premultiplied BGRA of the scene's non-camera layers at exactly the output size.</param>
public sealed record FrameRecipe(
    ImageFilters.Adjustments Adjustments, FrameTransform Transform,
    BackgroundSettings Background, int? OutputWidth = null, int? OutputHeight = null,
    SceneDocument? Scene = null, byte[]? Overlay = null, byte[]? BehindOverlay = null, byte[]? UnderOverlay = null)
{
    public static readonly FrameRecipe Passthrough = new(ImageFilters.Adjustments.Neutral, FrameTransform.Identity, BackgroundSettings.Off);
}

/// <summary>Runs framing → output fit → background → Enhance → scene. Used for preview, snapshots and recording.</summary>
public static class FrameProcessor
{
    /// <summary>Processes a frame into BGRA pixels at the output size.</summary>
    public static (byte[] Pixels, int Width, int Height) ProcessPixels(SoftwareBitmap frame, FrameRecipe r, BackgroundPipeline? background)
    {
        var bgra = frame.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? frame
            : SoftwareBitmap.Convert(frame, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        try
        {
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyToBuffer(pixels.AsBuffer());
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
            return (pixels, w, h);
        }
        finally { if (!ReferenceEquals(bgra, frame)) bgra.Dispose(); }
    }

    /// <summary>Returns a new Bgra8/Premultiplied bitmap; the caller owns (and disposes) it.</summary>
    public static SoftwareBitmap Process(SoftwareBitmap frame, FrameRecipe r, BackgroundPipeline? background)
    {
        var (pixels, w, h) = ProcessPixels(frame, r, background);
        return ToBitmap(pixels, w, h);
    }

    public static SoftwareBitmap ToBitmap(byte[] pixels, int w, int h) =>
        SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
}
