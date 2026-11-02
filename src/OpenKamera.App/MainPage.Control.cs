using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Remote control for AI assistants (MCP), Stream Deck buttons and scripts: localhost only, secret token, off by default.
public sealed partial class MainPage
{
    private ControlApi? _control;
    private static string ControlTokenFile => Path.Combine(AppPaths.Data, "control-token.txt");

    private void InitControl() { if (_settings.ControlApiOn) StartControl(); ControlToggle.IsChecked = _control is not null; }

    /// <summary>Runs an action on the UI thread and hands back its answer.</summary>
    private Task<string> OnUi(Func<string> action)
    {
        var done = new TaskCompletionSource<string>();
        if (!DispatcherQueue.TryEnqueue(() => { try { done.SetResult(action()); } catch (Exception ex) { done.SetResult("Failed: " + ex.Message); } }))
            done.SetResult("Failed: KISSIK is closing.");
        return done.Task;
    }

    private IEnumerable<ControlTool> ControlTools()
    {
        var none = ControlApi.Schema();
        yield return new("get_status", "What KISSIK is doing now: camera, scene, privacy, recording, streaming, captions.", none,
            _ => OnUi(() => $"Camera: {(_session is null ? "not connected" : (DeviceBox.SelectedItem as CameraDeviceInfo)?.Name)}. Scene: {_scene.Name}. " +
                            $"Privacy: {_shutter}. Recording: {(_recorder is null ? "no" : _recPaused ? "paused" : "yes")}. Live: {(_live is null ? "no" : "yes")}. " +
                            $"Captions: {(_offline is null ? "off" : "on")}. Background: {_bg.Mode}. Speed: {_fps.Fps:0} fps."));
        yield return new("list_scenes", "Lists the scenes you can switch to.", none,
            _ => OnUi(() => string.Join("\n", _scenes.Select(s => $"{s.Shortcut}. {s.Name}"))));
        yield return new("switch_scene", "Switches to a scene by name (or its number).", ControlApi.Schema(("name", "string", "Scene name or number, e.g. \"Lower Third\" or \"2\"")),
            a => OnUi(() =>
            {
                string q = a["name"]?.ToString() ?? "";
                var s = int.TryParse(q, out int n) ? _scenes.FirstOrDefault(x => x.Shortcut == n)
                    : _scenes.FirstOrDefault(x => x.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) ?? _scenes.FirstOrDefault(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
                if (s is null) return "Failed: no scene called " + q;
                if (SceneList.Items.Contains(s)) SceneList.SelectedItem = s; else LoadScene(s);
                return "Switched to " + s.Name;
            }));
        yield return new("set_privacy", "Shows you, blurs you, or shows a be-right-back card (and mutes the mic).", ControlApi.Schema(("mode", "string", "visible, blur or away")),
            a => OnUi(() =>
            {
                var mode = (a["mode"]?.ToString() ?? "").ToLowerInvariant() switch { "blur" => ShutterMode.Blur, "away" => ShutterMode.Away, _ => ShutterMode.Off };
                SetShutter(mode); return "Privacy: " + mode;
            }));
        yield return new("set_background", "Background effect.", ControlApi.Schema(("mode", "string", "off, blur, replace or remove")),
            a => OnUi(() =>
            {
                string m = (a["mode"]?.ToString() ?? "off").ToLowerInvariant();
                BgMode.SelectedItem = m switch { "blur" => BgBlur, "replace" => BgReplace, "remove" => BgRemove, _ => BgOff };
                UpdateBackground(); return "Background: " + _bg.Mode;
            }));
        yield return new("start_recording", "Starts recording (after the 3-2-1 countdown).", none,
            _ => OnUi(() => { if (_recorder is not null) return "Already recording."; if (_session is null) return "Failed: no camera connected."; RecordBtn_Click(this, new RoutedEventArgs()); return "Recording starts after the countdown."; }));
        yield return new("stop_recording", "Stops and saves the recording.", none,
            _ => OnUi(() => { if (_recorder is null) return "Not recording."; RecordBtn_Click(this, new RoutedEventArgs()); return "Stopping and saving the recording."; }));
        yield return new("set_captions", "Turns live captions on or off.", ControlApi.Schema(("on", "boolean", "true to turn on")),
            a => OnUi(() =>
            {
                bool on = a["on"] is JsonValue v && v.TryGetValue(out bool b) && b;
                if (on == (_offline is not null)) return "Captions already " + (on ? "on" : "off");
                CaptionsToggle.IsChecked = on; CaptionsToggle_Click(this, new RoutedEventArgs());
                return "Captions turning " + (on ? "on" : "off");
            }));
        yield return new("take_snapshot", "Saves a still picture.", none,
            _ => OnUi(() => { if (_session is null) return "Failed: no camera connected."; SnapshotBtn_Click(this, new RoutedEventArgs()); return "Snapshot saved to your Pictures."; }));
    }

    private void StartControl()
    {
        if (_control is not null) return;
        _control = new ControlApi(ControlTools(), ControlTokenFile);
        _control.Start();
    }

    private async void ControlToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ControlApiOn = ControlToggle.IsChecked; _settings.Save();
        if (!ControlToggle.IsChecked) { if (_control is { } c) { _control = null; await c.DisposeAsync(); } Status("Remote control off."); return; }
        StartControl();
        string mcp = "{\n  \"mcpServers\": {\n    \"kissik\": {\n      \"type\": \"http\",\n      \"url\": \"" + _control!.BaseUrl + "/mcp\",\n      \"headers\": { \"Authorization\": \"Bearer " + _control.Token + "\" }\n    }\n  }\n}";
        var box = new TextBox { Text = mcp, IsReadOnly = true, AcceptsReturn = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "MCP settings to copy");
        var body = new StackPanel { Spacing = 10, MaxWidth = 460 };
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "AI assistants (like Claude), Stream Deck and scripts on this PC can now switch scenes, hide you, record and more. Only programs on this PC that have the key below can connect." });
        body.Children.Add(new TextBlock { Text = "Add this to your AI app's MCP settings:", FontSize = 12, Opacity = 0.8 });
        body.Children.Add(box);
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8, Text = $"Scripts and Stream Deck: POST {_control.BaseUrl}/tools/<name> with the same Authorization header. GET /tools lists them." });
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) => { var d = new Windows.ApplicationModel.DataTransfer.DataPackage(); d.SetText(mcp); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(d); Status("MCP settings copied."); };
        body.Children.Add(copy);
        await new ContentDialog { XamlRoot = XamlRoot, Title = "Remote control is on", Content = new ScrollViewer { Content = body, MaxHeight = 520 }, CloseButtonText = "Done" }.ShowAsync();
    }
}
