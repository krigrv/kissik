using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
        bool supported = VirtualCamera.IsSupported, installed = VirtualCamera.IsInstalled, outdated = VirtualCamera.IsOutdated;
        MeetCamInstallBtn.Visibility = supported && (!installed || outdated) ? Visibility.Visible : Visibility.Collapsed;
        MeetCamInstallBtn.Content = outdated ? "Update the KISSIK camera" : "Install the KISSIK camera";
        MeetCamOnBtn.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        MeetCamUninstallBtn.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        // Theme colours (3:1 or better on light and dark); the state is in the button's name too, not colour alone.
        MeetCamDot.Fill = Token(installed ? (_vcam.HasClients ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush") : "ControlStrongStrokeColorDefaultBrush");
        AutomationProperties.SetName(MeetCamBtn, !installed ? "Meeting camera, not installed" : _vcam.HasClients ? "Meeting camera, in use by a meeting" : "Meeting camera, ready");
        if (!supported) MeetCamStatus.Text = "The meeting camera needs Windows 11.";
        else if (VirtualCamera.LastCheckError is { } err) MeetCamStatus.Text = "Couldn't check the installed camera: " + err;
        else if (outdated) MeetCamStatus.Text = "A newer meeting camera comes with this KISSIK. Update it (asks for admin permission), then rejoin your meeting.";
        else if (!installed) MeetCamStatus.Text = $"Install once (asks for admin permission). Then pick \"{VirtualCamera.FriendlyName}\" as the camera in Zoom, Teams, Meet, Discord or OBS.";
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
