using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;
using Windows.Graphics.Imaging;

namespace OpenKamera_App;

// Auto-framing and Smart zoom: a face detector runs a few times a second on a small copy of the camera
// frame, and AutoFramer turns the face box into a calm zoom/pan that drives the normal framing transform.
public sealed partial class MainPage
{
    private volatile FramingMode _framingMode = FramingMode.Manual;
    private readonly AutoFramer _framer = new();
    private FaceTracker? _faceTracker;
    private int _detecting;
    private DateTime _lastDetect = DateTime.MinValue;

    private void InitFraming()
    {
        bool ok = FaceTracker.IsSupported;
        FramingAuto.IsEnabled = FramingSmart.IsEnabled = ok;
        if (!ok) FramingAuto.SetValue(ToolTipService.ToolTipProperty, "Face detection isn't available on this PC");
    }

    private void FramingMode_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var item = sender.SelectedItem;
        _framingMode = item == FramingAuto ? FramingMode.Auto : item == FramingSmart ? FramingMode.Smart : FramingMode.Manual;
        // Auto keeps head and shoulders; Smart moves in closer and follows a little quicker.
        (_framer.TargetFaceShare, _framer.MaxZoom, _framer.Follow) = _framingMode == FramingMode.Smart ? (0.42, 3.0, 0.18) : (0.30, 2.0, 0.12);
        bool manual = _framingMode == FramingMode.Manual;
        FrZoom.IsEnabled = FrPanX.IsEnabled = FrPanY.IsEnabled = manual;
        if (manual) { _framer.Reset(); UpdateTransform(); }
        else Status(_framingMode == FramingMode.Smart ? "Smart zoom on: keeps your face close and centred" : "Auto-framing on: keeps you in the picture");
    }

    /// <summary>Called from the frame thread. Starts a detection at most ~6 times a second, never two at once.</summary>
    private void TrackFace(SoftwareBitmap bmp)
    {
        if (_framingMode == FramingMode.Manual && !_guardOn) return;
        var now = DateTime.UtcNow;
        if (now - _lastDetect < TimeSpan.FromMilliseconds(160) || Interlocked.Exchange(ref _detecting, 1) == 1) return;
        _lastDetect = now;
        SoftwareBitmap copy;
        try { copy = SoftwareBitmap.Copy(bmp); }
        catch { Volatile.Write(ref _detecting, 0); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                _faceTracker ??= new FaceTracker();
                var face = await _faceTracker.DetectAsync(copy);
                if (_guardOn) GuardUpdate(_faceTracker.LastCount);
                var t = _transform;
                // Face and crop are both in raw camera coordinates, so mirroring needs no correction.
                var (zoom, px, py) = _framer.Update(face, DateTime.UtcNow);
                if (_framingMode == FramingMode.Manual) return;
                _transform = t with { Zoom = zoom, PanX = px, PanY = py };
                DispatcherQueue.TryEnqueue(() =>
                {
                    ZoomLabel.Text = $"{zoom:0.0}×";
                    UpdateMinimap();
                });
            }
            catch { /* a failed detection just skips one update */ }
            finally { copy.Dispose(); Volatile.Write(ref _detecting, 0); }
        });
    }
}

public sealed partial class MainPage
{
    private readonly Stabilizer _stabilizer = new();
    private volatile bool _stabilise;

    private byte[]? _stabBuf;

    /// <summary>
    /// Measures shake on the raw camera frame (before our own crop), so the correction never chases itself.
    /// </summary>
    private void StabiliseMeasure(SoftwareBitmap bmp)
    {
        if (!_stabilise) return;
        try
        {
            using var bgra = bmp.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? null
                : SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var src = bgra ?? bmp;
            int len = src.PixelWidth * src.PixelHeight * 4;
            if (_stabBuf?.Length != len) _stabBuf = new byte[len];
            src.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(_stabBuf));
            _stabilizer.Update(_stabBuf, src.PixelWidth, src.PixelHeight);
        }
        catch { /* skip a frame's measurement */ }
    }

    private void StabiliseToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _stabilizer.Reset();
        _stabilise = StabiliseSwitch.IsOn;
        Status(_stabilise ? "Steady picture on" : "Steady picture off");
    }
}
