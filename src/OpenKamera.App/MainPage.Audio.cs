using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OpenKamera.Core;

namespace OpenKamera_App;

/// <summary>Audio tab: microphone mixer, meters, limiter, EQ, noise suppression; feeds the recorder.</summary>
public sealed partial class MainPage
{
    private readonly AudioEngine _audio = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _meterTimer;
    private bool _audioReady;
    private readonly List<(AudioTrack Track, ProgressBar L, ProgressBar R)> _trackMeters = [];

    private AudioMixSettings Mix => _settings.Audio;

    private void InitAudio()
    {
        MasterVolume.Value = Mix.MasterVolume * 100;
        LimiterToggle.IsOn = Mix.Limiter;
        NoiseToggle.IsOn = Mix.NoiseSuppression;
        var eq = new[] { Eq0, Eq1, Eq2, Eq3 };
        for (int i = 0; i < 4; i++) eq[i].Value = Mix.EqDb.ElementAtOrDefault(i);
        AudioOnToggle.IsOn = _settings.AudioOn;
        _audio.MixReady += samples => { if (!_recPaused) _recorder?.PushAudio(samples, _recordClock.Elapsed); };

        _meterTimer = DispatcherQueue.CreateTimer();
        _meterTimer.Interval = TimeSpan.FromMilliseconds(50);
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();
        Unloaded += async (_, _) => await _audio.DisposeAsync();
        _audioReady = true;
        UpdateMixLabels();
        _ = RefreshMeetOutAsync();
        if (_settings.AudioOn) _ = StartAudioAsync();
        else BuildMicTracks();
    }

    private async Task StartAudioAsync()
    {
        try
        {
            AudioStatus.Text = "Starting audio…";
            if (!await AudioEngine.RequestAccessAsync())
            {
                AudioStatus.Text = "Microphone access is off for KISSIK. Turn it on in Settings → Privacy & security → Microphone, then switch audio off and on.";
                _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));
                BuildMicTracks();
                return;
            }
            await _audio.StartAsync(Mix);
            _phoneMicTrack = null; // the engine was rebuilt; the phone mic re-joins on its next sound
            ResumeSystemSound();
            bool denied = _audio.LastError?.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) == true
                || (_audio.Tracks.Count == 0 && DeviceUsage.OwnConsent(PrivacyDevice.Microphone) is "Prompt" or "Deny");
            AudioStatus.Text = _audio.Tracks.Count == 0
                ? (denied ? "Microphone access is off for KISSIK. Turn it on in Settings → Privacy & security → Microphone, then switch audio off and on."
                          : $"No microphone could be opened{(_audio.LastError is { } err ? $" ({err})" : "")}.")
                : $"Capturing {_audio.Tracks.Count} microphone(s) at 48 kHz." + (_audio.OutputName is { } on
                    ? $" Sending the mix to {on}." : " Nothing is played back, so there is no echo.");
            if (denied) _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));
            if (_audio.Notes.Count > 0) AudioStatus.Text += " " + string.Join(" ", _audio.Notes);
            if (_audio.Tracks.Count == 0 && _audio.LastError?.Contains("DeviceNotAvailable") == true)
                AudioStatus.Text += " Another app may be using it exclusively, or it is unplugged.";
            MicName.Text = _audio.Tracks.Count switch { 0 => "No microphone", 1 => _audio.Tracks[0].Name, var n => $"{n} microphones" };
            CardAudioHint.Text = _audio.Tracks.Count > 0 ? (_audio.OutputName is { } o ? $"Live · sending to {o}" : "Live · in recordings") : "Microphone not open";
        }
        catch (UnauthorizedAccessException)
        {
            AudioStatus.Text = "Microphone access is off for KISSIK. Turn it on in Settings → Privacy & security → Microphone.";
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));
        }
        catch (Exception ex) { AudioStatus.Text = "Audio failed: " + ex.Message; }
        BuildMicTracks();
    }

    /// <summary>One card per microphone: include checkbox, meter, volume, mute.</summary>
    private async void BuildMicTracks()
    {
        MicTracks.Children.Clear();
        _trackMeters.Clear();
        IReadOnlyList<(string Id, string Name)> mics;
        try { mics = await AudioEngine.ListMicrophonesAsync(); } catch { mics = []; }
        // Code-fed tracks (the phone's microphone) get a card too.
        mics = mics.Concat(_audio.Tracks.Where(t => t.DeviceId.StartsWith("openkamera:")).Select(t => (t.DeviceId, t.Name))).ToList();
        MicEmpty.Visibility = mics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, name) in mics)
        {
            var track = _audio.Tracks.FirstOrDefault(t => t.DeviceId == id);
            bool included = track is not null || (Mix.MicIds.Count == 0 ? AudioEngine.DefaultMic(mics).Any(m => m.Id == id) : Mix.MicIds.Contains(id));
            var include = new CheckBox { Content = name, IsChecked = included, FontSize = 14 };
            AutomationProperties.SetName(include, $"Use {name}");
            include.Click += async (_, _) =>
            {
                var chosen = Mix.MicIds.Count == 0 ? AudioEngine.DefaultMic(mics).Select(m => m.Id).ToList() : Mix.MicIds.ToList();
                if (include.IsChecked == true) { if (!chosen.Contains(id)) chosen.Add(id); } else chosen.Remove(id);
                Mix.MicIds = chosen;
                _settings.Save();
                if (_audio.IsRunning) await StartAudioAsync(); else BuildMicTracks();
            };
            var card = new StackPanel { Spacing = 4 };
            card.Children.Add(include);
            if (track is not null)
            {
                var l = new ProgressBar { Maximum = 1, Height = 6, CornerRadius = new CornerRadius(3) };
                var r = new ProgressBar { Maximum = 1, Height = 6, CornerRadius = new CornerRadius(3) };
                AutomationProperties.SetName(l, $"{name} level left"); AutomationProperties.SetName(r, $"{name} level right");
                card.Children.Add(l); card.Children.Add(r);
                _trackMeters.Add((track, l, r));
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var vol = new Slider { Minimum = 0, Maximum = 200, Value = track.Volume * 100 };
                AutomationProperties.SetName(vol, $"{name} volume");
                vol.ValueChanged += (_, e) => { track.Volume = e.NewValue / 100; _audio.ApplyMix(); _settings.Save(); };
                var mute = new ToggleButton { Content = new FontIcon { Glyph = track.Muted ? "" : "", FontSize = 14 }, IsChecked = track.Muted, CornerRadius = new CornerRadius(8) };
                AutomationProperties.SetName(mute, $"Mute {name}");
                ToolTipService.SetToolTip(mute, "Mute");
                mute.Click += (_, _) =>
                {
                    track.Muted = mute.IsChecked == true;
                    ((FontIcon)mute.Content).Glyph = track.Muted ? "" : "";
                    _audio.ApplyMix(); _settings.Save();
                };
                Grid.SetColumn(mute, 1);
                row.Children.Add(vol); row.Children.Add(mute);
                card.Children.Add(row);
            }
            else if (included)
            {
                // Ticked but not open: say why instead of showing nothing.
                string why = !_audio.IsRunning ? "Selected. Turn Audio on above to hear and adjust it."
                    : DeviceUsage.OwnConsent(PrivacyDevice.Microphone) is "Prompt" or "Deny"
                        ? "Selected, but Windows is blocking KISSIK's microphone. Allow it in Settings → Privacy & security → Microphone."
                        : "Selected, but it couldn't be opened. It may be unplugged or in use by another app.";
                card.Children.Add(new TextBlock { Text = why, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Token("DgDim") });
            }
            MicTracks.Children.Add(new Border { Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(12), Background = Token("DgWell"), Child = card });
        }
    }

    private void UpdateMeters()
    {
        if (!_audio.IsRunning)
        {
            CardMeterL.Value = CardMeterR.Value = MasterMeterL.Value = MasterMeterR.Value = 0;
            MasterLevelHint.Text = "Audio is off";
            return;
        }
        MasterLevelHint.Text = _audio.Tracks.Count == 0 ? "No microphone open" : "";
        // Show the louder of the mic and the mix in the card so a muted output still shows the mic is alive.
        float l = _audio.MasterPeakL, r = _audio.MasterPeakR;
        MasterMeterL.Value = l; MasterMeterR.Value = r;
        float ml = _audio.Tracks.Count > 0 ? _audio.Tracks.Max(t => t.PeakL) : 0, mr = _audio.Tracks.Count > 0 ? _audio.Tracks.Max(t => t.PeakR) : 0;
        CardMeterL.Value = Math.Max(l, ml); CardMeterR.Value = Math.Max(r, mr);
        foreach (var (t, pl, pr) in _trackMeters) { pl.Value = t.PeakL; pr.Value = t.PeakR; }
    }

    private async void AudioOnToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_audioReady) return;
        _settings.AudioOn = AudioOnToggle.IsOn; _settings.Save();
        if (AudioOnToggle.IsOn) await StartAudioAsync();
        else
        {
            await _audio.StopAsync();
            AudioStatus.Text = "Audio is off: recordings will be silent.";
            CardAudioHint.Text = "Audio is off. Turn it on in the Audio tab.";
            MicName.Text = "Off";
            BuildMicTracks();
        }
    }

    private void Mix_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_audioReady) return;
        Mix.MasterVolume = MasterVolume.Value / 100.0;
        Mix.EqDb = [Eq0.Value, Eq1.Value, Eq2.Value, Eq3.Value];
        _audio.ApplyMix();
        UpdateMixLabels();
        _settings.Save();
    }

    private void Mix_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_audioReady) return;
        Mix.Limiter = LimiterToggle.IsOn;
        _audio.ApplyMix(); _settings.Save();
    }

    /// <summary>Noise suppression is a property of how each microphone is opened, so the engine restarts.</summary>
    private async void Noise_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_audioReady) return;
        Mix.NoiseSuppression = NoiseToggle.IsOn; _settings.Save();
        if (_audio.IsRunning) await StartAudioAsync();
    }

    private void EqFlat_Click(object sender, RoutedEventArgs e)
    {
        _audioReady = false;
        Eq0.Value = Eq1.Value = Eq2.Value = Eq3.Value = 0;
        _audioReady = true;
        Mix_ValueChanged(Eq0, null!);
    }

    private void UpdateMixLabels() => MasterVolumeValue.Text = $"{MasterVolume.Value:0}%";
}
