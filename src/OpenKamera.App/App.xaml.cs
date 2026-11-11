using OpenKamera.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OpenKamera_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private DateTime _lastUnhandled;
    private int _unhandledBurst;

    /// <summary>The main window, for APIs that need an owner HWND (file pickers).</summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
        AppPaths.MigrateOldFolders(); // before anything reads settings, scenes or backgrounds
        // Crash log for bug reports: %LOCALAPPDATA%\KISSIK\crash.log (virtualised for the packaged app).
        UnhandledException += (_, e) =>
        {
            try
            {
                string dir = AppPaths.LocalData;
                Directory.CreateDirectory(dir);
                File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"), $"{DateTime.Now:O} {e.Exception}\n\n");
            }
            catch { /* never throw from the crash handler */ }
            // An error in one button's handler shouldn't end a meeting or a recording: note it, say so, carry on.
            // A burst of errors means something is badly wrong, so that is still allowed to close the app.
            var now = DateTime.UtcNow;
            if (now - _lastUnhandled > TimeSpan.FromSeconds(30)) _unhandledBurst = 0;
            _lastUnhandled = now;
            if (++_unhandledBurst > 3) return;
            e.Handled = true;
            try { MainPage.Current?.ReportProblem(e.Exception); } catch { }
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // One KISSIK at a time: a second copy would fight the first for the camera, the microphone and the meeting
        // camera. Opening KISSIK again shows the one that is already running.
        try
        {
            var first = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("KISSIK");
            if (!first.IsCurrent)
            {
                var how = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
                try { Task.Run(() => first.RedirectActivationToAsync(how).AsTask()).Wait(TimeSpan.FromSeconds(5)); } catch { }
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                return;
            }
            first.Activated += (_, _) => MainWindow?.DispatcherQueue.TryEnqueue(() => MainPage.Current?.ShowFromSecondLaunch());
        }
        catch { /* if Windows can't tell us, open normally */ }
        MainWindow = _window = new MainWindow();
        _window.Activate();
    }
}
