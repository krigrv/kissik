using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

/// <summary>The "Meeting camera": install, turn on, and feed the picture to the virtual camera.</summary>
public sealed partial class MainPage
{
    private readonly VirtualCameraChannel _vcam = new();

    private void InitVirtualCamera()
    {
        RefreshMeetingCamera();
        if (VirtualCamera.IsInstalled) MeetCamStatus.Text = VirtualCamera.EnsureRegistered();
        Unloaded += (_, _) => { VirtualCamera.Shutdown(); _vcam.Dispose(); };
    }

    /// <summary>Called on the frame thread with the final picture (scene layers included).</summary>
    private void FeedVirtualCamera(byte[] pixels, int w, int h) => _vcam.TryWrite(pixels, w, h);

    private void RefreshMeetingCamera()
    {
        bool supported = VirtualCamera.IsSupported, installed = VirtualCamera.IsInstalled;
        MeetCamInstallBtn.Visibility = supported && !installed ? Visibility.Visible : Visibility.Collapsed;
        MeetCamOnBtn.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        MeetCamUninstallBtn.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        MeetCamDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(installed
            ? (_vcam.HasClients ? Microsoft.UI.ColorHelper.FromArgb(255, 0xD3, 0x2F, 0x26) : Microsoft.UI.ColorHelper.FromArgb(255, 91, 255, 143))
            : Microsoft.UI.ColorHelper.FromArgb(102, 255, 255, 255));
        if (!supported) MeetCamStatus.Text = "The meeting camera needs Windows 11.";
        else if (!installed) MeetCamStatus.Text = "Install once (asks for admin permission). Then pick \"OpenKamera\" as the camera in Zoom, Teams, Meet, Discord or OBS.";
        var cam = DeviceUsage.InUseBy(PrivacyDevice.Camera).Distinct().ToList();
        var mic = DeviceUsage.InUseBy(PrivacyDevice.Microphone).Distinct().ToList();
        var lines = new List<string>();
        if (_vcam.HasClients) lines.Add("● A meeting app is using KISSIK now.");
        if (cam.Count > 0) lines.Add("Camera open in: " + string.Join(", ", cam));
        if (mic.Count > 0) lines.Add("Microphone open in: " + string.Join(", ", mic));
        MeetCamLive.Text = string.Join(Environment.NewLine, lines);
    }

    private void MeetCamFlyout_Opening(object sender, object e) => RefreshMeetingCamera();

    private async void MeetCamInstall_Click(object sender, RoutedEventArgs e)
    {
        MeetCamStatus.Text = "Installing… approve the Windows prompt.";
        bool ok;
        try { ok = await VirtualCamera.InstallAsync(); }
        catch (Exception ex) { MeetCamStatus.Text = "Install failed: " + ex.Message; return; }
        MeetCamStatus.Text = ok ? VirtualCamera.EnsureRegistered() : "Install was cancelled or failed.";
        RefreshMeetingCamera();
    }

    private void MeetCamOn_Click(object sender, RoutedEventArgs e)
    {
        MeetCamStatus.Text = VirtualCamera.EnsureRegistered();
        RefreshMeetingCamera();
    }

    private async void MeetCamUninstall_Click(object sender, RoutedEventArgs e)
    {
        MeetCamStatus.Text = "Removing… approve the Windows prompt.";
        bool ok = await VirtualCamera.UninstallAsync();
        MeetCamStatus.Text = ok ? "KISSIK camera removed." : "Removal was cancelled or failed.";
        RefreshMeetingCamera();
    }
}
