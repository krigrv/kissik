using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenKamera_App;

// Pure Windows look: every KISSIK colour is one of Windows' own theme colours, in light, dark and high contrast.
public sealed partial class MainPage
{
    /// <summary>KISSIK token → the Windows theme brush it stands for.</summary>
    private static readonly Dictionary<string, string> SystemBrush = new()
    {
        ["DgAccentBrush"] = "AccentTextFillColorPrimaryBrush",
        ["DgText"] = "TextFillColorPrimaryBrush",
        ["DgDim"] = "TextFillColorSecondaryBrush",
        ["DgEdge"] = "CardStrokeColorDefaultBrush",
        ["DgWell"] = "ControlFillColorDefaultBrush",
        ["DgWellOn"] = "ControlFillColorSecondaryBrush",
        ["DgGlass"] = "SolidBackgroundFillColorQuarternaryBrush",
        ["DgPage"] = "SolidBackgroundFillColorBaseBrush",
    };

    /// <summary>A colour for code-built UI, from Windows' theme for the theme this page is showing.</summary>
    private Brush Token(string key)
    {
        string name = SystemBrush.TryGetValue(key, out var k) ? k : key;
        string theme = IsHighContrast ? "HighContrast" : ActualTheme == ElementTheme.Light ? "Light" : "Default";
        var app = Application.Current.Resources;
        foreach (var dict in app.MergedDictionaries.Prepend(app))
            if (dict.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary rd && rd.TryGetValue(name, out var v) && v is Brush b)
                return b;
        return app.TryGetValue(name, out var any) && any is Brush fallback ? fallback : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private void InitTheme() => ApplyTheme(_settings.Theme);

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t }) { _settings.Theme = t; _settings.Save(); ApplyTheme(t); }
    }

    private void ApplyTheme(string theme)
    {
        RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ThemeSystem.IsChecked = theme is not ("Light" or "Dark");
        ThemeLight.IsChecked = theme == "Light"; ThemeDark.IsChecked = theme == "Dark";
        // Code-built pieces picked their colours when they were made: rebuild them for the new theme.
        if (_audioReady) BuildMicTracks();
        BuildLookStrip();
        if (_shutter == OpenKamera.Core.ShutterMode.Off) ShutterText.Foreground = Token("DgText");
    }
}
