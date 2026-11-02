using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenKamera_App;

// One short welcome on first run: what KISSIK is, three steps, the free promise. Nothing else pops up.
public sealed partial class MainPage
{
    private async Task ShowWelcomeAndModeAsync() { await ShowWelcomeOnceAsync(); await AskUiModeAsync(); }

    private async Task ShowWelcomeOnceAsync()
    {
        if (_settings.SeenWelcome) return;
        _settings.SeenWelcome = true; _settings.Save();
        var body = new StackPanel { Spacing = 12, MaxWidth = 420 };
        body.Children.Add(new TextBlock { Text = "Your camera, looking its best — in any meeting app.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        foreach (var (n, line) in new[]
        {
            ("1", "Pick your camera under Source (or your phone: choose Phone (Wi-Fi) and scan the code)."),
            ("2", "Choose a background, a look, and a scene at the bottom."),
            ("3", "In Zoom, Teams or Meet, choose “KISSIK” as the camera. Install it from the Meeting camera menu."),
        })
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = n, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
            var t = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(t, 1); row.Children.Add(t);
            body.Children.Add(row);
        }
        body.Children.Add(new TextBlock { Text = "Every resolution is free, with no watermark, no ads and no account.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 });
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Welcome to KISSIK", Content = body,
            PrimaryButtonText = "Get started", DefaultButton = ContentDialogButton.Primary,
        };
        try { await dlg.ShowAsync(); } catch { /* another dialog was open */ }
    }
}
