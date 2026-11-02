namespace OpenKamera_App;

// Audio watchdog: if the audio engine stalls or Windows reports an audio error (mic unplugged, driver reset),
// restart it and say so, instead of a meeting or recording silently losing sound.
public sealed partial class MainPage
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _audioWatch;
    private bool _audioRestarting;
    private int _audioRestarts;

    private void StartAudioWatchdog()
    {
        _audioWatch = DispatcherQueue.CreateTimer();
        _audioWatch.Interval = TimeSpan.FromSeconds(2);
        _audioWatch.Tick += async (_, _) =>
        {
            CheckPhoneMic();
            CheckSystemMicMute();
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
    }
}
