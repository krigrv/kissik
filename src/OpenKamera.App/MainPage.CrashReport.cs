using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Crash reports: after an unexpected close, KISSIK asks once whether to send reports automatically. With a yes,
// later crashes are sent quietly (error text only, scrubbed of names and paths). Without one, the user can copy the
// report for a bug page. Until the project's report inbox is set up (CrashReports.Dsn), nothing is ever sent.
public sealed partial class MainPage
{
    private static string AppVersion()
    {
        try { var v = Windows.ApplicationModel.Package.Current.Id.Version; return $"{v.Major}.{v.Minor}.{v.Build}"; }
        catch { return "dev"; }
    }

    private void InitCrashReports()
    {
        CrashReportsToggle.IsEnabled = CrashReports.IsConfigured;
        CrashReportsToggle.IsChecked = CrashReports.IsConfigured && _settings.CrashReportsAuto == true;
    }

    private void CrashReportsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.CrashReportsAuto = CrashReportsToggle.IsChecked; _settings.Save();
        Status(CrashReportsToggle.IsChecked ? "Crash reports will be sent automatically (error details only)." : "Crash reports won't be sent.");
    }

    private async Task OfferCrashReportAsync()
    {
        string log = Path.Combine(AppPaths.LocalData, "crash.log");
        if (!File.Exists(log)) return;
        long size = new FileInfo(log).Length;
        if (size <= _settings.CrashLogSeen) return;
        bool firstRun = _settings.CrashLogSeen == 0; // first time: just remember where the log is now
        string text;
        try { text = File.ReadAllText(log); } catch { return; }
        string latest = text.Length > _settings.CrashLogSeen ? text[(int)Math.Min(text.Length, _settings.CrashLogSeen)..].Trim() : "";
        _settings.CrashLogSeen = size; _settings.Save();
        if (firstRun || latest.Length == 0) return;
        var crashes = CrashReports.SplitEntries(latest);

        if (CrashReports.IsConfigured)
        {
            if (_settings.CrashReportsAuto == true) { await SendCrashesAsync(crashes, quiet: true); return; }
            if (_settings.CrashReportsAuto is null)
            {
                var ask = await new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "KISSIK closed unexpectedly last time",
                    Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
                        "Sorry about that. Can KISSIK send crash reports so they get fixed? A report holds only the error and the " +
                        "KISSIK and Windows versions, with your name and folders removed. Never video, audio or files. " +
                        "You can change this later in Settings and help." },
                    PrimaryButtonText = "Send this and future reports", SecondaryButtonText = "Send just this one", CloseButtonText = "Don't send",
                    DefaultButton = ContentDialogButton.Primary,
                }.ShowAsync();
                if (ask == ContentDialogResult.Primary) { _settings.CrashReportsAuto = true; _settings.Save(); InitCrashReports(); }
                else if (ask == ContentDialogResult.None) { _settings.CrashReportsAuto = false; _settings.Save(); return; }
                await SendCrashesAsync(crashes, quiet: false);
                return;
            }
            return; // the user said no
        }

        // Report inbox not set up: offer to copy the report for a bug page.
        string copy = latest.Length > 6000 ? latest[^6000..] : latest;
        var res = await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "KISSIK closed unexpectedly last time",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Sorry about that. If you'd like to help fix it, copy the error report and add it to a bug report. Nothing has been sent anywhere." },
            PrimaryButtonText = "Copy report and open bug page", SecondaryButtonText = "Copy report", CloseButtonText = "Not now",
        }.ShowAsync();
        if (res == ContentDialogResult.None) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText("KISSIK error report\n\n" + CrashReports.Scrub(copy));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        if (res == ContentDialogResult.Primary)
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/krigrv/kissik/issues/new"));
        Status("Error report copied. Paste it into the bug report.");
    }

    private async Task SendCrashesAsync(IReadOnlyList<string> crashes, bool quiet)
    {
        int sent = 0;
        foreach (var c in crashes.TakeLast(5)) if (await CrashReports.SendAsync(c, AppVersion())) sent++;
        if (!quiet || sent < crashes.Count)
            Status(sent > 0 ? $"Thanks: {sent} crash report{(sent == 1 ? "" : "s")} sent." : "Couldn't send the crash report now; it's kept on this PC.");
    }
}
