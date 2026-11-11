using OpenKamera.Core;

namespace OpenKamera_App;

// Camera lost while recording or live: the recording used to end there, and nothing resumed. Now the outputs carry
// on with a "paused" card (the last picture, dimmed, with a pause sign) until the camera is back, then continue
// with the live picture, all in the same file or stream.
public sealed partial class MainPage
{
    private System.Threading.Timer? _holdTimer;
    private byte[]? _holdCard;
    private int _holdW, _holdH, _holdBusy;

    private bool OutputsRunning => _recorder is not null || _live is not null || _replay is not null;

    private void StartCameraHold()
    {
        StopCameraHold();
        var p = _profile;
        byte[] last;
        if (_lastFrame is { } f && f.Px.Length == f.W * f.H * 4)
            last = f.W == p.Width && f.H == p.Height ? f.Px : OutputFit.Stretch(f.Px, f.W, f.H, p.Width, p.Height).Pixels;
        else
        {
            last = new byte[p.Width * p.Height * 4];
            for (int i = 3; i < last.Length; i += 4) last[i] = 255;
        }
        _holdCard = PrivacyShutter.Apply(last, p.Width, p.Height, ShutterMode.Away);
        (_holdW, _holdH) = (p.Width, p.Height);
        int every = 1000 / Math.Clamp(p.Fps, 5, 30);
        _holdTimer = new System.Threading.Timer(_ => HoldTick(), null, every, every);
    }

    private void StopCameraHold()
    {
        _holdTimer?.Dispose(); _holdTimer = null;
        _holdCard = null;
    }

    /// <summary>Timer thread: feeds the card to whatever is still running, exactly where the camera's frames would go.</summary>
    private void HoldTick()
    {
        if (Interlocked.Exchange(ref _holdBusy, 1) == 1) return;
        try
        {
            if (_session is not null || _holdCard is not { } card) return; // the camera is back and feeding again
            var rec = _recorder is { } r && !_recPaused ? r : null;
            var live = _live; var replay = _replay;
            if (rec is not null) { rec.Push(card, _holdW, _holdH, _recordClock.Elapsed); PushVertical(card, _holdW, _holdH); }
            live?.Push(card, _holdW, _holdH);
            replay?.Push(card, _holdW, _holdH, _replayClock.Elapsed);
        }
        catch { /* an output that is stopping right now */ }
        finally { Volatile.Write(ref _holdBusy, 0); }
    }

    /// <summary>
    /// While waiting for a lost camera that Windows still lists (after sleep, a driver reset, another app letting
    /// go of it), try to open it again every few seconds. An unplugged camera comes back through the device watcher.
    /// </summary>
    private async Task RetryLostCameraAsync()
    {
        if (_waitingFor is not { } id || _session is not null || _pictureRetrying) return;
        var now = DateTime.UtcNow;
        if (now - _lastRetry < TimeSpan.FromSeconds(6)) return;
        _lastRetry = now; _pictureRetrying = true;
        try
        {
            var cams = await _catalog.GetCamerasAsync();
            var match = cams.FirstOrDefault(c => c.Id == id) ?? cams.FirstOrDefault(c => c.Name == _waitingName);
            if (match is null || _session is not null) return;
            if (!(DeviceBox.ItemsSource as IEnumerable<CameraDeviceInfo> ?? []).Contains(match)) await RefreshDevicesAsync();
            DeviceBox.SelectedItem = match;
            await ConnectAsync();
            if (_session is not null) Status($"{match.Name} is back." + (OutputsRunning ? " Your recording carried on and now shows the camera again." : ""));
        }
        catch { /* try again in a few seconds */ }
        finally { _pictureRetrying = false; }
    }
}
