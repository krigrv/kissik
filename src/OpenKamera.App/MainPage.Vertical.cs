using Microsoft.UI.Xaml;
using OpenKamera.Core;

namespace OpenKamera_App;

// A vertical 9:16 copy recorded alongside the normal one (for Shorts, Reels, TikTok). It crops around you:
// the face tracker's position when framing is on, otherwise the centre.
public sealed partial class MainPage
{
    private SegmentedRecorder? _vertical;
    private int _verticalBusy;

    private void InitVertical() => VerticalToggle.IsChecked = _settings.RecordVertical;

    private void VerticalToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.RecordVertical = VerticalToggle.IsChecked; _settings.Save();
        Status(_settings.RecordVertical ? "Recordings will also save a vertical 9:16 copy (for Shorts, Reels, TikTok)." : "Vertical copy off.");
    }

    private async Task StartVerticalAsync(string mainPath)
    {
        if (!_settings.RecordVertical) return;
        int h = Math.Min(1920, _profile.Height >= 1080 ? 1920 : 1280), w = h * 9 / 16 & ~1;
        var v = new SegmentedRecorder(w, h, _profile.Fps, withAudio: _audio.IsRunning);
        await v.StartAsync(Path.ChangeExtension(mainPath, null) + " (vertical).mp4");
        _vertical = v;
    }

    /// <summary>Frame thread: crops the finished frame to 9:16 around the subject. Skipped if the last one is still being made.</summary>
    private void PushVertical(byte[] frame, int w, int h)
    {
        if (_vertical is not { } v || Interlocked.Exchange(ref _verticalBusy, 1) == 1) return;
        var at = _recordClock.Elapsed;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                int cw = Math.Min(w, h * 9 / 16) & ~1;
                double centre = 0.5 + _transform.PanX * 0.25; // follow framing's pan when Auto/Smart moves you
                int x0 = Math.Clamp((int)(centre * w) - cw / 2, 0, w - cw);
                var crop = new byte[cw * h * 4];
                for (int y = 0; y < h; y++) Buffer.BlockCopy(frame, (y * w + x0) * 4, crop, y * cw * 4, cw * 4);
                var (scaled, _, _) = OutputFit.Stretch(crop, cw, h, v.Width, v.Height);
                v.Push(scaled, v.Width, v.Height, at);
            }
            catch { }
            finally { Volatile.Write(ref _verticalBusy, 0); }
        });
    }

    private async Task<string> StopVerticalAsync()
    {
        var v = _vertical; _vertical = null;
        if (v is null) return "";
        try { await v.StopAsync(); return " · vertical copy saved too"; }
        catch (Exception ex) { return " · vertical copy failed: " + ex.Message; }
    }
}
