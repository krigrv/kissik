using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenKamera_App;

// Connection watch: if a meeting app starts using the OpenKamera camera while no real camera is connected,
// ask once to connect instead of leaving the meeting on the "OpenKamera" card.
public sealed partial class MainPage
{
    private DispatcherTimer? _connectWatch;
    private bool _promptOpen, _promptedForThisMeeting;

    private void StartConnectWatch()
    {
        _connectWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectWatch.Tick += async (_, _) =>
        {
            try { CheckAppProfiles(); } catch { /* the usage record is optional */ }
            if (_session is not null || !_vcam.HasClients) { if (!_vcam.HasClients) _promptedForThisMeeting = false; return; }
            if (_promptOpen || _promptedForThisMeeting || DeviceBox.SelectedItem is null) return;
            _promptOpen = _promptedForThisMeeting = true;
            try
            {
                var dlg = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "A meeting is using KISSIK",
                    Content = "Your camera isn't connected, so the meeting only sees the KISSIK card. Connect now?",
                    PrimaryButtonText = "Connect", CloseButtonText = "Not now", DefaultButton = ContentDialogButton.Primary,
                };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary) await ConnectAsync();
            }
            catch { /* another dialog was open; try again on the next meeting */ }
            finally { _promptOpen = false; }
        };
        _connectWatch.Start();
    }
}
