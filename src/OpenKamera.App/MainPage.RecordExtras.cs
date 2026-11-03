using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace OpenKamera_App;

// Recording extras: a 3-2-1 countdown, and pause/resume that stops the recording clock so the file has no gap.
public sealed partial class MainPage
{
    private volatile bool _recPaused;
    private CancellationTokenSource? _countdown;

    /// <summary>Shows 3, 2, 1 over the preview. Returns false if Record was pressed again to cancel.</summary>
    private async Task<bool> CountdownAsync(int seconds = 3)
    {
        _countdown = new CancellationTokenSource();
        try
        {
            CountdownText.Visibility = Visibility.Visible;
            RecordLabel.Text = "Cancel";
            for (int n = seconds; n >= 1; n--)
            {
                CountdownText.Text = n.ToString();
                await Task.Delay(1000, _countdown.Token);
            }
            return true;
        }
        catch (TaskCanceledException) { return false; }
        finally
        {
            CountdownText.Visibility = Visibility.Collapsed;
            if (_recorder is null) RecordLabel.Text = "Record";
            _countdown = null;
        }
    }

    private void PauseBtn_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        if (_recorder is null) return;
        _recPaused = !_recPaused;
        // The clock stops while paused, so frames and sound after resuming continue right where they left off.
        if (_recPaused) _recordClock.Stop(); else _recordClock.Start();
        SetPauseLook();
        Status(_recPaused ? "Recording paused. Nothing is being recorded." : "Recording resumed.");
    }

    private void SetPauseLook()
    {
        PauseIcon.Glyph = _recPaused ? "" : "";
        PauseLabel.Text = _recPaused ? "Resume" : "Pause";
        AutomationProperties.SetName(PauseBtn, _recPaused ? "Resume recording" : "Pause recording");
        RecPillText.Text = _recPaused ? $"❚❚ PAUSED {_recordClock.Elapsed:mm\\:ss}" : RecPillText.Text;
    }
}
