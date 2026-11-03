using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OpenKamera.Core;

namespace OpenKamera_App;

/// <summary>Screen light: the panel, one light window per chosen display, monitor brightness and auto-tune.</summary>
public sealed partial class MainPage
{
    private readonly Dictionary<string, ScreenLightWindow> _lightWindows = [];
    private readonly List<(LightLook Look, Border Card)> _lookCards = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoTuneTimer;
    private int? _savedMonitorBrightness;
    private bool _lightLoading;
    /// <summary>False until InitScreenLight ran: sliders fire ValueChanged while XAML loads, before later controls exist.</summary>
    private bool _lightReady;

    private ScreenLightSettings Light => _settings.ScreenLight;

    private void InitScreenLight()
    {
        _lightLoading = true;
        // Range in code: compiled XAML sets Minimum before Maximum, and 2200 > the default maximum (100) throws.
        LightKelvin.Maximum = 9000; LightKelvin.Minimum = 2200;
        BuildLightLooks();
        BuildLightDisplays();
        LightSize.Value = Light.Size * 100;
        LightKelvin.Value = Math.Clamp(Light.Kelvin, 2200, 9000);
        LightBrightness.Value = Light.Brightness * 100;
        LightAutoTune.IsOn = Light.AutoTune;
        LightWholeScreen.IsChecked = Light.WholeScreen;
        InitRingControls();
        ScreenBrightnessUse.IsChecked = Light.ScreenBrightness is not null;
        if (Light.ScreenBrightness is int sb) ScreenBrightnessSlider.Value = sb;
        LightOnToggle.IsOn = false; // never start with the screen lit
        Light.On = false;
        _lightLoading = false;
        _lightReady = true;
        RefreshLightUi();

        _autoTuneTimer = DispatcherQueue.CreateTimer();
        _autoTuneTimer.Interval = TimeSpan.FromMilliseconds(700);
        _autoTuneTimer.Tick += (_, _) => AutoTuneTick();
        _autoTuneTimer.Start();
        Unloaded += (_, _) => TurnLightOff();
    }

    private void LightBtn_Click(object sender, RoutedEventArgs e)
    {
        LightPanel.Visibility = LightPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (LightPanel.Visibility == Visibility.Visible) { BuildLightDisplays(); RefreshMonitorBrightnessHint(); }
    }

    private void LightClose_Click(object sender, RoutedEventArgs e) => LightPanel.Visibility = Visibility.Collapsed;

    private void BuildLightLooks()
    {
        LightLooks.Children.Clear();
        _lookCards.Clear();
        foreach (var look in ScreenLight.Looks)
        {
            var (r, g, b) = ScreenLight.LightColor(new ScreenLightSettings { Kelvin = look.Kelvin, Tint = look.Tint, Brightness = look.Brightness });
            var lit = Microsoft.UI.ColorHelper.FromArgb(255, r, g, b);
            var card = new Border
            {
                Width = 196, Height = 112, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                // A face-lit-by-the-screen feel: light from the left fading into the room.
                Background = new LinearGradientBrush
                {
                    StartPoint = new(0, 0.5), EndPoint = new(1, 0.5),
                    GradientStops =
                    {
                        new GradientStop { Color = lit, Offset = 0 },
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, (byte)(r / 3), (byte)(g / 3), (byte)(b / 3)), Offset = 0.55 },
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, 14, 16, 20), Offset = 1 },
                    },
                },
                Child = new Grid
                {
                    Children =
                    {
                        new FontIcon { Glyph = "", FontSize = 40, Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(170, 255, 255, 255)) },
                        new TextBlock
                        {
                            Text = look.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 10),
                            VerticalAlignment = VerticalAlignment.Bottom, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                        },
                    },
                },
            };
            var btn = new Button { Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Content = card };
            AutomationProperties.SetName(btn, $"Light look: {look.Name}");
            btn.Click += (_, _) => { ScreenLight.ApplyLook(Light, look); LoadLightControls(); ApplyLight(); };
            LightLooks.Children.Add(btn);
            _lookCards.Add((look, card));
        }
    }

    private void BuildLightDisplays()
    {
        LightDisplays.Children.Clear();
        var displays = AllDisplays();
        var mine = DisplayArea.GetFromWindowId(App.MainWindow!.AppWindow.Id, DisplayAreaFallback.Nearest);
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            string id = d.DisplayId.Value.ToString();
            bool selected = Light.Displays.Count == 0 ? d.DisplayId.Value == mine.DisplayId.Value : Light.Displays.Contains(id);
            var cb = new CheckBox
            {
                Content = $"Display {i + 1}  ·  {d.OuterBounds.Width}×{d.OuterBounds.Height}{(d.IsPrimary ? "  (main)" : "")}",
                IsChecked = selected, Tag = id, FontSize = 12,
            };
            cb.Click += (_, _) =>
            {
                Light.Displays = LightDisplays.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
                ApplyLight();
            };
            LightDisplays.Children.Add(cb);
        }
    }

    private void LoadLightControls()
    {
        _lightLoading = true;
        LightKelvin.Value = Math.Clamp(Light.Kelvin, 2200, 9000);
        LightBrightness.Value = Light.Brightness * 100;
        _lightLoading = false;
        RefreshLightUi();
    }

    private void RefreshLightUi()
    {
        var look = ScreenLight.Looks.FirstOrDefault(l => l.Id == Light.LookId);
        LightDescription.Text = look?.Description ?? "Your own light: temperature, colour and shape set by hand.";
        foreach (var (l, card) in _lookCards)
            card.BorderBrush = l.Id == Light.LookId ? Token("DgAccentBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        LightSizeValue.Text = $"{LightSize.Value:0}%";
        LightKelvinValue.Text = Light.Tint is null ? $"{LightKelvin.Value:N0} K" : "Custom colour";
        LightBrightnessValue.Text = $"{LightBrightness.Value:0}%";
        ScreenBrightnessValue.Text = ScreenBrightnessUse.IsChecked == true ? $"{ScreenBrightnessSlider.Value:0}%" : "unchanged";
        foreach (var (tile, shape) in new (ToggleButton, LightShape)[]
            { (LightShapeSide, LightShape.SideBars), (LightShapeTop, LightShape.TopBar), (LightShapeFrame, LightShape.Frame), (LightShapeRing, LightShape.Ring) })
            tile.IsChecked = Light.Shape == shape;
        LightOnLabel.Text = Light.On ? "Lights on" : "Lights off";
        LightBtnIcon.Foreground = Light.On ? Token("DgAccentBrush") : Token("DgText");
        LightCoverage.Text = $"Covers about {ScreenLight.Coverage(Light.Shape, Light.Size, 1920, 1080):P0} of the screen. Whole-screen light is hidden from screen shares and recordings.";
        RefreshRingUi();
        SetColorButton(LightColorBtn, Light.Tint ?? "#FFFFFFFF", hex =>
        {
            Light.Tint = hex.Equals("#FFFFFFFF", StringComparison.OrdinalIgnoreCase) ? null : hex;
            Light.LookId = "custom";
            RefreshLightUi(); ApplyLight();
        });
    }

    private void Light_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_lightReady || _lightLoading) return;
        Light.Size = LightSize.Value / 100.0;
        if (ReferenceEquals(sender, LightKelvin)) { Light.Kelvin = (int)LightKelvin.Value; Light.Tint = null; Light.LookId = "custom"; }
        Light.Brightness = LightBrightness.Value / 100.0;
        RefreshLightUi();
        ApplyLight();
    }

    private void LightShape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string s } && Enum.TryParse<LightShape>(s, out var shape)) Light.Shape = shape;
        RefreshLightUi();
        ApplyLight();
    }

    private void LightReset_Click(object sender, RoutedEventArgs e)
    {
        var look = ScreenLight.Looks.FirstOrDefault(l => l.Id == Light.LookId) ?? ScreenLight.Looks[0];
        ScreenLight.ApplyLook(Light, look);
        Light.Size = 0.25; Light.Shape = LightShape.Ring;
        _lightLoading = true; LightSize.Value = 25; _lightLoading = false;
        LoadLightControls();
        ApplyLight();
    }

    private void LightOnToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_lightReady || _lightLoading) return;
        Light.On = LightOnToggle.IsOn;
        if (Light.On) ApplyLight(); else TurnLightOff();
        RefreshLightUi();
    }

    private void LightAutoTune_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_lightReady || _lightLoading) return;
        Light.AutoTune = LightAutoTune.IsOn;
        _settings.Save();
    }

    private void ScreenBrightnessUse_Click(object sender, RoutedEventArgs e)
    {
        Light.ScreenBrightness = ScreenBrightnessUse.IsChecked == true ? (int)ScreenBrightnessSlider.Value : null;
        if (Light.ScreenBrightness is null) RestoreMonitorBrightness();
        RefreshLightUi(); ApplyLight();
    }

    private void ScreenBrightness_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_lightReady || _lightLoading) return;
        if (ScreenBrightnessUse.IsChecked == true) Light.ScreenBrightness = (int)e.NewValue;
        RefreshLightUi(); ApplyLight();
    }

    /// <summary>
    /// Shows/updates the light. Default: drawn inside OpenKamera's window around the video. With
    /// "whole screen" ticked: separate light windows over each chosen display, on top of every app.
    /// </summary>
    private void ApplyLight()
    {
        _settings.Save();
        LightDisplays.Visibility = LightDisplaysLabel.Visibility = Light.WholeScreen ? Visibility.Visible : Visibility.Collapsed;
        if (!Light.On) return;
        if (!Light.WholeScreen)
        {
            CloseLightWindows();
            DrawInAppLight();
        }
        else
        {
            LightLayer.Visibility = Visibility.Collapsed;
            var mainId = DisplayArea.GetFromWindowId(App.MainWindow!.AppWindow.Id, DisplayAreaFallback.Nearest).DisplayId.Value;
            var wanted = AllDisplays().Where(d =>
                Light.Displays.Count == 0 ? d.DisplayId.Value == mainId : Light.Displays.Contains(d.DisplayId.Value.ToString())).ToList();
            foreach (var id in _lightWindows.Keys.Except(wanted.Select(d => d.DisplayId.Value.ToString())).ToList())
            {
                _lightWindows[id].Close(); _lightWindows.Remove(id);
            }
            foreach (var d in wanted)
            {
                string id = d.DisplayId.Value.ToString();
                if (!_lightWindows.TryGetValue(id, out var w)) _lightWindows[id] = w = new ScreenLightWindow(d);
                w.Apply(Light);
            }
        }
        if (Light.ScreenBrightness is int pct)
        {
            var r = DisplayArea.GetFromWindowId(App.MainWindow!.AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
            _savedMonitorBrightness ??= MonitorBrightness.Get(r.X + r.Width / 2, r.Y + r.Height / 2);
            if (!MonitorBrightness.Set(r.X + r.Width / 2, r.Y + r.Height / 2, pct))
                ScreenBrightnessHint.Text = "This monitor doesn't accept brightness changes from apps (DDC/CI off or unsupported).";
        }
    }

    /// <summary>Draws the light shape inside the window, over the video's edges and under the glass panels.</summary>
    private void DrawInAppLight()
    {
        LightLayer.Children.Clear();
        // Measure the window, not the layer: the layer is collapsed (0×0) until the first draw.
        double w = Math.Max(4, Root.ActualWidth), h = Math.Max(4, Root.ActualHeight);
        var (r, g, b) = ScreenLight.LightColor(Light);
        var color = Microsoft.UI.ColorHelper.FromArgb(255, r, g, b);
        var brush = new SolidColorBrush(color);
        if (Light.Shape == LightShape.Ring) DrawRing(w, h, color);
        else
        {
            foreach (var a in ScreenLight.Areas(Light.Shape, Light.Size, (int)w, (int)h))
            {
                if (a.W <= 0 || a.H <= 0) continue;
                var rect = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = a.W, Height = a.H, Fill = brush };
                Canvas.SetLeft(rect, a.X); Canvas.SetTop(rect, a.Y);
                LightLayer.Children.Add(rect);
            }
        }
        LightLayer.Visibility = Visibility.Visible;
    }

    private void DrawRing(double w, double h, Windows.UI.Color color)
    {
        var (cx, cy, radius, band) = ScreenLight.RingLayout(Light, w, h);
        var (start, sweep) = ScreenLight.DirectionArc(Light.Direction, Light.ArcStart, Light.ArcSweep);
        double soft = Math.Clamp(Light.Softness, 0, 1);
        switch (Light.RingType)
        {
            case RingType.Halo:
            {
                var halo = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = radius * 2, Height = radius * 2,
                    Fill = new RadialGradientBrush
                    {
                        GradientStops =
                        {
                            new GradientStop { Color = color, Offset = 0 },
                            new GradientStop { Color = color, Offset = Math.Max(0.05, 0.85 - soft * 0.75) },
                            new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(0, color.R, color.G, color.B), Offset = 1 },
                        },
                    },
                };
                Canvas.SetLeft(halo, cx - radius); Canvas.SetTop(halo, cy - radius);
                LightLayer.Children.Add(halo);
                break;
            }
            case RingType.Double:
                AddArc(cx, cy, radius - band * 0.18, start, sweep, band * 0.36, color, 1);
                AddArc(cx, cy, radius - band * 0.82, start, sweep, band * 0.36, color, 1);
                break;
            case RingType.Led:
                foreach (var (x, y) in ScreenLight.LedPositions(cx, cy, radius - band / 2, band, start, sweep))
                {
                    double d = band * 0.9;
                    if (soft > 0.05)
                    {
                        var glow = new Microsoft.UI.Xaml.Shapes.Ellipse
                        {
                            Width = d * (1 + soft * 1.4), Height = d * (1 + soft * 1.4),
                            Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb((byte)(70 * soft), color.R, color.G, color.B)),
                        };
                        Canvas.SetLeft(glow, x - glow.Width / 2); Canvas.SetTop(glow, y - glow.Height / 2);
                        LightLayer.Children.Add(glow);
                    }
                    var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = d, Height = d, Fill = new SolidColorBrush(color) };
                    Canvas.SetLeft(dot, x - d / 2); Canvas.SetTop(dot, y - d / 2);
                    LightLayer.Children.Add(dot);
                }
                break;
            case RingType.Soft:
                // Feathered edges: wider, fainter bands under the core band.
                for (int k = 6; k >= 1; k--)
                    AddArc(cx, cy, radius - band / 2, start, sweep, band * (1 + soft * 1.6 * k / 6), color, 0.16 * (1 - k / 7.0) + 0.04);
                AddArc(cx, cy, radius - band / 2, start, sweep, band, color, 1);
                break;
            default: // Classic
                AddArc(cx, cy, radius - band / 2, start, sweep, band, color, 1);
                break;
        }
    }

    /// <summary>A stroked arc (or full circle) centred on the band's middle radius.</summary>
    private void AddArc(double cx, double cy, double rMid, double start, double sweep, double thickness, Windows.UI.Color color, double opacity)
    {
        if (rMid <= 0 || thickness <= 0) return;
        var stroke = new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0, 1) };
        if (sweep >= 359.9)
        {
            var ring = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = rMid * 2, Height = rMid * 2, Stroke = stroke, StrokeThickness = thickness };
            Canvas.SetLeft(ring, cx - rMid); Canvas.SetTop(ring, cy - rMid);
            LightLayer.Children.Add(ring);
            return;
        }
        var (x0, y0) = ScreenLight.OnCircle(cx, cy, rMid, start);
        var (x1, y1) = ScreenLight.OnCircle(cx, cy, rMid, start + sweep);
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(x0, y0), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(x1, y1), Size = new Windows.Foundation.Size(rMid, rMid),
            IsLargeArc = sweep > 180, SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        LightLayer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry, Stroke = stroke, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
    }

    private void LightLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lightReady && Light.On && !Light.WholeScreen) DrawInAppLight();
    }

    private void LightWholeScreen_Click(object sender, RoutedEventArgs e)
    {
        Light.WholeScreen = LightWholeScreen.IsChecked == true;
        if (Light.WholeScreen) BuildLightDisplays();
        ApplyLight();
    }

    private void CloseLightWindows()
    {
        foreach (var w in _lightWindows.Values) w.Close();
        _lightWindows.Clear();
    }

    /// <summary>
    /// DisplayArea.FindAll() returns a projected list that cannot be enumerated (LINQ/foreach throw
    /// InvalidCastException); copy it by index.
    /// </summary>
    private static List<DisplayArea> AllDisplays()
    {
        var all = DisplayArea.FindAll();
        var list = new List<DisplayArea>(all.Count);
        for (int i = 0; i < all.Count; i++) list.Add(all[i]);
        return list;
    }

    private void TurnLightOff()
    {
        CloseLightWindows();
        LightLayer.Children.Clear();
        LightLayer.Visibility = Visibility.Collapsed;
        RestoreMonitorBrightness();
    }

    private void RestoreMonitorBrightness()
    {
        if (_savedMonitorBrightness is not int original) return;
        var d = DisplayArea.GetFromWindowId(App.MainWindow!.AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
        MonitorBrightness.Set(d.X + d.Width / 2, d.Y + d.Height / 2, original);
        _savedMonitorBrightness = null;
    }

    private void RefreshMonitorBrightnessHint()
    {
        var d = DisplayArea.GetFromWindowId(App.MainWindow!.AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
        int? now = MonitorBrightness.Get(d.X + d.Width / 2, d.Y + d.Height / 2);
        ScreenBrightnessSlider.IsEnabled = ScreenBrightnessUse.IsEnabled = now is not null;
        ScreenBrightnessHint.Text = now is int v
            ? $"Monitor is at {v}% now. It goes back when the light turns off."
            : "This monitor doesn't report brightness over DDC/CI, so it can't be changed from here.";
        if (now is int cur && ScreenBrightnessUse.IsChecked != true) { _lightLoading = true; ScreenBrightnessSlider.Value = cur; _lightLoading = false; }
    }

    /// <summary>Keeps the face evenly lit: nudges light brightness from the camera's centre luma.</summary>
    private void AutoTuneTick()
    {
        if (!Light.On || !Light.AutoTune) return;
        if (PreviewSource(160, 90) is not { } src) return;
        double luma = ScreenLight.CentreLuma(src.Pixels, src.W, src.H);
        double next = ScreenLight.AutoTuneStep(Light.Brightness, luma);
        if (Math.Abs(next - Light.Brightness) < 0.001) return;
        Light.Brightness = next;
        _lightLoading = true; LightBrightness.Value = next * 100; _lightLoading = false;
        RefreshLightUi();
        if (Light.WholeScreen) foreach (var w in _lightWindows.Values) w.Apply(Light); else DrawInAppLight();
    }
}
