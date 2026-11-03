using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;
using System.Runtime.InteropServices.WindowsRuntime;

namespace OpenKamera_App;

/// <summary>The "Look" strip: small live previews of the camera through each filter, plus "+ LUT".</summary>
public sealed partial class MainPage
{
    private const int LookSize = 100;
    private readonly List<(FilterPreset Filter, Border Frame, Image Thumb, TextBlock Label)> _lookTiles = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lookTimer;

    private void BuildLookStrip()
    {
        LookStrip.Children.Clear();
        _lookTiles.Clear();
        foreach (var f in new[] { FilterPreset.None, FilterPreset.Cinematic, FilterPreset.Warm, FilterPreset.Cool,
                                  FilterPreset.BlackAndWhite, FilterPreset.Vivid, FilterPreset.Bleach, FilterPreset.Fade })
        {
            var thumb = new Image { Stretch = Stretch.UniformToFill };
            var frame = new Border
            {
                Width = 50, Height = 50, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x12, 255, 255, 255)), Child = thumb,
            };
            var label = new TextBlock { Text = LookName(f), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var btn = new Button
            {
                Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
                Content = new StackPanel { Width = 56, Spacing = 4, Children = { frame, label } },
            };
            AutomationProperties.SetName(btn, $"Look: {LookName(f)}");
            ToolTipService.SetToolTip(btn, LookName(f));
            btn.Click += (_, _) => { FilterBox.SelectedIndex = (int)f; SyncLookTiles(); };
            LookStrip.Children.Add(btn);
            _lookTiles.Add((f, frame, thumb, label));
        }
        var add = new Button
        {
            Width = 50, Height = 50, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x12, 255, 255, 255)), BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 16 },
        };
        AutomationProperties.SetName(add, "Add a LUT");
        ToolTipService.SetToolTip(add, "Import a .cube LUT");
        add.Click += async (_, _) => await ImportLutCoreAsync();
        LookStrip.Children.Add(new StackPanel { Spacing = 4, Children = { add, new TextBlock { Text = "LUT", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } } });

        _lookTimer = DispatcherQueue.CreateTimer();
        _lookTimer.Interval = TimeSpan.FromSeconds(ReduceMotion ? 10 : 2); // reduce motion: previews barely move
        _lookTimer.Tick += (_, _) => RefreshLookThumbs();
        _lookTimer.Start();
        RefreshLookThumbs();
        SyncLookTiles();
    }

    private static string LookName(FilterPreset f) => f switch
    {
        FilterPreset.None => "None", FilterPreset.BlackAndWhite => "B&W", FilterPreset.Bleach => "Bleach", _ => f.ToString(),
    };

    /// <summary>Re-renders the previews from the live camera (cheap: 100×100 per look, every 2 s, only when the panel is visible).</summary>
    private void RefreshLookThumbs()
    {
        if (CameraPanel.Visibility != Visibility.Visible || LeftPane.Visibility != Visibility.Visible) return;
        var src = PreviewSource(LookSize, LookSize);
        foreach (var (f, _, thumb, _) in _lookTiles)
        {
            byte[] px;
            if (src is { } s) px = (byte[])s.Pixels.Clone();
            else
            {
                px = new byte[LookSize * LookSize * 4];
                for (int y = 0; y < LookSize; y++) for (int x = 0; x < LookSize; x++)
                {
                    int i = (y * LookSize + x) * 4;
                    px[i] = (byte)(150 + x); px[i + 1] = (byte)(110 + y / 2); px[i + 2] = (byte)(200 - x); px[i + 3] = 255;
                }
            }
            ImageFilters.Apply(px, LookSize, LookSize, new ImageFilters.Adjustments(Filter: f));
            var wb = new WriteableBitmap(LookSize, LookSize);
            using (var stream = wb.PixelBuffer.AsStream()) stream.Write(px, 0, px.Length);
            wb.Invalidate();
            thumb.Source = wb;
        }
    }

    /// <summary>Saves the app window (not the desktop) as a PNG — handy for bug reports and docs.</summary>
    private async void SaveWindowPicture_Click(object sender, RoutedEventArgs e)
    {
        // The menu that asked for this hands focus back to its button; hide that focus outline for the picture.
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as Control;
        bool visuals = focused?.UseSystemFocusVisuals ?? false;
        try
        {
            if (focused is not null) focused.UseSystemFocusVisuals = false;
            await Task.Delay(250); // let the menu finish closing
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(Root);
            if (focused is not null) focused.UseSystemFocusVisuals = visuals;
            var px = (await rtb.GetPixelsAsync()).ToArray();
            string folder = AppPaths.Pictures;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"KISSIK-window-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var file = await (await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder)).CreateFileAsync(Path.GetFileName(path));
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, px);
            await enc.FlushAsync();
            Status("Window picture saved: " + file.Path);
        }
        catch (Exception ex) { Status("Window picture failed: " + ex.Message); }
        finally { if (focused is not null) focused.UseSystemFocusVisuals = visuals; }
    }

    private void SyncLookTiles()
    {
        if (_lookTiles.Count == 0) return;
        var current = (FilterPreset)Math.Max(0, FilterBox.SelectedIndex);
        var accent = Token("DgAccentBrush");
        foreach (var (f, frame, _, label) in _lookTiles)
        {
            frame.BorderBrush = f == current ? accent : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            label.Foreground = f == current ? Token("DgText") : Token("DgDim");
        }
        string look = LookName(current);
        if (LutBox.SelectedItem is string lut) look = current == FilterPreset.None ? lut : $"{look} + {lut}";
        LookValue.Text = look;
    }
}
