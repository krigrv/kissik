using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;

namespace OpenKamera_App;

// Scene cards show your real camera picture: one small shared image, refreshed about once a second.
public sealed partial class MainPage
{
    private readonly WriteableBitmap _thumbSource = new(320, 180);
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
            }
            catch { /* skip a refresh */ }
            finally { _thumbBusy = false; }
        };
        _thumbTimer.Start();
    }
}
