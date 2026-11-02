using OpenKamera.Core;
using Microsoft.UI.Xaml.Controls;

namespace OpenKamera_App;

// Opt-in crash reports: after an unexpected close, say so once and let the user copy the report or open a
// bug report. Nothing is sent automatically.
public sealed partial class MainPage
{
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
        if (latest.Length > 6000) latest = latest[^6000..];
        var res = await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "KISSIK closed unexpectedly last time",
            Content = new TextBlock { TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Text = "Sorry about that. If you'd like to help fix it, copy the error report and add it to a bug report. Nothing has been sent anywhere." },
            PrimaryButtonText = "Copy report and open bug page", SecondaryButtonText = "Copy report", CloseButtonText = "Not now",
        }.ShowAsync();
        if (res == ContentDialogResult.None) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText("KISSIK error report\n\n" + latest);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        if (res == ContentDialogResult.Primary)
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/krigrv/OpenKamera/issues/new"));
        Status("Error report copied. Paste it into the bug report.");
    }
}
