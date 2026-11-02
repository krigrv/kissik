using OpenKamera.Core;

namespace OpenKamera_App;

// Ctrl+Alt shortcuts that work from any app: hide/show, mute, record, scenes 1–9.
public sealed partial class MainPage
{
    private GlobalHotkeys? _hotkeys;
    private bool _mixMutedByHotkey;

    private void InitGlobalHotkeys()
    {
        _hotkeys = new GlobalHotkeys(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        _hotkeys.Add(0x48 /* H */, "Ctrl+Alt+H", () => SetShutter(_shutter == ShutterMode.Off ? ShutterMode.Away : ShutterMode.Off));
        _hotkeys.Add(0x4D /* M */, "Ctrl+Alt+M", ToggleMixMute);
        _hotkeys.Add(0x52 /* R */, "Ctrl+Alt+R", () => RecordBtn_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs()));
        _hotkeys.Add(0x4B /* K */, "Ctrl+Alt+K", BringToFront);
        for (uint n = 1; n <= 9; n++) { int scene = (int)n; _hotkeys.Add(0x30 + n, $"Ctrl+Alt+{n}", () => SelectSceneByShortcut(scene)); }
        var notes = new List<string>();
        if (_hotkeys.Moved.Count > 0) notes.Add("another app uses some Ctrl+Alt keys, so use " + string.Join(", ", _hotkeys.Moved) + " instead");
        if (_hotkeys.Taken.Count > 0) notes.Add("unavailable: " + string.Join(", ", _hotkeys.Taken));
        if (notes.Count > 0) Status("Shortcuts: " + string.Join("; ", notes) + ".");
        Unloaded += (_, _) => _hotkeys?.Dispose();
    }

    private void ToggleMixMute()
    {
        _mixMutedByHotkey = !_mixMutedByHotkey;
        _audio.MuteAll = _mixMutedByHotkey || _shutter == ShutterMode.Away;
        _audio.ApplyMix();
        Status(_mixMutedByHotkey ? "Microphone mix muted (Ctrl+Alt+M to unmute)." : "Microphone mix on.");
    }
}
