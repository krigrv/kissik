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

    /// <summary>A Windows theme colour for the theme KISSIK is showing, for the other KISSIK windows.</summary>
    internal Brush ThemeBrush(string name) => Token(name);

    /// <summary>Gives another KISSIK window the theme chosen here (its content and its title bar).</summary>
    private void ThemeWindow(Window? window)
    {
        if (window is null) return;
        try
        {
            if (window.Content is FrameworkElement root) root.RequestedTheme = RequestedTheme;
            window.AppWindow.TitleBar.PreferredTheme = RequestedTheme switch
            {
                ElementTheme.Light => Microsoft.UI.Windowing.TitleBarTheme.Light,
                ElementTheme.Dark => Microsoft.UI.Windowing.TitleBarTheme.Dark,
                _ => Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode,
            };
        }
        catch { /* an older Windows without title bar themes keeps its own */ }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t }) { _settings.Theme = t; _settings.Save(); ApplyTheme(t); }
    }

    private void ApplyTheme(string theme)
    {
        RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ThemeSystem.IsChecked = theme is not ("Light" or "Dark");
        ThemeLight.IsChecked = theme == "Light"; ThemeDark.IsChecked = theme == "Dark";
        ThemeWindow(_gallery); ThemeWindow(_recordings);
        _gallery?.ThemeChanged();
        // Code-built pieces picked their colours when they were made: rebuild them for the new theme.
        if (_audioReady) BuildMicTracks();
        BuildLookStrip();
        if (_shutter == OpenKamera.Core.ShutterMode.Off) ShutterText.Foreground = Token("DgText");
    }
}
