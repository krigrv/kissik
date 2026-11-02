using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenKamera.Core;
using Windows.System;

namespace OpenKamera_App;

/// <summary>
/// Eye-line prompter: a slim always-on-top strip at the top-centre of the screen, right under the webcam, so you read
/// while looking almost at the lens. Hidden from screen shares and recordings.
/// </summary>
public sealed class PrompterWindow : Window
{
    private readonly PrompterSettings _s;
    private readonly Action _save;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Disabled };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.White) };
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _play = new(), _edit = new();
    private readonly TextBlock _info = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Colors.White) };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private double _offset;
    private bool _playing;
    private DateTime _last = DateTime.UtcNow;

    public PrompterWindow(PrompterSettings settings, Action save)
    {
        _s = settings; _save = save;
        Title = "KISSIK prompter";
        _text.Text = _s.Script;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 6, 8, 0) };
        bar.Children.Add(Btn(_play, "", "Play or pause (Space)", TogglePlay));
        bar.Children.Add(Btn(new Button(), "", "Slower (Down arrow)", () => Speed(-10)));
        bar.Children.Add(Btn(new Button(), "", "Faster (Up arrow)", () => Speed(+10)));
        bar.Children.Add(Btn(new Button(), "", "Smaller text (minus)", () => Size(-2)));
        bar.Children.Add(Btn(new Button(), "", "Bigger text (plus)", () => Size(+2)));
        bar.Children.Add(Btn(new Button(), "", "Back to the start", () => { _offset = 0; _scroll.ChangeView(null, 0, null, true); }));
        bar.Children.Add(Btn(new Button(), "", "Mirror the text for a beam-splitter glass", () => { _s.Mirror = !_s.Mirror; ApplyLook(); _save(); }));
        bar.Children.Add(Btn(_edit, "", "Edit the script", ToggleEdit));
        bar.Children.Add(_info);

        _scroll.Content = _text;
        _scroll.Padding = new Thickness(18, 4, 18, 60);
        var body = new Grid();
        body.Children.Add(_scroll);
        body.Children.Add(_editor);
        var root = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 12, 12, 16)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(body, 1);
        root.Children.Add(bar); root.Children.Add(body);
        root.KeyDown += OnKey;
        Content = root;

        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)_s.Width, (int)_s.Height));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsAlwaysOnTop = true; p.IsMinimizable = false; p.IsMaximizable = false; }
        PlaceUnderWebcam();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowDisplayAffinity(hwnd, 0x11); // WDA_EXCLUDEFROMCAPTURE: never appears in shares or recordings
        AppWindow.Changed += (_, a) => { if (a.DidSizeChange) { _s.Width = AppWindow.Size.Width; _s.Height = AppWindow.Size.Height; _save(); } };

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); if (_editor.Visibility == Visibility.Visible) { _s.Script = _editor.Text; _save(); } };
        ApplyLook();
    }

    private static Button Btn(Button b, string glyph, string tip, Action click)
    {
        b.Content = new FontIcon { Glyph = glyph, FontSize = 14 };
        b.Width = 34; b.Height = 30; b.Padding = new Thickness(0);
        b.Background = new SolidColorBrush(Colors.Transparent); b.BorderThickness = new Thickness(0);
        b.Foreground = new SolidColorBrush(Colors.White);
        ToolTipService.SetToolTip(b, tip); AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private void PlaceUnderWebcam()
    {
        // Webcams sit on top of the main screen: centre the strip right below the top edge.
        var area = DisplayArea.Primary.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + 6));
    }

    private void ApplyLook()
    {
        _text.FontSize = _s.FontSize;
        _text.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _text.RenderTransform = new ScaleTransform { ScaleX = _s.Mirror ? -1 : 1 };
        ((FontIcon)_play.Content).Glyph = _playing ? "" : "";
        var mins = Prompter.ReadingTime(_s.Script, _s.WordsPerMinute);
        _info.Text = $"  {_s.WordsPerMinute} wpm · {_s.FontSize:0} pt · about {Math.Max(1, Math.Round(mins.TotalMinutes))} min";
    }

    private void TogglePlay() { _playing = !_playing; _last = DateTime.UtcNow; _offset = _scroll.VerticalOffset; ApplyLook(); }
    private void Speed(int d) { _s.WordsPerMinute = Math.Clamp(_s.WordsPerMinute + d, Prompter.MinWpm, Prompter.MaxWpm); ApplyLook(); _save(); }
    private void Size(int d) { _s.FontSize = Math.Clamp(_s.FontSize + d, 14, 72); ApplyLook(); _save(); }

    private void ToggleEdit()
    {
        bool editing = _editor.Visibility == Visibility.Visible;
        if (editing) { _s.Script = _editor.Text; _text.Text = _s.Script; _save(); _offset = 0; _scroll.ChangeView(null, 0, null, true); }
        else { _playing = false; _editor.Text = _s.Script; }
        _editor.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _scroll.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ((FontIcon)_edit.Content).Glyph = editing ? "" : "";
        if (!editing) _editor.Focus(FocusState.Programmatic);
        ApplyLook();
    }

    private void Tick()
    {
        var now = DateTime.UtcNow; double dt = (now - _last).TotalSeconds; _last = now;
        if (!_playing || _scroll.Visibility != Visibility.Visible) return;
        _offset += Prompter.PixelsPerSecond(_s.WordsPerMinute, _s.FontSize, Math.Max(100, _scroll.ActualWidth - 36)) * dt;
        if (_offset >= _scroll.ScrollableHeight) { _offset = _scroll.ScrollableHeight; _playing = false; ApplyLook(); }
        _scroll.ChangeView(null, _offset, null, true);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (_editor.Visibility == Visibility.Visible) return; // typing in the script
        switch (e.Key)
        {
            case VirtualKey.Space: TogglePlay(); break;
            case VirtualKey.Up: Speed(+10); break;
            case VirtualKey.Down: Speed(-10); break;
            case (VirtualKey)187: case VirtualKey.Add: Size(+2); break;
            case (VirtualKey)189: case VirtualKey.Subtract: Size(-2); break;
            default: return;
        }
        e.Handled = true;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
