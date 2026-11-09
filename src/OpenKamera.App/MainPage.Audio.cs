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
    private readonly List<(AudioTrack Track, TextBlock Label)> _noiseLabels = [];

    private AudioMixSettings Mix => _settings.Audio;

    private void InitAudio()
    {
        MasterVolume.Value = Mix.MasterVolume * 100;
        LimiterToggle.IsOn = Mix.Limiter;
        NoiseToggle.IsOn = Mix.NoiseSuppression;
        LowCutToggle.IsOn = Mix.LowCut;
        AutoLevelToggle.IsOn = Mix.AutoLevel;
        DeEssToggle.IsOn = Mix.DeEss;
        var eq = new[] { Eq0, Eq1, Eq2, Eq3 };
        for (int i = 0; i < 4; i++) eq[i].Value = Mix.EqDb.ElementAtOrDefault(i);
        AudioOnToggle.IsOn = _settings.AudioOn;
        _audioDelay.Samples = AudioDelay.SamplesFor(_settings.AudioDelayMs);
        _audio.MixReady += samples =>
        {
            // Recordings, streams and replays get the sound delayed by the amount set in Settings, so it lands with
            // the picture. Captions listen to it as it happens.
            var timed = _audioDelay.Process(samples);
            if (!_recPaused) { _recorder?.PushAudio(timed, _recordClock.Elapsed); _vertical?.PushAudio(timed, _recordClock.Elapsed); }
            _live?.PushAudio(timed); _replay?.PushAudio(timed, _replayClock.Elapsed);
            FeedCaptions(samples);
        };

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
        _noiseLabels.Clear();
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
                if (track.IsVoice) card.Children.Add(NoisePicker(id, name, track));
                else card.Children.Add(new TextBlock { Text = "In recordings and streams only. Meetings don't hear it.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Token("DgDim") });
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
        foreach (var (t, label) in _noiseLabels) UpdateNoiseLabel(t, label);
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

    private static readonly (NoiseRemoval Mode, string Label)[] NoiseModes =
        [(NoiseRemoval.Off, "Off"), (NoiseRemoval.Light, "Light"), (NoiseRemoval.Studio, "Studio voice")];

    /// <summary>Per-mic noise removal: Off, Light (RNNoise) or Studio voice (DeepFilterNet). Applies without stopping audio.</summary>
    private FrameworkElement NoisePicker(string id, string name, AudioTrack track)
    {
        var box = new ComboBox { ItemsSource = NoiseModes.Select(m => m.Label).ToList(), SelectedIndex = (int)Mix.NoiseFor(id), MinWidth = 140 };
        AutomationProperties.SetName(box, $"Noise removal for {name}");
        AutomationProperties.SetHelpText(box, "Light uses little power. Studio voice removes more noise.");
        ToolTipService.SetToolTip(box, "Light uses little power. Studio voice removes more noise.");
        var label = new TextBlock { Text = "Noise removal", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Token("DgDim") };
        box.SelectionChanged += async (_, _) =>
        {
            var mode = NoiseModes[Math.Max(0, box.SelectedIndex)].Mode;
            if (mode == Mix.NoiseFor(id)) return;
            Mix.MicNoise[id] = mode;
            _settings.Save();
            box.IsEnabled = false; // Studio voice takes a moment to load its model
            await _audio.UpdateVoiceAsync();
            box.IsEnabled = true;
            box.Focus(FocusState.Programmatic);
            UpdateNoiseLabel(track, label);
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(box, 1);
        row.Children.Add(label); row.Children.Add(box);
        _noiseLabels.Add((track, label));
        UpdateNoiseLabel(track, label);
        return row;
    }

    /// <summary>"Noise removal · removing 14 dB", or why it isn't running.</summary>
    private void UpdateNoiseLabel(AudioTrack track, TextBlock label)
    {
        string text = track.VoiceNote is { } note ? $"Noise removal: {note}"
            : track.NoiseFailed ? "Noise removal stopped after an error"
            : track.Noise != NoiseRemoval.Off && track.NoiseReductionDb >= 1 ? $"Noise removal · removing {track.NoiseReductionDb:0} dB"
            : "Noise removal";
        if (label.Text != text) label.Text = text;
    }

    /// <summary>Low cut, auto level and de-esser are part of each mic's voice chain: swapped in live.</summary>
    private async void Voice_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_audioReady) return;
        Mix.LowCut = LowCutToggle.IsOn; Mix.AutoLevel = AutoLevelToggle.IsOn; Mix.DeEss = DeEssToggle.IsOn; _settings.Save();
        if (_audio.IsRunning) await _audio.UpdateVoiceAsync();
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
