using Microsoft.UI.Xaml;
using OpenKamera.Core;

namespace OpenKamera_App;

// System sound: what the PC plays joins the mix as its own track (meter, volume, mute).
public sealed partial class MainPage
{
    private const string SystemSoundId = "openkamera:system-sound";
    private readonly SystemSound _systemSound = new();
    private bool _systemSoundLoading;

    private void InitSystemSound()
    {
        _systemSoundLoading = true;
        SystemSoundToggle.IsOn = _settings.SystemSoundOn;
        _systemSoundLoading = false;
        _systemSound.Samples += s => _audio.PushAudio(SystemSoundId, s);
        Unloaded += (_, _) => _systemSound.Dispose();
    }

    /// <summary>Called after the audio engine (re)starts, so the system sound track is rebuilt with it.</summary>
    private void ResumeSystemSound()
    {
        if (!_settings.SystemSoundOn || !_audio.IsRunning) { _systemSound.Stop(); return; }
        string? why = LoopRisk();
        if (why is not null) { _systemSound.Stop(); AudioStatus.Text += " " + why; return; }
        _audio.AddPushTrack(SystemSoundId, "System sound");
        try { if (!_systemSound.IsRunning) _systemSound.Start(); }
        catch (Exception ex) { AudioStatus.Text += " System sound unavailable: " + ex.Message; }
    }

    /// <summary>Sending the mix to the same speakers we capture would feed back on itself.</summary>
    private string? LoopRisk()
    {
        string? speakers = null;
        try { speakers = new NAudio.CoreAudioApi.MMDeviceEnumerator().GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia).FriendlyName; } catch { }
        return speakers is not null && _audio.OutputName is { } o && string.Equals(o, speakers, StringComparison.OrdinalIgnoreCase)
            ? $"System sound is paused: the mix goes to {o}, the same speakers it would record, which would echo forever. Set Send to meetings to Off or a virtual cable."
            : null;
    }

    private async void SystemSoundToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_systemSoundLoading || !_audioReady) return;
        _settings.SystemSoundOn = SystemSoundToggle.IsOn;
        _settings.Save();
        if (SystemSoundToggle.IsOn && !_audio.IsRunning) { AudioOnToggle.IsOn = true; return; } // starting audio resumes it
        if (SystemSoundToggle.IsOn) ResumeSystemSound();
        else { _systemSound.Stop(); _audio.RemovePushTrack(SystemSoundId); }
        BuildMicTracks();
        await Task.CompletedTask;
    }
}
