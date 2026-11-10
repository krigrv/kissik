using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OpenKamera.Core;

namespace OpenKamera_App;

// Universal design: screen-reader announcements, Large controls, Windows "reduce motion",
// and the Simple mode (five big buttons) next to the full Studio window.
public sealed partial class MainPage
{
    /// <summary>Windows Settings → Accessibility → Visual effects → Animation effects is off.</summary>
    private static bool ReduceMotion
    {
        get { try { return !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; } catch { return false; } }
    }

    /// <summary>Says <paramref name="msg"/> through Narrator (or any screen reader), replacing an older message still queued.</summary>
    private void Announce(string msg)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(StatusText) ?? FrameworkElementAutomationPeer.CreatePeerForElement(StatusText);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent, msg, "KissikStatus");
        }
        catch { /* no automation client listening */ }
    }

    private static readonly Windows.UI.ViewManagement.AccessibilitySettings _a11y = new();

    /// <summary>A Windows contrast theme (Aquatic, Desert, Dusk, Night sky) is on.</summary>
    internal static bool IsHighContrast { get { try { return _a11y.HighContrast; } catch { return false; } } }

    /// <summary>Contrast themes: the caption bar takes the user's own window colours instead of white on black.</summary>
    private void ApplyContrast()
    {
        if (IsHighContrast)
        {
            CaptionBar.Background = (Brush)Application.Current.Resources["SystemColorWindowColorBrush"];
            CaptionText.Foreground = (Brush)Application.Current.Resources["SystemColorWindowTextColorBrush"];
            CaptionBar.BorderBrush = CaptionText.Foreground; CaptionBar.BorderThickness = new Thickness(2);
        }
        else
        {
            CaptionBar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD9, 0, 0, 0));
            CaptionText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            CaptionBar.BorderThickness = new Thickness(0);
        }
    }

    private void InitAccess()
    {
        ApplyContrast();
        _a11y.HighContrastChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { ApplyContrast(); ApplyTheme(_settings.Theme); });
        AddLayerNudgeKeys();
        LargeControlsToggle.IsChecked = _settings.LargeControls;
        if (_settings.LargeControls) ApplyLargeControls(true);
        ApplyUiMode(_settings.UiMode == "Simple" ? "Simple" : "Studio");
    }

    // ── Keyboard moving of scene layers (the drag on the video is mouse/touch only) ──

    private void AddLayerNudgeKeys()
    {
        var ctrl = Windows.System.VirtualKeyModifiers.Control;
        var shift = Windows.System.VirtualKeyModifiers.Shift;
        foreach (var (key, dx, dy) in new (Windows.System.VirtualKey, int, int)[]
            { (Windows.System.VirtualKey.Left, -1, 0), (Windows.System.VirtualKey.Right, 1, 0),
              (Windows.System.VirtualKey.Up, 0, -1), (Windows.System.VirtualKey.Down, 0, 1) })
        {
            foreach (var (mods, step) in new[] { (ctrl, 0.01), (ctrl | shift, 0.10) })
            {
                var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = mods };
                acc.Invoked += (_, a) =>
                {
                    if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
                    if (NudgeLayer(dx * step, dy * step)) a.Handled = true;
                };
                KeyboardAccelerators.Add(acc);
            }
        }
    }

    /// <summary>Ctrl+arrow moves the selected layer 1% of the picture, Ctrl+Shift+arrow 10%; one undo step each.</summary>
    private bool NudgeLayer(double dx, double dy)
    {
        if (Selected is not { } l) return false;
        BeginEdit();
        l.X = Math.Clamp(l.X + dx, -l.W * 0.9, 1 - l.W * 0.1);
        l.Y = Math.Clamp(l.Y + dy, -l.H * 0.9, 1 - l.H * 0.1);
        Commit(rebuildProps: false);
        UpdateAdorner();
        Announce($"{(l.Name.Length > 0 ? l.Name : l.Kind)}: {l.X * 100:0}% from the left, {l.Y * 100:0}% from the top.");
        return true;
    }

    // ── Large controls ──

    private readonly Dictionary<Control, (double MinW, double MinH, double Font)> _normalSize = [];

    private void LargeControls_Click(object sender, RoutedEventArgs e)
    {
        _settings.LargeControls = LargeControlsToggle.IsChecked; _settings.Save();
        ApplyLargeControls(_settings.LargeControls);
        Status(_settings.LargeControls ? "Large controls on." : "Large controls off.");
    }

    /// <summary>Every button, switch, list and slider gets at least 48 px and 16 px text; off puts back what each had.</summary>
    private void ApplyLargeControls(bool on)
    {
        if (!on)
        {
            foreach (var (c, (w, h, f)) in _normalSize) { c.MinWidth = w; c.MinHeight = h; c.FontSize = f; }
            _normalSize.Clear();
            return;
        }
        foreach (var c in Descendants(Root).OfType<Control>())
        {
            if (c is not (ButtonBase or ComboBox or ToggleSwitch or Slider or TextBox)) continue;
            if (_normalSize.ContainsKey(c)) continue;
            _normalSize[c] = (c.MinWidth, c.MinHeight, c.FontSize);
            c.MinWidth = Math.Max(c.MinWidth, 48); c.MinHeight = Math.Max(c.MinHeight, 48);
            c.FontSize = Math.Max(c.FontSize, 16);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    // ── Simple / Studio ──

    private readonly List<(FrameworkElement El, Visibility Was)> _studioOnly = [];

    private void UiMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string m }) SetUiMode(m);
    }

    private void SetUiMode(string mode)
    {
        _settings.UiMode = mode; _settings.Save();
        ApplyUiMode(mode);
        Status(mode == "Simple" ? "Simple mode: five big buttons. Switch back from the menu, View, Studio." : "Studio mode: every control is shown.");
    }

    private void ApplyUiMode(string mode)
    {
        bool simple = mode == "Simple";
        ModeSimpleItem.IsChecked = simple; ModeStudioItem.IsChecked = !simple;
        if (simple && _studioOnly.Count == 0)
        {
            foreach (var el in new FrameworkElement[] { LeftPane, RightPane, AudioCard, Filmstrip, CapsuleRow })
            { _studioOnly.Add((el, el.Visibility)); el.Visibility = Visibility.Collapsed; }
        }
        else if (!simple && _studioOnly.Count > 0)
        {
            foreach (var (el, was) in _studioOnly) el.Visibility = was;
            _studioOnly.Clear();
        }
        SimplePanel.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        // Simple mode: messages sit in the empty left of the top bar. Bottom-right they covered the "Hide me" button.
        MessageArea.HorizontalAlignment = simple ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        MessageArea.VerticalAlignment = simple ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        MessageArea.Margin = simple ? new Thickness(16, 6, 0, 0) : new Thickness(0, 0, 12, 12);
        MessageArea.MaxWidth = simple ? 520 : 236;
        StatusText.MaxLines = simple ? 2 : 3;
        // Simple mode has no Source panel: its camera list sits above the big buttons.
        EmptyStep1.Text = simple ? "1. Pick your camera in the list at the bottom of the window."
                                 : "1. Pick your camera where the orange bubble points, under Source on the left.";
        UpdateStageMargin();
        SyncSimple();
    }

    /// <summary>Keeps the five big buttons' words in step with what the app is doing.</summary>
    private void SyncSimple()
    {
        if (SimplePanel.Visibility != Visibility.Visible) return;
        SyncSimpleSource();
        SetSimple(SimpleCameraBtn, SimpleCameraLabel, _session is null ? "Turn camera on" : "Turn camera off");
        SetSimple(SimpleRecordBtn, SimpleRecordLabel, _recorder is not null ? "Stop recording" : "Record");
        SimpleRecordBtn.IsEnabled = _session is not null || _recorder is not null;
        SetSimple(SimpleHideBtn, SimpleHideLabel, _shutter == ShutterMode.Off ? "Hide me" : "Show me");
        string bg = BgMode.SelectedItem == BgBlur ? "Background: blur" : BgMode.SelectedItem == BgReplace ? "Background: picture"
            : BgMode.SelectedItem == BgRemove ? "Background: removed" : "Background: off";
        SetSimple(SimpleBgBtn, SimpleBgLabel, bg);
    }

    private static void SetSimple(Button b, TextBlock label, string text)
    {
        label.Text = text; AutomationProperties.SetName(b, text);
    }

    private async void SimpleCamera_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null && DeviceBox.SelectedItem is null && DeviceBox.Items.Count > 0) DeviceBox.SelectedIndex = 0;
        await ConnectAsync(toggle: true);
        SyncSimple();
    }

    private void SimpleBackground_Click(object sender, RoutedEventArgs e)
    {
        // Off → Blur → Picture → Off: one button, three plain choices.
        BgMode.SelectedItem = BgMode.SelectedItem == BgOff ? BgBlur : BgMode.SelectedItem == BgBlur ? BgReplace : BgOff;
        UpdateBackground();
        SyncSimple();
        Status(SimpleBgLabel.Text + ".");
    }

    private void SimpleRecord_Click(object sender, RoutedEventArgs e)
    {
        RecordBtn_Click(sender, e);
        SyncSimple();
    }

    private void SimpleMeet_Click(object sender, RoutedEventArgs e)
    {
        RefreshMeetingCamera();
        MeetCamFlyout.ShowAt(SimpleMeetBtn);
    }

    private void SimpleHide_Click(object sender, RoutedEventArgs e)
    {
        SetShutter(_shutter == ShutterMode.Off ? ShutterMode.Away : ShutterMode.Off);
        SyncSimple();
    }

    /// <summary>First run: pick Simple or Studio (Studio if the dialog could not show).</summary>
    private async Task AskUiModeAsync()
    {
        if (_settings.UiMode is "Simple" or "Studio") return;
        var body = new StackPanel { Spacing = 10, MaxWidth = 440 };
        body.Children.Add(new TextBlock { Text = "How would you like KISSIK to look? You can change this any time from the menu, under View.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "Simple: five big buttons — Turn camera on, Background, Record, Meeting camera, Hide me.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "Studio: every control, for scenes, lighting, audio and livestreams.", TextWrapping = TextWrapping.Wrap });
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Simple or Studio?", Content = body,
            PrimaryButtonText = "Simple", SecondaryButtonText = "Studio", DefaultButton = ContentDialogButton.Primary,
        };
        string mode = "Studio";
        try { mode = await dlg.ShowAsync() == ContentDialogResult.Primary ? "Simple" : "Studio"; } catch { /* another dialog was open */ }
        SetUiMode(mode);
    }
}
