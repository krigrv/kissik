using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace OpenKamera.Core;

public enum FramingMode { Manual, Auto, Smart }

/// <summary>A face box in normalised frame coordinates (0..1).</summary>
public readonly record struct FaceBox(double X, double Y, double W, double H)
{
    public double CentreX => X + W / 2;
    public double CentreY => Y + H / 2;
}

/// <summary>
/// Turns face positions into a smooth zoom/pan: keeps the face at a steady size, slightly above centre, with a
/// dead zone so small head movements don't make the picture swim. Pure maths; no camera or UI.
/// </summary>
public sealed class AutoFramer
{
    /// <summary>Face height as a share of the visible picture height.</summary>
    public double TargetFaceShare { get; set; } = 0.30;
    /// <summary>Where the face centre sits vertically in the picture (0 = top).</summary>
    public double FaceLine { get; set; } = 0.42;
    public double MaxZoom { get; set; } = 2.0;
    /// <summary>0..1 per update: how quickly the frame follows (lower = calmer).</summary>
    public double Follow { get; set; } = 0.12;

    public double Zoom { get; private set; } = 1;
    public double PanX { get; private set; }
    public double PanY { get; private set; }
    private DateTime _lastSeen = DateTime.MinValue;

    public void Reset() { Zoom = 1; PanX = PanY = 0; }

    /// <summary>Feeds the newest detection (null = no face). Returns the smoothed zoom and pans for FrameTransform.</summary>
    public (double Zoom, double PanX, double PanY) Update(FaceBox? face, DateTime now)
    {
        double tz, tx, ty;
        if (face is { } f)
        {
            _lastSeen = now;
            tz = Math.Clamp(TargetFaceShare / Math.Max(0.02, f.H), 1, MaxZoom);
            (tx, ty) = PansFor(f.CentreX, f.CentreY, tz);
        }
        else if (now - _lastSeen > TimeSpan.FromSeconds(2))
        {
            (tz, tx, ty) = (1, 0, 0); // nobody there for a while: ease back to the full picture
        }
        else return (Zoom, PanX, PanY); // brief miss (blink, turn): hold still

        // Dead zone: ignore tiny changes so the picture doesn't drift with every breath.
        double k = Follow;
        if (Math.Abs(tz - Zoom) > 0.04) Zoom += (tz - Zoom) * k;
        if (Math.Abs(tx - PanX) > 0.03) PanX += (tx - PanX) * k;
        if (Math.Abs(ty - PanY) > 0.03) PanY += (ty - PanY) * k;
        return (Zoom, PanX, PanY);
    }

    /// <summary>
    /// The FrameTransform pans that put (cx, cy) at the framing point of a crop zoomed by <paramref name="zoom"/>.
    /// FrameTransform: left = (1 − c)/2 × (1 + pan) where c = 1/zoom.
    /// </summary>
    public (double PanX, double PanY) PansFor(double cx, double cy, double zoom)
    {
        double c = 1 / Math.Max(1, zoom);
        if (c >= 0.999) return (0, 0);
        double wantLeft = cx - c / 2;
        double wantTop = cy - c * FaceLine;
        double half = (1 - c) / 2;
        return (Math.Clamp(wantLeft / half - 1, -1, 1), Math.Clamp(wantTop / half - 1, -1, 1));
    }
}

/// <summary>Finds the largest face in a frame with the Windows face detector (CPU, runs on a small copy).</summary>
public sealed class FaceTracker
{
    private FaceDetector? _detector;
    private const int DetectWidth = 320;

    public static bool IsSupported => FaceDetector.IsSupported;

    public async Task<FaceBox?> DetectAsync(SoftwareBitmap frame)
    {
        _detector ??= await FaceDetector.CreateAsync();
        // The detector wants Gray8 or NV12 and is faster on a small image.
        double scale = Math.Min(1.0, DetectWidth / (double)frame.PixelWidth);
        using var bgra = frame.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? SoftwareBitmap.Copy(frame)
            : SoftwareBitmap.Convert(frame, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        int w = Math.Max(16, (int)(frame.PixelWidth * scale)), h = Math.Max(16, (int)(frame.PixelHeight * scale));
        var px = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(px));
        var (small, _, _) = OutputFit.Stretch(px, bgra.PixelWidth, bgra.PixelHeight, w, h);
        var gray = new byte[w * h];
        for (int i = 0, j = 0; j < gray.Length; i += 4, j++) gray[j] = (byte)((small[i + 2] * 77 + small[i + 1] * 150 + small[i] * 29) >> 8);
        using var g8 = SoftwareBitmap.CreateCopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(gray), BitmapPixelFormat.Gray8, w, h);
        var faces = await _detector.DetectFacesAsync(g8);
        LastCount = faces.Count;
        if (faces.Count == 0) return null;
        var f = faces.MaxBy(x => x.FaceBox.Width * x.FaceBox.Height)!.FaceBox;
        return new FaceBox(f.X / (double)w, f.Y / (double)h, f.Width / (double)w, f.Height / (double)h);
    }

    /// <summary>How many faces the last detection found (privacy guard).</summary>
    public int LastCount { get; private set; }
}
