using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;

namespace OpenKamera_App;

// Notices when a microphone is muted outside KISSIK (Windows sound settings, a mute key or a headset button)
// and says so on the Audio card, so you never talk into a mic that Windows has silenced.
public sealed partial class MainPage
{
    private readonly MMDeviceEnumerator _mmDevices = new();
    private string? _systemMuted; // names of mics Windows has muted, as last shown

    /// <summary>Runs on the watchdog tick: reads each mic's Windows mute switch.</summary>
    private void CheckSystemMicMute()
    {
        var muted = new List<string>();
        foreach (var t in _audio.Tracks)
        {
            if (!t.DeviceId.Contains("MMDEVAPI", StringComparison.OrdinalIgnoreCase)) continue; // phone mic: not a Windows device
            try
            {
                string id = t.DeviceId;
                int a = id.IndexOf("MMDEVAPI#", StringComparison.OrdinalIgnoreCase);
                { a += 9; int b = id.IndexOf('#', a); id = b > a ? id[a..b] : id[a..]; }
                using var dev = _mmDevices.GetDevice(id);
                if (dev.AudioEndpointVolume.Mute) muted.Add(t.Name);
            }
            catch { }
        }
        string now = string.Join(", ", muted);
        if (now == (_systemMuted ?? "")) return;
        _systemMuted = now;
        MicMutedBadge.Visibility = muted.Count > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        MicName.Visibility = muted.Count > 0 ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        MicMutedBadge.Text = muted.Count switch { 0 => "", 1 => "Muted in Windows", var n => $"{n} mics muted in Windows" };
        ToolTipService.SetToolTip(MicMutedBadge, muted.Count > 0 ? $"{now}: muted by Windows, a mute key or the headset. Unmute it there; KISSIK can't hear it." : null);
        Status(muted.Count > 0 ? $"Your microphone ({now}) is muted outside KISSIK. Nobody can hear you until you unmute it." : "Your microphone is unmuted again.");
    }
}
