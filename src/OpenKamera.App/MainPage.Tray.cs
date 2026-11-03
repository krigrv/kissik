using OpenKamera.Core;

namespace OpenKamera_App;

// Tray icon: open KISSIK, hide yourself, mute, record, or quit completely, without leaving your meeting app.
public sealed partial class MainPage
{
    private TrayIcon? _tray;

    private void InitTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _tray = new TrayIcon(hwnd, icon, "KISSIK", ShowMainWindow);
        _tray.Add("Show KISSIK", ShowMainWindow);
        _tray.AddSeparator();
        _tray.Add("Hide me (be right back)", () => SetShutter(_shutter == ShutterMode.Away ? ShutterMode.Off : ShutterMode.Away), () => _shutter == ShutterMode.Away);
        _tray.Add("Mute my microphone", ToggleMixMute, () => _mixMutedByHotkey);
        _tray.Add("Record", () => RecordBtn_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs()), () => _recorder is not null);
        _tray.AddSeparator();
        _tray.Add("Quit KISSIK", QuitCompletely);
        Unloaded += (_, _) => _tray?.Dispose();
    }

    private void ShowMainWindow()
    {
        if (App.MainWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p && p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized) p.Restore();
        App.MainWindow.Activate();
    }

    /// <summary>Quit means quit: finish a recording safely, stop streams, servers and devices, then exit.</summary>
    private async void QuitCompletely()
    {
        try
        {
            if (_recorder is not null) await StopRecordingAsync();
            if (_live is not null) await StopLiveAsync();
            await StopPhoneAsync();
            if (_control is { } c) await c.DisposeAsync();
            if (_session is { } s) await s.DisposeAsync();
            await _audio.DisposeAsync();
        }
        catch { }
        _tray?.Dispose(); _tray = null;
        Microsoft.UI.Xaml.Application.Current.Exit();
    }
}
