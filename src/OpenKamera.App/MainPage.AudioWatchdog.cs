namespace OpenKamera_App;

// Audio watchdog: if the audio engine stalls or Windows reports an audio error (mic unplugged, driver reset),
// restart it and say so, instead of a meeting or recording silently losing sound.
public sealed partial class MainPage
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _audioWatch;
    private bool _audioRestarting;
    private int _audioRestarts;
    private string? _shownAudioError;

    private void StartAudioWatchdog()
    {
        _audioWatch = DispatcherQueue.CreateTimer();
        _audioWatch.Interval = TimeSpan.FromSeconds(2);
        _audioWatch.Tick += async (_, _) =>
        {
            UpdateKeepAwake();
            CheckPhoneMic();
            CheckSystemMicMute();
            if (_audio.LastError is { } err && err != _shownAudioError) { _shownAudioError = err; Status("Audio problem: " + err); }
            if (_micsChanged && !_audioRestarting && _settings.AudioOn && _audio.IsRunning) await FollowMicChangeAsync();
            if (_micAccessOff && !_audioRestarting && _settings.AudioOn) { await OpenMicOnceAllowedAsync(); return; }
            if (_audioRestarting || !_settings.AudioOn || !_audio.NeedsRestart(DateTime.UtcNow, TimeSpan.FromSeconds(3))) return;
            _audioRestarting = true;
            try
            {
                _audioRestarts++;
                string why = _audio.Failed ? $"Windows reported an audio problem ({_audio.LastError ?? "device changed"})" : "sound stopped coming in";
                await StartAudioAsync();
                bool ok = _audio.IsRunning && !_audio.Failed;
                string rec = _recorder is not null ? " Your recording kept going." : "";
                Status(ok ? $"Audio restarted: {why}.{rec}" : $"Audio stopped: {why}. Check the microphone is plugged in.{rec}");
            }
            catch (Exception ex) { Status("Audio restart failed: " + ex.Message); }
            finally { _audioRestarting = false; }
        };
        _audioWatch.Start();
        WatchMicrophones();
    }

    private string? _micConsentSeen;

    /// <summary>
    /// The microphone was refused because Windows had access turned off for KISSIK. Once the person turns it on,
    /// open the microphone without asking them to switch audio off and on or restart. One try per change of the
    /// Windows setting, so a setting that still refuses is not retried every two seconds.
    /// </summary>
    private async Task OpenMicOnceAllowedAsync()
    {
        string? consent = OpenKamera.Core.DeviceUsage.OwnConsent(OpenKamera.Core.PrivacyDevice.Microphone);
        if (consent == _micConsentSeen) return;
        _micConsentSeen = consent;
        if (consent != "Allow") return;
        _audioRestarting = true;
        try
        {
            await StartAudioAsync(openSettings: false);
            if (_audio.Tracks.FirstOrDefault(t => t.IsVoice)?.Name is { } mic) Status($"Microphone access is on: now using {mic}.");
        }
        catch (Exception ex) { Status("Couldn't open the microphone: " + ex.Message); }
        finally { _audioRestarting = false; }
    }

    private Windows.Devices.Enumeration.DeviceWatcher? _micWatcher;
    private volatile bool _micsChanged;

    /// <summary>Notices microphones being plugged in or pulled out, and Windows' default microphone changing.</summary>
    private void WatchMicrophones()
    {
        try
        {
            var w = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(Windows.Devices.Enumeration.DeviceClass.AudioCapture);
            bool listed = false; // the first pass only lists what is already there
            w.Added += (_, _) => { if (listed) _micsChanged = true; };
            w.Removed += (_, _) => { if (listed) _micsChanged = true; };
            w.EnumerationCompleted += (_, _) => listed = true;
            w.Start();
            _micWatcher = w;
            Windows.Media.Devices.MediaDevice.DefaultAudioCaptureDeviceChanged += (_, _) => _micsChanged = true;
            Unloaded += (_, _) => { try { w.Stop(); } catch { } };
        }
        catch { /* no watcher: the stall check above still restarts a dead engine */ }
    }

    /// <summary>
    /// After a microphone change, reopens audio only when the set of microphones KISSIK should be using is no
    /// longer the set it has open: the chosen one came back, or it left and the default takes over.
    /// </summary>
    private async Task FollowMicChangeAsync()
    {
        _micsChanged = false;
        _audioRestarting = true;
        try
        {
            var present = await OpenKamera.Core.AudioEngine.ListMicrophonesAsync();
            var want = OpenKamera.Core.AudioEngine.ChooseMics(Mix.MicIds, present, out _).Select(m => m.Id).ToHashSet();
            if (want.SetEquals(_audio.OpenMicIds)) { BuildMicTracks(); return; }
            await StartAudioAsync();
            string now = _audio.Tracks.FirstOrDefault(t => t.IsVoice)?.Name ?? "no microphone";
            Status($"Microphones changed: now using {now}.");
        }
        catch (Exception ex) { Status("Couldn't follow the microphone change: " + ex.Message); }
        finally { _audioRestarting = false; }
    }
}
