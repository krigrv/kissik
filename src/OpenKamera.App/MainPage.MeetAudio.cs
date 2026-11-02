using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// "Send to meetings": routes the finished mix (EQ, limiter, volume) into a virtual cable. Meetings then use the
// cable's other end ("CABLE Output") as their microphone and hear the processed voice.
public sealed partial class MainPage
{
    private sealed record OutChoice(string? Id, string Name) { public override string ToString() => Name; }
    private bool _meetOutLoading;

    private async Task RefreshMeetOutAsync()
    {
        var outs = await AudioEngine.ListOutputsAsync();
        var cable = AudioEngine.FindVirtualCable(outs);
        // First run with a cable installed: use it, since that is the only reason to have one.
        if (Mix.MeetingOutputId is null && cable is { } c && !_settings.MeetOutChosen) { Mix.MeetingOutputId = c.Id; _settings.Save(); }
        var items = new List<OutChoice> { new(null, "Off (recordings only)") };
        items.AddRange(outs.Select(o => new OutChoice(o.Id, AudioEngine.IsVirtualCable(o.Name) ? o.Name + "  ★ virtual mic" : o.Name)));
        _meetOutLoading = true;
        MeetOutBox.ItemsSource = items;
        MeetOutBox.SelectedItem = items.FirstOrDefault(i => i.Id == Mix.MeetingOutputId) ?? items[0];
        _meetOutLoading = false;
        UpdateMeetOutHint(cable is not null);
    }

    private void UpdateMeetOutHint(bool hasCable)
    {
        MeetOutHint.Text = Mix.MeetingOutputId is null
            ? hasCable ? "Pick the CABLE Input above so meetings hear your mixed voice."
                       : "Meetings take your mic directly, so they don't hear the effects here. Install the free VB-CABLE (vb-audio.com/Cable), then pick \"CABLE Input\" here and \"CABLE Output\" as the microphone in Zoom, Teams or Meet."
            : AudioEngine.IsVirtualCable((MeetOutBox.SelectedItem as OutChoice)?.Name ?? "")
                ? "In Zoom, Teams or Meet choose \"CABLE Output\" as the microphone."
                : "This plays your mix out loud on that device. Use a virtual cable for meetings to avoid echo.";
    }

    private async void MeetOutBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_meetOutLoading || MeetOutBox.SelectedItem is not OutChoice choice) return;
        Mix.MeetingOutputId = choice.Id;
        _settings.MeetOutChosen = true;
        _settings.Save();
        UpdateMeetOutHint(AudioEngine.FindVirtualCable(await AudioEngine.ListOutputsAsync()) is not null);
        if (_audio.IsRunning) await StartAudioAsync(); // the output is fixed when the graph is built
    }
}
