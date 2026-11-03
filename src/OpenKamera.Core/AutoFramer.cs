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
/// Frames people the way a camera operator would. Pure maths; no camera or UI.
/// <list type="bullet">
/// <item>Composition: eyes on the upper third with natural headroom; groups are framed together.</item>
/// <item>Motion: a critically damped spring advanced every video frame (smooth ease-in and ease-out, no steps),
/// with hysteresis: it only starts moving when you have clearly moved, then settles fully before resting.</item>
/// </list>
/// Detections arrive a few times a second through <see cref="SetFaces"/>; <see cref="Step"/> runs per frame.
/// </summary>
public sealed class AutoFramer
{
    /// <summary>Face height as a share of the visible picture height (single person).</summary>
    public double TargetFaceShare { get; set; } = 0.30;
    /// <summary>Where the face centre sits vertically in the picture (0 = top). 0.40 puts the eyes near the upper third.</summary>
    public double FaceLine { get; set; } = 0.40;
    public double MaxZoom { get; set; } = 2.0;
    /// <summary>How quickly the frame follows: spring speed in 1/s (higher = snappier). About 1 s to settle at 4.</summary>
    public double Follow { get; set; } = 4;
    /// <summary>Faces smaller than this share of the largest are ignored (people far behind you).</summary>
    public double GroupMinShare { get; set; } = 0.45;

    public double Zoom { get; private set; } = 1;
    public double PanX { get; private set; }
    public double PanY { get; private set; }

    private double _tz = 1, _tx, _ty;          // target
    private double _vz, _vx, _vy;              // spring velocities
    private bool _moving;
    private DateTime _lastSeen = DateTime.MinValue, _lastStep = DateTime.MinValue;

    public void Reset() { Zoom = _tz = 1; PanX = PanY = _tx = _ty = 0; _vz = _vx = _vy = 0; _moving = false; }

    /// <summary>The newest detection (empty = nobody). Sets where the frame should go; <see cref="Step"/> moves it there.</summary>
    public void SetFaces(IReadOnlyList<FaceBox> faces, DateTime now)
    {
        if (faces.Count == 0)
        {
            if (now - _lastSeen > TimeSpan.FromSeconds(2)) (_tz, _tx, _ty) = (1, 0, 0); // nobody for a while: back to the full picture
            return;                                                                       // brief miss (blink, turn): hold
        }
        _lastSeen = now;
        double big = faces.Max(f => f.H);
        var group = faces.Where(f => f.H >= big * GroupMinShare).ToList();
        double l = group.Min(f => f.X), r = group.Max(f => f.X + f.W), t = group.Min(f => f.Y), b = group.Max(f => f.Y + f.H);
        double h = b - t, w = r - l, faceH = group.Average(f => f.H);
        // One person: face at the target size. A group: everyone fits across with shoulder room either side.
        double zoom = TargetFaceShare / Math.Max(0.02, faceH);
        if (group.Count > 1) zoom = Math.Min(zoom, 0.72 / Math.Max(0.05, w + faceH));
        _tz = Math.Clamp(Math.Min(zoom, 0.8 / Math.Max(0.05, h + faceH)), 1, MaxZoom);
        (_tx, _ty) = PansFor((l + r) / 2, (t + b) / 2, _tz);
    }

    /// <summary>Advances the motion by the time since the last call. Call once per video frame.</summary>
    public (double Zoom, double PanX, double PanY) Step(DateTime now)
    {
        double dt = _lastStep == DateTime.MinValue ? 1 / 30.0 : Math.Clamp((now - _lastStep).TotalSeconds, 0, 0.1);
        _lastStep = now;
        // Hysteresis: start only for a clear change (no hunting while you breathe or nod); once moving, finish the move.
        double err = Math.Max(Math.Abs(_tz - Zoom) / 0.06, Math.Max(Math.Abs(_tx - PanX), Math.Abs(_ty - PanY)) / 0.05);
        if (!_moving && err > 1) _moving = true;
        if (_moving && err < 0.08 && Math.Abs(_vz) + Math.Abs(_vx) + Math.Abs(_vy) < 0.02) _moving = false;
        if (!_moving) { _vz = _vx = _vy = 0; return (Zoom, PanX, PanY); }
        double wn = Follow;
        Zoom = Spring(Zoom, _tz, ref _vz, wn, dt);
        PanX = Spring(PanX, _tx, ref _vx, wn, dt);
        PanY = Spring(PanY, _ty, ref _vy, wn, dt);
        return (Zoom, PanX, PanY);
    }

    /// <summary>Critically damped spring step (no overshoot, smooth start and stop).</summary>
    private static double Spring(double x, double target, ref double v, double wn, double dt)
    {
        double a = wn * wn * (target - x) - 2 * wn * v;
        v += a * dt;
        return x + v * dt;
    }

    /// <summary>Convenience for one face: set it and advance one step.</summary>
    public (double Zoom, double PanX, double PanY) Update(FaceBox? face, DateTime now)
    {
        SetFaces(face is { } f ? [f] : [], now);
        return Step(now);
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
        var all = await DetectAllAsync(frame);
        return all.Count == 0 ? null : all.MaxBy(f => f.W * f.H);
    }

    /// <summary>Every face in the frame, normalised.</summary>
    public async Task<IReadOnlyList<FaceBox>> DetectAllAsync(SoftwareBitmap frame)
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
        return faces.Select(x => new FaceBox(x.FaceBox.X / (double)w, x.FaceBox.Y / (double)h, x.FaceBox.Width / (double)w, x.FaceBox.Height / (double)h)).ToList();
    }

    /// <summary>How many faces the last detection found (privacy guard).</summary>
    public int LastCount { get; private set; }
}
