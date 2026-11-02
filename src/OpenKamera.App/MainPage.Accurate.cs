using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Accurate captions: words still appear instantly; each finished line is re-checked with Whisper on this PC and
// corrected about a second later. One-time model download (142 MB); nothing is sent anywhere.
public sealed partial class MainPage
{
    private WhisperRefiner? _whisper;

    private void InitAccurate() => AccurateToggle.IsChecked = _settings.AccurateCaptions;

    private void StartAccurate(OfflineCaptions source, string language)
    {
        string model = WhisperRefiner.ModelPath(SpeechRoot);
        if (!File.Exists(model)) return;
        try
        {
            var wr = new WhisperRefiner(model, language);
            wr.Corrected += (draft, fixedText) => DispatcherQueue.TryEnqueue(() => _captions.Correct(draft, fixedText));
            source.FinalWithAudio += (draft, audio) => wr.Refine(draft, audio);
            _whisper = wr;
        }
        catch (Exception ex) { Status("Accurate captions couldn't start: " + ex.Message); }
    }

    private async void AccurateToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = AccurateToggle.IsChecked;
        if (on && !File.Exists(WhisperRefiner.ModelPath(SpeechRoot)) && !await DownloadWhisperAsync()) { AccurateToggle.IsChecked = false; return; }
        _settings.AccurateCaptions = on; _settings.Save();
        // Restart captions if they are running so the change takes effect now.
        if (_offline is not null) { await StopCaptionsAsync(); await StartCaptionsAsync(); }
        Status(on ? "Accurate captions on: each line is double-checked on this PC and corrected a moment later." : "Accurate captions off.");
    }

    private async Task<bool> DownloadWhisperAsync()
    {
        var bar = new ProgressBar { Maximum = 1, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Spacing = 10, MaxWidth = 420 };
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = $"Accurate captions need a speech model ({WhisperRefiner.ModelSizeMb} MB), downloaded once. It runs on this PC; your voice never leaves it." });
        body.Children.Add(bar);
        var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = "Download the accurate speech model?", Content = body,
            PrimaryButtonText = "Download", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        var cts = new CancellationTokenSource();
        bool ok = false;
        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            dlg.IsPrimaryButtonEnabled = false; bar.Visibility = Visibility.Visible;
            try
            {
                await WhisperRefiner.DownloadAsync(SpeechRoot, new Progress<double>(p => bar.Value = p), cts.Token);
                ok = true;
            }
            catch (Exception ex) { args.Cancel = true; dlg.IsPrimaryButtonEnabled = true; Status("Download failed: " + ex.Message); }
            finally { deferral.Complete(); }
        };
        dlg.CloseButtonClick += (_, _) => cts.Cancel();
        await dlg.ShowAsync();
        return ok;
    }
}
