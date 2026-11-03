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
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MainWindow = _window = new MainWindow();
        _window.Activate();
    }
}
