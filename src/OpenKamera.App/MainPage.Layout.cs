using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace OpenKamera_App;

// Responsive top bar: when the window is too narrow for every label, buttons drop to icon-only (tooltips and
// screen-reader names stay) so nothing overlaps or clips.
public sealed partial class MainPage
{
    private bool _capsuleCompact;

    private void FitCapsule()
    {
        if (CapsuleRow is null) return;
        // Measure the full-label width once per check, then decide with a little hysteresis to avoid flicker.
        SetCapsuleLabels(true);
        CapsuleRow.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        CapsuleRight.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double needed = CapsuleRow.DesiredSize.Width + CapsuleRight.DesiredSize.Width + 56, room = ActualWidth;
        bool compact = _capsuleCompact ? needed > room - 24 : needed > room;
        _capsuleCompact = compact;
        SetCapsuleLabels(!compact);
    }

    private void SetCapsuleLabels(bool show)
    {
        foreach (var child in CapsuleRow.Children.Concat(CapsuleRight.Children))
            if (child is ButtonBase b && b.Content is StackPanel sp && b != ProfileButton)
                foreach (var t in sp.Children.OfType<TextBlock>())
                    if (t.FontFamily?.Source != "Segoe UI Emoji") t.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // The profile pill keeps its name but drops the resolution detail.
        foreach (var t in ((StackPanel)ProfileButton.Content).Children.OfType<TextBlock>().Skip(1).Take(1))
            t.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
}
