using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Settings: the choices that are made once and left, in one place (they had no home: the save folders could not
// be changed at all).
public sealed partial class MainPage
{
    private readonly AudioDelay _audioDelay = new();

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        try { await ShowSettingsAsync(); }
        catch (Exception ex) { Status("Settings couldn't open: " + ex.Message); }
    }

    private async Task ShowSettingsAsync()
    {
        var body = new StackPanel { Spacing = 20, Width = 460 };

        // ── Where things are saved ──
        body.Children.Add(Section("Where things are saved",
            FolderRow("Recordings", () => RecordingFolder(), p => _settings.RecordingFolder = p, AppPaths.Videos),
            FolderRow("Pictures", () => string.IsNullOrWhiteSpace(_settings.ImageFolder) ? AppPaths.Pictures : _settings.ImageFolder, p => _settings.ImageFolder = p, AppPaths.Pictures)));

        // ── Sound in step with picture ──
        var delayValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Right };
        var delay = new Slider { Minimum = 0, Maximum = 400, StepFrequency = 10, Value = Math.Clamp(_settings.AudioDelayMs, 0, 400), MinHeight = 40 };
        AutomationProperties.SetName(delay, "Sound delay in milliseconds");
        void ShowDelay() => delayValue.Text = delay.Value < 1 ? "Off" : $"{delay.Value:0} ms";
        delay.ValueChanged += (_, _) =>
        {
            _settings.AudioDelayMs = delay.Value; _settings.Save();
            _audioDelay.Samples = AudioDelay.SamplesFor(delay.Value);
            ShowDelay();
        };
        ShowDelay();
        var delayRow = new Grid { ColumnSpacing = 12 };
        delayRow.ColumnDefinitions.Add(new ColumnDefinition());
        delayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(delayValue, 1);
        delayRow.Children.Add(delay); delayRow.Children.Add(delayValue);
        body.Children.Add(Section("Sound delay",
            Hint("If your voice comes before your lips move in a recording or stream, add a little delay. Clap once on camera, play it back, and raise this until the sound lands with the clap. It does not change what meetings hear."),
            delayRow));

        // ── How KISSIK looks ──
        var simple = new RadioButton { Content = "Simple: five big buttons", GroupName = "uimode", IsChecked = _settings.UiMode == "Simple", MinHeight = 40 };
        var studio = new RadioButton { Content = "Studio: every control", GroupName = "uimode", IsChecked = _settings.UiMode != "Simple", MinHeight = 40 };
        simple.Checked += (_, _) => { if (_settings.UiMode != "Simple") SetUiMode("Simple"); };
        studio.Checked += (_, _) => { if (_settings.UiMode == "Simple") SetUiMode("Studio"); };
        body.Children.Add(Section("How KISSIK looks", simple, studio));

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Settings", CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 },
        };
        await dlg.ShowAsync();
    }

    private static StackPanel Section(string title, params UIElement[] rows)
    {
        var s = new StackPanel { Spacing = 8 };
        s.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var r in rows) s.Children.Add(r);
        return s;
    }

    private TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Token("DgDim") };

    /// <summary>One save location: its name, where it points now, and Change / Open / Use the usual folder.</summary>
    private StackPanel FolderRow(string what, Func<string> current, Action<string> set, string usual)
    {
        var path = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Token("DgDim"), IsTextSelectionEnabled = true };
        var reset = new Button { Content = "Use the usual folder", MinHeight = 40 };
        void Show()
        {
            path.Text = current();
            reset.Visibility = string.Equals(current(), usual, StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
        }
        var change = new Button { Content = "Change…", MinHeight = 40 };
        AutomationProperties.SetName(change, $"Change where {what.ToLowerInvariant()} are saved");
        change.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                if (await picker.PickSingleFolderAsync() is not { } folder) return;
                // Make sure KISSIK can really write there before trusting it with a recording.
                string probe = Path.Combine(folder.Path, ".kissik-write-test");
                File.WriteAllText(probe, ""); File.Delete(probe);
                set(folder.Path); _settings.Save(); Show();
            }
            catch (Exception ex) { path.Text = $"KISSIK can't save there ({ex.Message}). Still using {current()}"; }
        };
        var open = new Button { Content = "Open", MinHeight = 40 };
        AutomationProperties.SetName(open, $"Open the {what.ToLowerInvariant()} folder");
        open.Click += (_, _) => { try { Directory.CreateDirectory(current()); ShowInFolder(current()); } catch { } };
        AutomationProperties.SetName(reset, $"Save {what.ToLowerInvariant()} in the usual folder");
        reset.Click += (_, _) => { set(""); _settings.Save(); Show(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(change); buttons.Children.Add(open); buttons.Children.Add(reset);
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(new TextBlock { Text = what });
        row.Children.Add(path);
        row.Children.Add(buttons);
        Show();
        return row;
    }

    // ── Simple mode: camera and microphone ──
    private bool _syncingSimpleSource;

    private void SyncSimpleSource()
    {
        _syncingSimpleSource = true;
        try
        {
            if (!ReferenceEquals(SimpleSource.ItemsSource, DeviceBox.ItemsSource)) SimpleSource.ItemsSource = DeviceBox.ItemsSource;
            if (!Equals(SimpleSource.SelectedItem, DeviceBox.SelectedItem)) SimpleSource.SelectedItem = DeviceBox.SelectedItem;
            bool on = _audio.IsRunning;
            SimpleMicBtn.IsChecked = on;
            SimpleMicLabel.Text = on ? "Microphone on" : "Microphone off";
            SimpleMicIcon.Glyph = on ? "\uE720" : "\uF781";
            AutomationProperties.SetName(SimpleMicBtn, SimpleMicLabel.Text);
        }
        finally { _syncingSimpleSource = false; }
    }

    private async void SimpleSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSimpleSource || SimpleSource.SelectedItem is not CameraDeviceInfo pick || Equals(pick, DeviceBox.SelectedItem)) return;
        try
        {
            if (_recorder is not null) { Status("Stop recording before changing the camera."); SyncSimpleSource(); return; }
            bool wasOn = _session is not null;
            if (wasOn) await ConnectAsync(toggle: true);
            DeviceBox.SelectedItem = pick;
            await ConnectAsync();
        }
        catch (Exception ex) { Status("Couldn't switch camera: " + ex.Message); }
        SyncSimple();
    }

    private void SimpleMic_Click(object sender, RoutedEventArgs e)
    {
        AudioOnToggle.IsOn = !AudioOnToggle.IsOn; // the Audio tab's own switch does the work
        SyncSimple();
    }
}
