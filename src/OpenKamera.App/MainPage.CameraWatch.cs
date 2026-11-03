using OpenKamera.Core;
using Windows.Devices.Enumeration;

namespace OpenKamera_App;

// Camera unplugged mid-call: say so at once, keep the meeting on the KISSIK card, and reconnect by itself
// as soon as the same camera is plugged back in.
public sealed partial class MainPage
{
    private DeviceWatcher? _camWatcher;
    private string? _waitingFor;

    private void WatchCamera(CaptureSession session, CameraDeviceInfo cam)
    {
        session.Lost += reason => DispatcherQueue.TryEnqueue(async () =>
        {
            if (_session != session) return;
            _waitingFor = cam.Id;
            await ConnectAsync(toggle: true); // tidy disconnect (stops recording safely)
            Status($"{cam.Name} stopped working ({reason.Trim()}). Plug it back in: KISSIK reconnects by itself.");
            EmptyBusy.Text = $"Waiting for {cam.Name}…";
            StartCameraWatcher();
        });
    }

    private void StartCameraWatcher()
    {
        if (_camWatcher is not null) return;
        _camWatcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
        _camWatcher.Added += (_, d) => DispatcherQueue.TryEnqueue(async () =>
        {
            if (_waitingFor is null || _session is not null || d.Id != _waitingFor) return;
            _waitingFor = null;
            await Task.Delay(800); // let the driver settle
            await RefreshDevicesAsync();
            var match = (DeviceBox.ItemsSource as IEnumerable<CameraDeviceInfo>)?.FirstOrDefault(c => c.Id == d.Id);
            if (match is null) return;
            DeviceBox.SelectedItem = match;
            await ConnectAsync();
            Status($"{match.Name} is back and reconnected.");
        });
        _camWatcher.Start();
    }
}
