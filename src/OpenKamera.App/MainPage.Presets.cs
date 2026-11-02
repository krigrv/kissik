using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Presets: save the whole look (enhance, filter, LUT, background, mirror) under a name and recall it in one click.
public sealed partial class MainPage
{
    private void RefreshPresetBox(string? select)
    {
        PresetBox.ItemsSource = _settings.Presets.Select(p => p.Name).ToList();
        PresetBox.SelectedItem = select;
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoring || PresetBox.SelectedItem is not string name) return;
        if (ApplyPreset(name)) Status($"Preset “{name}” applied");
    }

    private bool ApplyPreset(string name)
    {
        var preset = _settings.Presets.FirstOrDefault(p => p.Name == name);
        if (preset is null) return false;
        preset.ApplyTo(_settings);
        _settings.Save();
        RestoreLook();
        _restoring = true; PresetBox.SelectedItem = name; _restoring = false;
        SyncLookTiles();
        return true;
    }

    // ── Per-app profiles ──────────────────────────────────────────────

    private readonly AppProfileWatcher _appProfiles = new();

    /// <summary>Called every couple of seconds: switches to an app's preset when that app opens a camera.</summary>
    private void CheckAppProfiles()
    {
        _appProfiles.Links.Clear();
        foreach (var (app, preset) in _settings.AppPresets) _appProfiles.Links[app] = preset;
        if (_appProfiles.Update(DeviceUsage.InUseBy(PrivacyDevice.Camera)) is { } hit && ApplyPreset(hit.Preset))
            Status($"{hit.App} opened the camera, so KISSIK switched to your “{hit.Preset}” preset.");
    }

    private async void LinkPresetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not string preset) { Status("Pick or save a preset first, then link it to an app."); return; }
        // Apps that have used a camera on this PC, plus the usual meeting apps.
        var apps = DeviceUsage.Read(PrivacyDevice.Camera).Select(u => u.App)
            .Concat(["Zoom", "Microsoft Teams", "Google Chrome", "Microsoft Edge", "Discord", "OBS Studio", "Slack"])
            .Where(a => a != "KISSIK").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pick = new ComboBox { ItemsSource = apps, PlaceholderText = "Choose an app", HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pick, "App to link");
        var body = new StackPanel { Spacing = 12, MinWidth = 340 };
        body.Children.Add(new TextBlock { Text = $"When this app opens the camera, switch to “{preset}”.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(pick);
        if (_settings.AppPresets.Count > 0)
            body.Children.Add(new TextBlock
            {
                Text = "Linked now:\n" + string.Join("\n", _settings.AppPresets.Select(kv => $"• {kv.Key} → {kv.Value}")),
                FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
            });
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Use this preset for an app", Content = body,
            PrimaryButtonText = "Link", SecondaryButtonText = "Unlink this app", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dlg.ShowAsync();
        if (pick.SelectedItem is not string app) return;
        if (result == ContentDialogResult.Primary) { _settings.AppPresets[app] = preset; Status($"Linked: {app} → “{preset}”."); }
        else if (result == ContentDialogResult.Secondary) { _settings.AppPresets.Remove(app); Status($"{app} no longer switches presets."); }
        else return;
        _settings.Save();
    }

    private async void SavePresetBtn_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "e.g. Client calls", Text = PresetBox.SelectedItem as string ?? "", MaxLength = 40 };
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Save preset", Content = box,
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        string name = box.Text.Trim();
        if (name.Length == 0) return;
        SaveLook();
        _settings.Background = _bg;
        LookPreset.Upsert(_settings.Presets, LookPreset.Capture(name, _settings));
        _settings.Save();
        _restoring = true; RefreshPresetBox(name); _restoring = false;
        Status($"Preset “{name}” saved");
    }

    private void DeletePresetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not string name) return;
        _settings.Presets.RemoveAll(p => p.Name == name);
        _settings.Save();
        _restoring = true; RefreshPresetBox(null); _restoring = false;
        Status($"Preset “{name}” deleted");
    }
}
