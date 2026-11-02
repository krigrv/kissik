using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;

namespace OpenKamera_App;

// Scene cards show your real camera picture: one small shared image, refreshed about once a second.
public sealed partial class MainPage
{
    private readonly WriteableBitmap _thumbSource = new(320, 180);
    /// <summary>The whole camera image (before framing) for the framing minimap.</summary>
    private readonly WriteableBitmap _rawThumb = new(320, 180);
    private byte[]? _rawBuf;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _thumbTimer;
    private bool _thumbsLive, _thumbBusy;

    private void StartSceneThumbs()
    {
        _thumbTimer = DispatcherQueue.CreateTimer();
        _thumbTimer.Interval = TimeSpan.FromSeconds(1);
        _thumbTimer.Tick += async (_, _) =>
        {
            bool live = _session is not null && _lastFrame is not null;
            if (live != _thumbsLive) { _thumbsLive = live; RefreshSceneRail(_scene.Id); } // swap placeholders ↔ picture
            if (!live || _thumbBusy || _lastFrame is not { } f) return;
            _thumbBusy = true;
            try
            {
                var (small, _, _) = await Task.Run(() => OutputFit.Stretch(f.Px, f.W, f.H, 320, 180));
                using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(_thumbSource.PixelBuffer))
                    await stream.WriteAsync(small.AsMemory(0, Math.Min(small.Length, (int)_thumbSource.PixelBuffer.Capacity)));
                _thumbSource.Invalidate();
                await UpdateMinimapPictureAsync();
            }
            catch { /* skip a refresh */ }
            finally { _thumbBusy = false; }
        };
        _thumbTimer.Start();
    }

    private async Task UpdateMinimapPictureAsync()
    {
        if (_session?.Latest is not { } raw) return;
        int w = raw.PixelWidth, h = raw.PixelHeight;
        try
        {
            if (_rawBuf?.Length != w * h * 4) _rawBuf = new byte[w * h * 4];
            raw.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(_rawBuf));
        }
        catch { return; } // the frame was replaced while copying
        var buf = _rawBuf;
        var (small, _, _) = await Task.Run(() => OutputFit.Stretch(buf, w, h, 320, 180));
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(_rawThumb.PixelBuffer))
            await stream.WriteAsync(small.AsMemory(0, Math.Min(small.Length, (int)_rawThumb.PixelBuffer.Capacity)));
        _rawThumb.Invalidate();
        if (Minimap.Background is not Microsoft.UI.Xaml.Media.ImageBrush)
            Minimap.Background = new Microsoft.UI.Xaml.Media.ImageBrush { ImageSource = _rawThumb, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
    }
}
