using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace OpenKamera_App;

// Layout that follows the window: the top bar gives up words in steps as the window narrows (the two that matter
// most, Record and Meeting camera, keep theirs longest), the screen light panel sits beside the picture instead of
// over it, and the window can't be made smaller than everything fits in.
public sealed partial class MainPage
{
    /// <summary>0 = every label, centred. 1 = every label, from the left. 2 = only Record and Meeting camera keep words. 3 = icons only.</summary>
    private int _capsuleLevel;

    private void FitCapsule()
    {
        if (CapsuleRow is null) return;
        double room = ActualWidth - 40; // the bar's own margins and padding
        if (room <= 0) return;
        int level = 0;
        for (; level < 3; level++)
        {
            SetCapsuleLabels(level);
            CapsuleRow.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            CapsuleRight.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double row = CapsuleRow.DesiredSize.Width, right = CapsuleRight.DesiredSize.Width;
            // Centred, the right-hand group must fit beside half of the row on each side; from the left, just beside it.
            double needed = level == 0 ? row + 2 * right + 56 : row + right + 24;
            // A little slack before stepping back up, so the bar doesn't flicker while the window is dragged.
            if (needed <= room - (level < _capsuleLevel ? 24 : 0)) break;
        }
        _capsuleLevel = level;
        SetCapsuleLabels(level);
        CapsuleRow.HorizontalAlignment = level == 0 ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        FitLightPanel();
    }

    private void SetCapsuleLabels(int level)
    {
        foreach (var child in CapsuleRow.Children.Concat(CapsuleRight.Children))
            if (child is ButtonBase b && b.Content is StackPanel sp && b != ProfileButton)
            {
                bool show = level <= 1 || (level == 2 && (b == RecordBtn || b == MeetCamBtn));
                foreach (var t in sp.Children.OfType<TextBlock>())
                    if (t.FontFamily?.Source != "Segoe UI Emoji") t.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                // Without its words, Meeting camera would be a bare dot: give it a camera icon.
                if (b == MeetCamBtn) MeetCamIcon.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            }
        // The profile pill keeps its name but drops the resolution detail.
        foreach (var t in ((StackPanel)ProfileButton.Content).Children.OfType<TextBlock>().Skip(1).Take(1))
            t.Visibility = level <= 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _lightStacked;

    /// <summary>
    /// The screen light panel takes the left side, as wide as the window allows while leaving the picture at least
    /// 420 px, and stacks its three columns when it gets narrow. It used to sit in the middle, on top of the very
    /// picture it lights.
    /// </summary>
    private void FitLightPanel()
    {
        if (LightPanel is null || LightPanel.Visibility != Visibility.Visible) return;
        bool right = RightPane.Visibility == Visibility.Visible;
        double width = Math.Clamp(ActualWidth - (right ? 260 : 12) - 300 - 36, 380, 900);
        LightPanel.Width = width;
        bool stack = width < 760;
        if (stack != _lightStacked || LightColumns.RowDefinitions.Count == 0)
        {
            _lightStacked = stack;
            Restack(LightColumns, stack, 3);
            Restack(RingColumns, stack, 2);
            // Narrow: the buttons go under the title instead of pushing it out of sight.
            Grid.SetRow(LightHeaderButtons, stack ? 1 : 0);
            LightHeaderButtons.HorizontalAlignment = stack ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            LightHeaderButtons.Margin = new Thickness(0, 0, stack ? 0 : 50, 0); // beside the close button, or under it
        }
    }

    /// <summary>Lays a grid's children out side by side, or one under another.</summary>
    private static void Restack(Grid grid, bool stack, int columns)
    {
        grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
        int n = grid.Children.Count;
        for (int i = 0; i < (stack ? 1 : columns); i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (stack ? n : 1); i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < n; i++)
            if (grid.Children[i] is FrameworkElement el) { Grid.SetColumn(el, stack ? 0 : Math.Min(i, columns - 1)); Grid.SetRow(el, stack ? i : 0); }
    }

    /// <summary>The smallest window in which nothing overlaps or is cut off.</summary>
    private void SetMinimumWindowSize()
    {
        try
        {
            // The window's size is in screen pixels; the layout is in scaled units (125 %, 150 % text sizes).
            double scale = XamlRoot?.RasterizationScale ?? 1;
            if (App.MainWindow?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            { p.PreferredMinimumWidth = (int)(880 * scale); p.PreferredMinimumHeight = (int)(580 * scale); }
        }
        catch { /* an older Windows App Runtime: no minimum */ }
    }
}
