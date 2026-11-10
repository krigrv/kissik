using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;

namespace OpenKamera_App;

// Scene cards show your real camera picture: one small shared image, refreshed about once a second.
public sealed partial class MainPage
{
    private readonly WriteableBitmap _thumbSource = new(320, 180);
    /// <summary>The whole camera image (before framing) for the framing minimap.</summary>
    private WriteableBitmap _rawThumb = new(320, 180);
    private byte[]? _rawBuf;
    private object? _lastRaw;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _thumbTimer;
    private bool _thumbsLive, _thumbBusy;
    private int _thumbTick;
    private byte[]? _thumbFrom;

    private void StartSceneThumbs()
    {
        _thumbTimer = DispatcherQueue.CreateTimer();
        // Scene cards are live (~30 a second, one shared small image); the framing minimap ~6 a second (it copies the
        // full camera frame). With "reduce motion" on: the minimap once a second and scene cards every 10 seconds.
        _thumbTimer.Interval = ReduceMotion ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(33);
        _thumbTimer.Tick += async (_, _) =>
        {
            bool live = _session is not null && _lastFrame is not null;
            if (live != _thumbsLive) { _thumbsLive = live; RefreshSceneRail(_scene.Id, reload: false); } // swap placeholders ↔ picture
            if (!live || _thumbBusy || _lastFrame is not { } f) return;
            _thumbBusy = true;
            try
            {
                ++_thumbTick;
                if (!ReduceMotion || _thumbTick % 10 == 0)
                {
                    var (small, _, _) = await Task.Run(() => OutputFit.Cover(f.Px, f.W, f.H, 320, 180));
                    using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(_thumbSource.PixelBuffer))
                        await stream.WriteAsync(small.AsMemory(0, Math.Min(small.Length, (int)_thumbSource.PixelBuffer.Capacity)));
                    _thumbSource.Invalidate();
                    _thumbFrom = f.Px;
                }
                if (Minimap.ActualWidth > 0 && Minimap.XamlRoot is not null) await UpdateMinimapPictureAsync();
            }
            catch { /* skip a refresh */ }
            finally { _thumbBusy = false; }
        };
        _thumbTimer.Start();
    }

    private async Task UpdateMinimapPictureAsync()
    {
        byte[] buf; int w, h;
        var raw = _session?.Latest;
        if (raw is not null && !ReferenceEquals(raw, _lastRaw))
        {
            _lastRaw = raw; w = raw.PixelWidth; h = raw.PixelHeight;
            try
            {
                if (_rawBuf?.Length != w * h * 4) _rawBuf = new byte[w * h * 4];
                var target = _rawBuf;
                await Task.Run(() =>
                {
                    // The camera may deliver NV12 / YUY2 for the GPU path.
                    using var bgra = raw.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? null
                        : SoftwareBitmap.Convert(raw, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    (bgra ?? raw).CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(target));
                });
            }
            catch { return; } // the frame was replaced while copying
            buf = _rawBuf;
        }
        // No new camera frame since the last refresh: keep what's shown.
        else return;
        // The preview box takes the camera's own shape (4:3 cameras would otherwise look stretched sideways).
        if (Minimap.ActualWidth > 0 && w > 0 && h > 0)
        {
            double want = Math.Round(Minimap.ActualWidth * h / w);
            if (Math.Abs(Minimap.Height - want) > 1) { Minimap.Height = want; UpdateMinimap(); }
        }
        // The small copy keeps the camera's shape. A phone held upright used to be squeezed into a wide 320×180
        // copy and stretched back, which left 180 rows for a tall preview: blocky and soft.
        int tw = w >= h ? 320 : Math.Max(2, 320 * w / h), th = w >= h ? Math.Max(2, 320 * h / w) : 320;
        if (_rawThumb.PixelWidth != tw || _rawThumb.PixelHeight != th)
        {
            _rawThumb = new WriteableBitmap(tw, th);
            Minimap.Background = null;
        }
        var (small, _, _) = await Task.Run(() => OutputFit.Stretch(buf, w, h, tw, th));
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(_rawThumb.PixelBuffer))
            await stream.WriteAsync(small.AsMemory(0, Math.Min(small.Length, (int)_rawThumb.PixelBuffer.Capacity)));
        _rawThumb.Invalidate();
        if (Minimap.Background is not Microsoft.UI.Xaml.Media.ImageBrush)
            Minimap.Background = new Microsoft.UI.Xaml.Media.ImageBrush { ImageSource = _rawThumb, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
    }
}
