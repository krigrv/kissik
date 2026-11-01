using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

/// <summary>What the gallery window can ask of the studio.</summary>
public sealed partial class MainPage
{
    public static MainPage? Current { get; private set; }
    private GalleryWindow? _gallery;

    internal AppSettings Settings => _settings;
    internal string CurrentSceneId => _scene.Id;
    internal IReadOnlyList<Lut3D> Luts => _luts;
    internal FilterPreset CurrentFilter => _adj.Filter;
    internal string? CurrentLutName => _adj.Lut?.Name;
    internal string? CurrentBackgroundId => _bg.Mode == BackgroundMode.Replace ? _bg.BackgroundId : null;

    private void GalleryBtn_Click(object sender, RoutedEventArgs e) => OpenGallery();

    internal void OpenGallery(string tab = "scenes")
    {
        if (_gallery is null)
        {
            _gallery = new GalleryWindow(this);
            _gallery.Closed += (_, _) => _gallery = null;
        }
        _gallery.Show(tab);
    }

    internal void UseScene(string id) => RefreshSceneRail(id);

    internal void ScenesChanged(string? select = null) => RefreshSceneRail(select ?? _scene.Id);

    internal void UseBackground(string id)
    {
        RefreshBackgroundPicker(id);
        BgMode.SelectedItem = BgReplace;
        UpdateBackground();
        Status($"Background: {BackgroundLibrary.All().FirstOrDefault(b => b.Id == id)?.Name ?? id}");
    }

    internal void BackgroundsChanged() => RefreshBackgroundPicker((BgPicker.SelectedItem as BackgroundInfo)?.Id);

    internal void UseFilter(FilterPreset f)
    {
        FilterBox.SelectedIndex = (int)f;
        Status($"Filter: {FilterBox.SelectedItem}");
    }

    internal void UseLut(string? name)
    {
        LutBox.SelectedItem = name;
        Status(name is null ? "LUT removed" : $"LUT: {name}");
    }

    internal Task<Lut3D?> ImportLutAsync() => ImportLutCoreAsync();

    internal void SaveFavourites() => _settings.Save();

    /// <summary>A small, unfiltered, unscened copy of the live camera for filter previews (null when not connected).</summary>
    internal (byte[] Pixels, int W, int H)? PreviewSource(int w, int h)
    {
        if (_session?.Latest is not { } raw) return null;
        try
        {
            var recipe = new FrameRecipe(ImageFilters.Adjustments.Neutral, _transform, BackgroundSettings.Off, w, h);
            return FrameProcessor.ProcessPixels(raw, recipe, null);
        }
        catch { return null; }
    }
}
