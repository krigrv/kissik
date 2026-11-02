using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;
using Windows.Graphics.Imaging;

namespace OpenKamera_App;

// Meeting check: measures the live picture and mic for a moment, then lists what's good and what to fix.
public sealed partial class MainPage
{
    private volatile object? _lastFrameBox;
    private (byte[] Px, int W, int H)? _lastFrame { get => _lastFrameBox as (byte[], int, int)?; set => _lastFrameBox = value; }

    private async void MeetingCheck_Click(object sender, RoutedEventArgs e)
    {
        MeetCamFlyout.Hide();
        Status("Checking your setup…");
        // Listen to the mic for a moment so a pause between words doesn't read as silence.
        double peak = 0;
        for (int i = 0; i < 15 && _audio.IsRunning; i++) { peak = Math.Max(peak, Math.Max(_audio.MasterPeakL, _audio.MasterPeakR)); await Task.Delay(100); }

        FaceBox? face = null; double? faceLuma = null, imageLuma = null;
        if (_session is not null && _lastFrame is { } f)
        {
            try
            {
                using var bmp = SoftwareBitmap.CreateCopyFromBuffer(
                    System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(f.Px), BitmapPixelFormat.Bgra8, f.W, f.H, BitmapAlphaMode.Premultiplied);
                face = await (_faceTracker ??= new FaceTracker()).DetectAsync(bmp);
            }
            catch { /* no detector: framing is reported as unknown */ }
            imageLuma = MeetingCheck.Luma(f.Px, f.W, f.H, new FaceBox(0, 0, 1, 1));
            if (face is { } fb) faceLuma = MeetingCheck.Luma(f.Px, f.W, f.H, fb);
        }

        var results = MeetingCheck.Run(new CheckInputs(
            _session is not null, _fps.Fps, face, faceLuma, imageLuma,
            _audio.IsRunning && _audio.Tracks.Count > 0, _audio.IsRunning ? peak : null,
            VirtualCamera.IsInstalled, DeviceUsage.InUseBy(PrivacyDevice.Camera).FirstOrDefault()));

        var list = new StackPanel { Spacing = 12, MinWidth = 380 };
        foreach (var r in results)
        {
            var (glyph, color) = r.Level switch
            {
                CheckLevel.Good => ("", "SystemFillColorSuccessBrush"),
                CheckLevel.Warn => ("", "SystemFillColorCautionBrush"),
                _ => ("", "SystemFillColorCriticalBrush"),
            };
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
                Foreground = Token(IsHighContrast ? "DgText" : color) });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = r.Item, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = r.Message, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 13 });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            AutomationProperties.SetName(row, $"{r.Item}: {r.Level}. {r.Message}");
            list.Children.Add(row);
        }
        int issues = results.Count(r => r.Level != CheckLevel.Good);
        Status(issues == 0 ? "Setup check: all good." : $"Setup check: {issues} thing(s) to look at.");
        await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = issues == 0 ? "You're ready" : "Almost ready",
            Content = new ScrollViewer { Content = list, MaxHeight = 460 }, CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    }
}
