using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenKamera.Core;
using Windows.UI;

namespace OpenKamera_App;

/// <summary>
/// Turns a <see cref="SceneDocument"/> into XAML elements on a canvas. The same output is shown on the stage,
/// used for scene thumbnails, and captured into the recorded frames, so all three always match.
/// </summary>
public static class SceneRenderer
{
    public static Color ToColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var (a, r, g, b) = ColorHex.Parse(hex, (fallback.A, fallback.R, fallback.G, fallback.B));
        return Color.FromArgb(a, r, g, b);
    }

    public static Brush Brush(string? hex, Color fallback) => new SolidColorBrush(ToColor(hex, fallback));

    /// <summary>
    /// Renders every overlay layer (and the camera's outline) onto <paramref name="canvas"/>.
    /// When <paramref name="withBackground"/> is set (thumbnails) the scene background and a camera
    /// placeholder are drawn too.
    /// </summary>
    public static void Render(Canvas canvas, SceneDocument scene, double cw, double ch, bool withBackground = false, ImageSource? camera = null)
    {
        canvas.Children.Clear();
        canvas.Width = cw; canvas.Height = ch;
        if (withBackground)
        {
            canvas.Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = ToColor(scene.Background, Colors.Black), Offset = 0 },
                    new GradientStop { Color = ToColor(string.IsNullOrEmpty(scene.Background2) ? scene.Background : scene.Background2, Colors.Black), Offset = 1 },
                },
            };
            AddCameraPlaceholder(canvas, scene.Camera, cw, ch, camera);
        }
        // Transparent, never null: with no background RenderTargetBitmap captures only the layers'
        // bounding box (e.g. 992×144) instead of the whole canvas, and the overlay lands misplaced.
        else canvas.Background = new SolidColorBrush(Colors.Transparent);

        foreach (var layer in scene.Layers)
        {
            if (!layer.Visible) continue;
            var el = Create(layer, scene, cw, ch, withBackground);
            if (el is null) continue;
            el.Tag = layer.Id;
            el.Opacity = Math.Clamp(layer.Opacity, 0, 1);
            Canvas.SetLeft(el, layer.X * cw);
            Canvas.SetTop(el, layer.Y * ch);
            canvas.Children.Add(el);
        }
    }

    private static FrameworkElement? Create(SceneLayer l, SceneDocument scene, double cw, double ch, bool thumbnail)
    {
        double w = Math.Max(1, l.W * cw), h = Math.Max(1, l.H * ch);
        double stroke = l.StrokeWidth * ch, radius = l.CornerRadius * ch;
        switch (l.Kind)
        {
            case "text":
                return new Border
                {
                    Width = w, Height = h,
                    Background = string.IsNullOrEmpty(l.Fill) ? new SolidColorBrush(Colors.Transparent) : Brush(l.Fill, Colors.Transparent),
                    CornerRadius = new CornerRadius(radius > 0 ? radius : (string.IsNullOrEmpty(l.Fill) ? 0 : h * 0.15)),
                    Child = new TextBlock
                    {
                        Text = l.Text,
                        FontSize = Math.Max(1, l.FontSize * ch),
                        FontWeight = l.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = Brush(l.Color, Colors.White),
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = l.Align switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left },
                        VerticalAlignment = string.IsNullOrEmpty(l.Fill) ? VerticalAlignment.Top : VerticalAlignment.Center,
                        Margin = new Thickness(string.IsNullOrEmpty(l.Fill) ? 0 : h * 0.15, 0, string.IsNullOrEmpty(l.Fill) ? 0 : h * 0.15, 0),
                        LineHeight = l.FontSize * ch * 1.25,
                    },
                };
            case "shape" when l.Shape == "ellipse":
                return new Ellipse
                {
                    Width = w, Height = h, Fill = Brush(l.Color, Colors.White),
                    Stroke = string.IsNullOrEmpty(l.Stroke) ? null : Brush(l.Stroke, Colors.White), StrokeThickness = stroke,
                };
            case "shape":
                return new Border
                {
                    Width = w, Height = h, Background = Brush(l.Color, Colors.White),
                    BorderBrush = string.IsNullOrEmpty(l.Stroke) ? null : Brush(l.Stroke, Colors.White),
                    BorderThickness = new Thickness(stroke), CornerRadius = new CornerRadius(radius),
                };
            case "image":
                {
                    var host = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(radius), Background = new SolidColorBrush(Colors.Transparent) };
                    if (scene.ResolveAsset(l) is { } path)
                        host.Child = new Image { Source = new BitmapImage(new Uri(path)), Stretch = Stretch.Uniform };
                    else
                        host.Child = new Border
                        {
                            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), BorderThickness = new Thickness(2),
                            CornerRadius = new CornerRadius(6),
                            Child = new FontIcon { Glyph = "", FontSize = Math.Min(w, h) * 0.4, Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) },
                        };
                    return host;
                }
            case "camera":
                // Only the outline: the video itself is placed by SceneCompositor. Transparent fill keeps it clickable.
                if (l.Shape == "ellipse")
                    return new Ellipse
                    {
                        Width = w, Height = h, Fill = new SolidColorBrush(Colors.Transparent),
                        Stroke = string.IsNullOrEmpty(l.Stroke) ? null : Brush(l.Stroke, Colors.White), StrokeThickness = stroke,
                    };
                return new Border
                {
                    Width = w, Height = h, Background = new SolidColorBrush(Colors.Transparent),
                    BorderBrush = string.IsNullOrEmpty(l.Stroke) ? null : Brush(l.Stroke, Colors.White),
                    BorderThickness = new Thickness(stroke), CornerRadius = new CornerRadius(radius),
                };
        }
        return null;
    }

    /// <summary>Thumbnail stand-in for the live camera: a soft shape with a person glyph.</summary>
    private static void AddCameraPlaceholder(Canvas canvas, SceneLayer cam, double cw, double ch, ImageSource? live = null)
    {
        double w = cam.W * cw, h = cam.H * ch;
        if (live is not null)
        {
            // The real camera picture, shaped like the scene's camera layer.
            var img = new ImageBrush { ImageSource = live, Stretch = Stretch.UniformToFill };
            FrameworkElement pic = cam.Shape == "ellipse"
                ? new Ellipse { Width = w, Height = h, Fill = img }
                : new Border { Width = w, Height = h, Background = img, CornerRadius = new CornerRadius(cam.CornerRadius * ch) };
            Canvas.SetLeft(pic, cam.X * cw); Canvas.SetTop(pic, cam.Y * ch);
            canvas.Children.Add(pic);
            return;
        }
        var fill = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(1, 1),
            GradientStops = { new GradientStop { Color = Color.FromArgb(255, 74, 92, 112), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 38, 48, 60), Offset = 1 } },
        };
        FrameworkElement shape = cam.Shape == "ellipse"
            ? new Ellipse { Width = w, Height = h, Fill = fill }
            : new Border { Width = w, Height = h, Background = fill, CornerRadius = new CornerRadius(cam.CornerRadius * ch) };
        Canvas.SetLeft(shape, cam.X * cw); Canvas.SetTop(shape, cam.Y * ch);
        canvas.Children.Add(shape);
        var person = new FontIcon { Glyph = "", FontSize = Math.Min(w, h) * 0.45, Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)) };
        var host = new Grid { Width = w, Height = h, Children = { person } };
        Canvas.SetLeft(host, cam.X * cw); Canvas.SetTop(host, cam.Y * ch);
        canvas.Children.Add(host);
    }
}
