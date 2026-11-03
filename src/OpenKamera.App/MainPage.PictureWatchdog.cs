using Microsoft.UI.Xaml;
using OpenKamera.Core;

namespace OpenKamera_App;

// Picture watchdog: a camera can open fine and then send nothing (another app holds it, the driver stalls, the phone
// app is in the background). Instead of a silent black picture, say why and try the camera again by itself.
public sealed partial class MainPage
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pictureWatch;
    private DateTime _lastFrameAt, _connectedAt, _lastRetry;
    private bool _pictureMissing, _pictureRetrying;

    private void StartPictureWatchdog()
    {
        _pictureWatch = DispatcherQueue.CreateTimer();
        _pictureWatch.Interval = TimeSpan.FromSeconds(1);
        _pictureWatch.Tick += async (_, _) =>
        {
            if (_session is null || _pictureRetrying) { if (_pictureMissing) PictureBack(); return; }
            var now = DateTime.UtcNow;
            var since = _lastFrameAt > _connectedAt ? _lastFrameAt : _connectedAt;
            if (now - since < TimeSpan.FromSeconds(3)) { if (_pictureMissing) PictureBack(); return; }

            if (DeviceBox.SelectedItem == PictureDevice) return; // frames come from the picture timer
            bool phone = DeviceBox.SelectedItem == PhoneDevice;
            string name = (DeviceBox.SelectedItem as CameraDeviceInfo)?.Name ?? "the camera";
            var busy = phone ? [] : DeviceUsage.InUseBy(PrivacyDevice.Camera).Distinct().ToList();
            EmptyTitle.Text = "No picture from " + name;
            EmptyHint.Text = phone
                ? "Open KISSIK Camera on the phone and keep it in front. The picture comes back by itself."
                : busy.Count > 0
                    ? $"Another app is using your camera: {string.Join(", ", busy)}. Close the camera there; KISSIK tries again every few seconds."
                    : "The camera isn't sending a picture. KISSIK tries again every few seconds. If it stays black, unplug and replug the camera.";
            EmptyBusy.Text = "";
            EmptyCards.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            if (!_pictureMissing) { _pictureMissing = true; Status(EmptyTitle.Text + ". " + EmptyHint.Text); }

            // Webcams: reopen every 8 s while there's no picture (not during a recording, which keeps its file).
            if (phone || _recorder is not null || now - _lastRetry < TimeSpan.FromSeconds(8)) return;
            _lastRetry = now; _pictureRetrying = true;
            try { await ConnectAsync(toggle: true); await ConnectAsync(toggle: true); }
            catch { }
            finally { _pictureRetrying = false; }
        };
        _pictureWatch.Start();
    }

    private void PictureBack()
    {
        _pictureMissing = false;
        EmptyTitle.Text = "Let's get your camera on";
        EmptyHint.Text = "Use a webcam plugged into this PC, or your phone's camera.";
        EmptyCards.Visibility = Visibility.Visible;
        if (_session is not null) { EmptyText.Visibility = Visibility.Collapsed; Status("Picture is back."); }
    }
}
