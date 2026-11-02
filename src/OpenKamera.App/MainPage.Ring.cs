using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenKamera.Core;

namespace OpenKamera_App;

/// <summary>Ring-light controls: type, direction, arc, rotation, diameter, softness and position.</summary>
public sealed partial class MainPage
{
    /// <summary>Ranges set in code: compiled XAML applies Minimum before Maximum (see the kelvin slider).</summary>
    private void InitRingControls()
    {
        static void Range(Slider s, double min, double max) { s.Maximum = max; s.Minimum = min; }
        Range(RingSweep, 10, 360);
        Range(RingRotation, -180, 180);
        Range(RingDiameter, 20, 140);
        Range(RingSoftness, 0, 100);
        Range(RingX, 0, 100);
        Range(RingY, 0, 100);
        LoadRingControls();
    }

    private void LoadRingControls()
    {
        bool was = _lightLoading;
        _lightLoading = true;
        var (start, sweep) = ScreenLight.DirectionArc(Light.Direction, Light.ArcStart, Light.ArcSweep);
        RingSweep.Value = sweep;
        RingRotation.Value = NormaliseDegrees(start + sweep / 2); // rotation = where the arc is centred
        RingDiameter.Value = Light.Diameter * 100;
        RingSoftness.Value = Light.Softness * 100;
        RingX.Value = Light.CentreX * 100;
        RingY.Value = Light.CentreY * 100;
        foreach (var item in RingTypeBar.Items) item.IsSelected = item.Tag as string == Light.RingType.ToString();
        _lightLoading = was;
        RefreshRingUi();
    }

    private static double NormaliseDegrees(double d) => ((d + 180) % 360 + 360) % 360 - 180;

    private void RefreshRingUi()
    {
        RingCard.Visibility = Light.Shape == LightShape.Ring ? Visibility.Visible : Visibility.Collapsed;
        RingSweepValue.Text = $"{RingSweep.Value:0}°";
        RingRotationValue.Text = $"{RingRotation.Value:0}°";
        RingDiameterValue.Text = $"{RingDiameter.Value:0}%";
        RingSoftnessValue.Text = Light.RingType is RingType.Soft or RingType.Halo or RingType.Led ? $"{RingSoftness.Value:0}%" : "n/a for this type";
        RingSoftness.IsEnabled = Light.RingType is RingType.Soft or RingType.Halo or RingType.Led;
        foreach (var t in RingDirections.Children.OfType<ToggleButton>())
            t.IsChecked = t.Tag as string == Light.Direction.ToString();
    }

    private void RingTypeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_lightReady || _lightLoading) return;
        if (sender.SelectedItem?.Tag is string t && Enum.TryParse<RingType>(t, out var type)) Light.RingType = type;
        RefreshRingUi(); ApplyLight();
    }

    private void RingDirection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string t } || !Enum.TryParse<LightDirection>(t, out var dir)) return;
        if (dir == LightDirection.Custom)
        {
            // Keep the current arc so "Custom" starts from what is on screen.
            var (start, sweep) = ScreenLight.DirectionArc(Light.Direction, Light.ArcStart, Light.ArcSweep);
            Light.ArcStart = start; Light.ArcSweep = sweep;
        }
        Light.Direction = dir;
        LoadRingControls(); ApplyLight();
    }

    private void Ring_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_lightReady || _lightLoading) return;
        if (ReferenceEquals(sender, RingSweep) || ReferenceEquals(sender, RingRotation))
        {
            // Moving the arc or rotation makes it a custom direction.
            Light.Direction = LightDirection.Custom;
            Light.ArcSweep = RingSweep.Value;
            Light.ArcStart = RingRotation.Value - RingSweep.Value / 2;
        }
        Light.Diameter = RingDiameter.Value / 100.0;
        Light.Softness = RingSoftness.Value / 100.0;
        Light.CentreX = RingX.Value / 100.0;
        Light.CentreY = RingY.Value / 100.0;
        RefreshRingUi(); ApplyLight();
    }

    private void RingCentre_Click(object sender, RoutedEventArgs e)
    {
        Light.CentreX = Light.CentreY = 0.5;
        LoadRingControls(); ApplyLight();
    }

    private void LightMax_Click(object sender, RoutedEventArgs e)
    {
        ScreenLight.MaxBrightness(Light);
        _lightLoading = true;
        LightSize.Value = Light.Size * 100;
        ScreenBrightnessUse.IsChecked = true;
        ScreenBrightnessSlider.Value = 100;
        _lightLoading = false;
        LoadLightControls();
        LoadRingControls();
        // Fill the screen with light.
        if (App.MainWindow?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Maximize();
        Light.On = true;
        _lightLoading = true; LightOnToggle.IsOn = true; _lightLoading = false;
        RefreshLightUi();
        ApplyLight();
        Status("Max brightness: white light, big ring, monitor at 100% (if it supports DDC/CI). Turn the light off to restore the monitor.");
    }
}
