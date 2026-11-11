using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Replay buffer: while on, the last 30 seconds of the output are kept so a moment can be saved after it happened.
// Scene transitions: how the picture moves from one scene to the next.
public sealed partial class MainPage
{
    private ReplayBuffer? _replay;
    private readonly Stopwatch _replayClock = new();
    private static string ReplayDir => Path.Combine(Path.GetTempPath(), "KissikReplayBuffer");

    private async void ReplayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ReplayToggle.IsChecked) await StartReplayAsync(); else await StopReplayAsync();
    }

    private async Task StartReplayAsync()
    {
        if (_replay is not null) return;
        if (_session is null) { ReplayToggle.IsChecked = false; Status("Connect a camera first, then turn on the replay buffer."); return; }
        try
        {
            var p = _profile;
            var buf = new ReplayBuffer(ReplayDir, p.Width, p.Height, p.Fps, withAudio: _audio.IsRunning);
            await buf.StartAsync();
            _replayClock.Restart();
            _replay = buf;
            SaveReplayItem.IsEnabled = true;
            Status("Replay buffer on: KISSIK keeps the last 30 seconds. Save them from the menu or with Ctrl+Alt+S.");
        }
        catch (Exception ex) { ReplayToggle.IsChecked = false; Status("The replay buffer couldn't start: " + ex.Message); }
    }

    private async Task StopReplayAsync()
    {
        var buf = _replay; _replay = null;
        SaveReplayItem.IsEnabled = false;
        if (buf is not null) await buf.DisposeAsync();
        Status("Replay buffer off.");
    }

    private async void SaveReplay_Click(object sender, RoutedEventArgs e) => await SaveReplayAsync();

    internal async Task SaveReplayAsync()
    {
        if (_replay is not { } buf) { Status("Turn on the replay buffer first (menu → Replay buffer)."); return; }
        Status("Saving the last 30 seconds…");
        try
        {
            Directory.CreateDirectory(RecordingFolder());
            string path = Path.Combine(RecordingFolder(), $"KISSIK-replay-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            if (await buf.SaveAsync(path)) Status("Replay saved: " + path, "Show in folder", () => ShowInFolder(path));
            else Status("Nothing to save yet: give it a few seconds.");
        }
        catch (Exception ex) { Status("Couldn't save the replay: " + ex.Message); }
    }

    private void Transition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string t } || !Enum.TryParse<SceneTransition>(t, out var kind)) return;
        _settings.Transition = kind; _settings.Save();
        Status(kind == SceneTransition.Cut ? "Scenes switch instantly." : $"Scene transition: {kind}.");
    }

    private void InitTransitions()
    {
        foreach (var item in new[] { TrFade, TrSlide, TrZoom, TrCut })
            item.IsChecked = (string)item.Tag == _settings.Transition.ToString();
    }
}
