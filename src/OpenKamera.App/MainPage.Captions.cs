using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;

namespace OpenKamera_App;

// Live captions: an offline speech model on this PC turns your voice into text, shown over the preview and burnt into
// the meeting camera and recordings so everyone can read along.
public sealed partial class MainPage
{
    private OfflineCaptions? _offline;
    private readonly CaptionBuffer _captions = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _captionTimer;
    private volatile CaptionImage? _captionImage;
    private string _shownCaption = "";

    private sealed record CaptionImage(byte[] Pixels, int Width, int Height);

    private static string SpeechRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenKamera", "Speech");

    private async void CaptionsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (CaptionsToggle.IsChecked) await StartCaptionsAsync(); else await StopCaptionsAsync();
    }

    /// <summary>Captions run entirely on this PC (offline model); the first time, the model is downloaded once.</summary>
    private async Task StartCaptionsAsync()
    {
        try
        {
            var lang = OfflineCaptions.Languages.FirstOrDefault(l => l.Code == _settings.CaptionLanguage) ?? OfflineCaptions.Languages[0];
            string? dir = OfflineCaptions.ModelDir(SpeechRoot, lang.Code);
            if (dir is null) dir = await DownloadCaptionModelAsync();
            if (dir is null) { CaptionsToggle.IsChecked = false; return; }
            if (!_audio.IsRunning) { AudioOnToggle.IsOn = true; await Task.Delay(500); } // captions listen to the audio mix
            _offline = await Task.Run(() => new OfflineCaptions(dir));
            _offline.Partial += t => DispatcherQueue.TryEnqueue(() => _captions.Partial(t, DateTime.UtcNow));
            _offline.Final += t => DispatcherQueue.TryEnqueue(() => _captions.Final(t, DateTime.UtcNow));

            _captionTimer ??= DispatcherQueue.CreateTimer();
            _captionTimer.Interval = TimeSpan.FromMilliseconds(150);
            _captionTimer.Tick -= CaptionTick; _captionTimer.Tick += CaptionTick;
            _captionTimer.Start();
            Status("Live captions on, worked out on this PC (nothing is sent anywhere). Everyone in the meeting sees them.");
        }
        catch (Exception ex)
        {
            CaptionsToggle.IsChecked = false;
            await StopCaptionsAsync();
            Status("Captions unavailable: " + ex.Message);
        }
    }

    /// <summary>Called from the audio mix (any thread).</summary>
    private void FeedCaptions(float[] samples)
    {
        _offline?.Enqueue(samples);
    }

    /// <summary>One-time download of the offline speech model, with a language choice and progress.</summary>
    private async Task<string?> DownloadCaptionModelAsync()
    {
        var pick = new ComboBox { ItemsSource = OfflineCaptions.Languages.Select(l => $"{l.Name} ({l.SizeMb} MB)").ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pick, "Caption language");
        var body = new StackPanel { Spacing = 12, MaxWidth = 420 };
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Captions are worked out on your PC, so nothing you say leaves it. KISSIK needs a small speech model once (from the open-source Vosk project)." });
        body.Children.Add(pick);
        var res = await new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Turn on live captions", Content = body,
            PrimaryButtonText = "Download and turn on", CloseButtonText = "Cancel", DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
        }.ShowAsync();
        if (res != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return null;
        var lang = OfflineCaptions.Languages[Math.Max(0, pick.SelectedIndex)];
        _settings.CaptionLanguage = lang.Code; _settings.Save();
        try
        {
            var progress = new Progress<double>(p => Status($"Downloading the {lang.Name} speech model… {p:P0}"));
            string dir = await OfflineCaptions.DownloadAsync(lang, SpeechRoot, progress);
            Status($"{lang.Name} speech model ready.");
            return dir;
        }
        catch (Exception ex) { Status("Couldn't download the speech model: " + ex.Message + ". Check your internet connection and try again."); return null; }
    }

    private async Task StopCaptionsAsync()
    {
        _captionTimer?.Stop();
        if (_captionTimer is not null) _captionTimer.Tick -= CaptionTick;
        var o = _offline; _offline = null;
        if (o is not null) await Task.Run(o.Dispose); // never on the UI thread: it waits for the worker
        _captionImage = null; _shownCaption = "";
        CaptionBar.Visibility = Visibility.Collapsed;
    }

    private async void CaptionTick(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object o)
    {
        string text = _captions.Text(DateTime.UtcNow);
        if (text == _shownCaption) return;
        _shownCaption = text;
        CaptionText.Text = text;
        CaptionBar.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length == 0) { _captionImage = null; return; }
        // Rasterise the bar for the outgoing video at a size proportional to the output frame.
        try
        {
            await Task.Yield();
            var rtb = new RenderTargetBitmap();
            int targetW = (int)Math.Min(_profile.Width * 0.8, CaptionBar.ActualWidth * _profile.Height / Math.Max(1, ActualHeight) * 1.0);
            await rtb.RenderAsync(CaptionBar, Math.Max(1, targetW), 0);
            var px = (await rtb.GetPixelsAsync()).ToArray();
            _captionImage = new CaptionImage(px, rtb.PixelWidth, rtb.PixelHeight);
        }
        catch { /* the preview keeps its captions even if the outgoing copy fails */ }
    }

    /// <summary>Frame thread: burns the current caption into the outgoing picture.</summary>
    private byte[] BurnCaptions(byte[] frame, int w, int h)
    {
        if (_captionImage is not { } c || c.Width > w || c.Height > h) return frame;
        var copy = (byte[])frame.Clone();
        CaptionBurn.Blend(copy, w, h, c.Pixels, c.Width, c.Height);
        return copy;
    }
}
