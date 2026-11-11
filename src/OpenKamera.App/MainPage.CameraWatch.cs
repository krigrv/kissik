using OpenKamera.Core;
using Windows.Devices.Enumeration;

namespace OpenKamera_App;

// Camera unplugged mid-call: say so at once, keep the meeting on the KISSIK card, and reconnect by itself
// as soon as the same camera is plugged back in.
public sealed partial class MainPage
{
    private DeviceWatcher? _camWatcher;
    private string? _waitingFor, _waitingName;
    private int _cameraListStamp;

    private void WatchCamera(CaptureSession session, CameraDeviceInfo cam)
    {
        session.Lost += reason => DispatcherQueue.TryEnqueue(async () =>
        {
            if (_session != session) return;
            _waitingFor = cam.Id; _waitingName = cam.Name;
            // A recording, stream or replay in progress carries on with a paused card instead of ending here.
            bool hold = OutputsRunning;
            await ConnectAsync(toggle: true, keepOutputs: hold);
            if (hold) StartCameraHold();
            _lastRetry = DateTime.UtcNow;
            Status(hold
                ? $"{cam.Name} stopped working ({reason.Trim()}). Your recording carries on with a paused card and picks the camera up again when it is back."
                : $"{cam.Name} stopped working ({reason.Trim()}). Plug it back in: KISSIK reconnects by itself.");
            EmptyBusy.Text = $"Waiting for {cam.Name}…";
            StartCameraWatcher();
        });
    }

    private void StartCameraWatcher()
    {
        if (_camWatcher is not null) return;
        _camWatcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
        bool listed = false; // the first pass only lists what is already there
        _camWatcher.EnumerationCompleted += (_, _) => listed = true;
        _camWatcher.Added += (_, d) => DispatcherQueue.TryEnqueue(async () =>
        {
            if (!listed) return;
            // The camera we lost is back. The same camera in another USB port gets a new id, so its name counts too.
            bool lost = _waitingFor is not null && _session is null && (d.Id == _waitingFor || d.Name == _waitingName);
            if (!lost) { await RefreshCameraListSoonAsync(); return; }
            await Task.Delay(800); // let the driver settle
            await RefreshDevicesAsync();
            var match = (DeviceBox.ItemsSource as IEnumerable<CameraDeviceInfo>)?.FirstOrDefault(c => c.Id == d.Id);
            if (match is null || _session is not null) return;
            DeviceBox.SelectedItem = match;
            await ConnectAsync();
            Status($"{match.Name} is back and reconnected.");
        });
        _camWatcher.Removed += (_, _) => DispatcherQueue.TryEnqueue(async () => { if (listed) await RefreshCameraListSoonAsync(); });
        _camWatcher.Start();
    }

    /// <summary>
    /// A camera was plugged in or removed: bring the Source list up to date, so a webcam connected after KISSIK
    /// started can be picked without restarting. Several device events in a row cause one refresh.
    /// </summary>
    private async Task RefreshCameraListSoonAsync()
    {
        int stamp = ++_cameraListStamp;
        await Task.Delay(1000);
        if (stamp != _cameraListStamp || DeviceBox.IsDropDownOpen) return;
        var before = (DeviceBox.ItemsSource as IEnumerable<CameraDeviceInfo>)?.Select(c => c.Id).ToHashSet() ?? [];
        string shown = StatusText.Text;
        var picked = DeviceBox.SelectedItem as CameraDeviceInfo;
        await RefreshDevicesAsync();
        var now = (DeviceBox.ItemsSource as IEnumerable<CameraDeviceInfo>)?.ToList() ?? [];
        // Rebuilding the list must not move the selection off the source in use (a picture, the phone).
        if (picked is not null && now.Contains(picked)) DeviceBox.SelectedItem = picked;
        var added = now.Where(c => !before.Contains(c.Id)).Select(c => c.Name).ToList();
        if (added.Count > 0) Status($"New camera found: {string.Join(", ", added)}. Pick it under Source.");
        else Status(shown); // nothing new to say: keep the message that was there
    }
}
