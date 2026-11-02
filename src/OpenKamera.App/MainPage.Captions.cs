using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;
using Windows.Media.SpeechRecognition;

namespace OpenKamera_App;

// Live captions: Windows speech recognition turns your voice into text, shown over the preview and burnt into
// the meeting camera and recordings so everyone can read along.
public sealed partial class MainPage
{
    private SpeechRecognizer? _speech;
    private readonly CaptionBuffer _captions = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _captionTimer;
    private volatile CaptionImage? _captionImage;
    private string _shownCaption = "";

    private sealed record CaptionImage(byte[] Pixels, int Width, int Height);

    private async void CaptionsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (CaptionsToggle.IsChecked) await StartCaptionsAsync(); else await StopCaptionsAsync();
    }

    private async Task StartCaptionsAsync()
    {
        try
        {
            _speech = new SpeechRecognizer();
            _speech.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await _speech.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success) throw new InvalidOperationException(compiled.Status.ToString());
            _speech.HypothesisGenerated += (_, a) => DispatcherQueue.TryEnqueue(() => _captions.Partial(a.Hypothesis.Text, DateTime.UtcNow));
            _speech.ContinuousRecognitionSession.ResultGenerated += (_, a) => DispatcherQueue.TryEnqueue(() => _captions.Final(a.Result.Text, DateTime.UtcNow));
            _speech.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromHours(4);
            await _speech.ContinuousRecognitionSession.StartAsync();

            _captionTimer ??= DispatcherQueue.CreateTimer();
            _captionTimer.Interval = TimeSpan.FromMilliseconds(150);
            _captionTimer.Tick += CaptionTick;
            _captionTimer.Start();
            Status("Live captions on. Everyone in the meeting sees them.");
        }
        catch (Exception ex)
        {
            CaptionsToggle.IsChecked = false;
            await StopCaptionsAsync();
            // Dictation needs Windows' speech privacy switch; it's the user's setting, so point to it.
            bool privacy = ex.HResult == unchecked((int)0x80045509) || ex.Message.Contains("privacy", StringComparison.OrdinalIgnoreCase)
                           || ex.Message.Contains("TopicLanguageNotSupported");
            Status(privacy ? "Captions need Windows speech recognition. Turn on Settings → Privacy & security → Speech → Online speech recognition, then try again."
                           : "Captions unavailable: " + ex.Message);
            if (privacy) _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-speech"));
        }
    }

    private async Task StopCaptionsAsync()
    {
        _captionTimer?.Stop();
        if (_captionTimer is not null) _captionTimer.Tick -= CaptionTick;
        if (_speech is { } s)
        {
            try { await s.ContinuousRecognitionSession.StopAsync(); } catch { }
            s.Dispose();
        }
        _speech = null; _captionImage = null; _shownCaption = "";
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
