using System.Text;
using Microsoft.UI.Xaml;
using OpenKamera.Core;

namespace OpenKamera_App;

// "Copy diagnostics": one paste with everything needed to help someone (no personal data: no names, links or tokens).
public sealed partial class MainPage
{
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        void Line(string k, object? v) => sb.AppendLine($"{k}: {v}");
        var pkg = Windows.ApplicationModel.Package.Current.Id.Version;
        Line("KISSIK", $"{pkg.Major}.{pkg.Minor}.{pkg.Build}.{pkg.Revision}");
        Line("Windows", Environment.OSVersion.VersionString);
        Line("CPU cores", Environment.ProcessorCount);
        Line("Camera", _session is null ? "not connected" : $"{(DeviceBox.SelectedItem as CameraDeviceInfo)?.Name} · {_session.Mode}");
        Line("Profile", $"{_profile.Name} {_profile.Width}×{_profile.Height} @ {_profile.Fps}");
        Line("Speed", $"{_fps.Fps:0} fps · effects {_procMs:0} ms · smooth-first level {_smooth.Level}");
        Line("Background", $"{_bg.Mode}, quality {_bg.Quality}, green screen {_bg.GreenScreen}");
        Line("Framing", $"{_framingMode}, steady {_stabilise}, shutter {_shutter}, guard {_guardOn}");
        Line("Meeting camera", VirtualCamera.IsInstalled ? $"installed, {(_vcam.HasClients ? "in use" : "idle")}" : "not installed");
        Line("Camera privacy", DeviceUsage.OwnConsent(PrivacyDevice.Camera) ?? "unknown");
        Line("Microphone privacy", DeviceUsage.OwnConsent(PrivacyDevice.Microphone) ?? "unknown");
        Line("Audio", _audio.IsRunning ? $"{_audio.Tracks.Count} track(s), output {_audio.OutputName ?? "none"}" : "off");
        Line("Camera in use by", string.Join(", ", DeviceUsage.InUseBy(PrivacyDevice.Camera)) is { Length: > 0 } c ? c : "nobody else");
        if (_phone is { } p)
        {
            Line("Phone", p.PhoneConnected ? $"connected, {p.LastStatus?.Width}×{p.LastStatus?.Height}, {p.FramesReceived} frames" : "not connected");
            Line("Phone visits", $"last {p.LastVisit?.ToString("HH:mm:ss") ?? "never"}, page loads {p.PageLoads}, old links {p.WrongLinkVisits}, certificate refusals {p.TlsFailures}");
            Line("Phone firewall rule", PhoneFirewall.RuleExists(p.Port) ? "present" : "missing");
            Line("PC networks", PhoneServer.LocalAddresses().Count);
        }
        Line("Last status", StatusText.Text);

        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(sb.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        Status("Diagnostics copied. Paste them into a GitHub issue or a message.");
    }
}
