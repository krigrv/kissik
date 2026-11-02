using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Privacy shutter: hides you from the meeting camera and recordings; your own preview stays normal.
public sealed partial class MainPage
{
    private volatile ShutterMode _shutter = ShutterMode.Off;

    private void Shutter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag } && Enum.TryParse<ShutterMode>(tag, out var m)) SetShutter(m);
    }

    private void SetShutter(ShutterMode mode)
    {
        _shutter = mode;
        _audio.MuteAll = mode == ShutterMode.Away;
        _audio.ApplyMix();
        (ShutterIcon.Glyph, ShutterText.Text) = mode switch
        {
            ShutterMode.Blur => ("", "Blurred"),
            ShutterMode.Away => ("", "Away"),
            _ => ("", "Visible"),
        };
        ShutterText.Foreground = mode == ShutterMode.Off ? Token("DgText")
            : Token("SystemFillColorCriticalBrush");
        Status(mode switch
        {
            ShutterMode.Blur => "Meeting and recording see a blurred picture. Your preview is unchanged.",
            ShutterMode.Away => "Meeting and recording show “be right back” and your mic mix is muted. Press Ctrl+Alt+H (from any app) to come back.",
            _ => "You're visible again.",
        });
    }
}
