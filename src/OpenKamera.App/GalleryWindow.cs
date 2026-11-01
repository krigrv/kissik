using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;
using System.Runtime.InteropServices.WindowsRuntime;

namespace OpenKamera_App;

/// <summary>
/// The gallery: Scenes, Backgrounds and Filters, each with collections (All, Favourites, categories),
/// a thumbnail grid and per-item actions.
/// </summary>
public sealed class GalleryWindow : Window
{
    private const int ThumbW = 320, ThumbH = 180;
    private readonly MainPage _main;
    private readonly SelectorBar _tabs = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ListView _collections = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly GridView _grid = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, Padding = new Thickness(16, 0, 16, 16) };
    private readonly TextBlock _heading = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _empty = new() { Margin = new Thickness(24), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private string _tab = "scenes";
    private string _collection = "";

    public GalleryWindow(MainPage main)
    {
        _main = main;
        Title = "OpenKamera Gallery";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));

        foreach (var (id, text) in new[] { ("scenes", "Scenes"), ("backgrounds", "Backgrounds"), ("filters", "Filters") })
            _tabs.Items.Add(new SelectorBarItem { Text = text, Tag = id });
        _tabs.SelectionChanged += (s, _) => { if (s.SelectedItem?.Tag is string t && t != _tab) { _tab = t; _collection = ""; Rebuild(); } };
        _collections.SelectionChanged += (_, _) =>
        {
            if (_collections.SelectedItem is ListViewItem { Tag: string c } && c != _collection) { _collection = c; RebuildGrid(); }
        };
        _grid.ItemClick += (_, e) => { if (e.ClickedItem is FrameworkElement { Tag: Action use }) use(); };
        AutomationProperties.SetName(_grid, "Gallery items");

        var left = new Grid { Width = 220, Padding = new Thickness(8, 12, 8, 12), Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition());
        left.Children.Add(new TextBlock { Text = "Collections", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 8) });
        Grid.SetRow(_collections, 1);
        left.Children.Add(_collections);

        var header = new Grid { Padding = new Thickness(16, 12, 16, 12) };
        header.Children.Add(_heading);
        header.Children.Add(_actions);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        right.Children.Add(header);
        var body = new Grid();
        body.Children.Add(_grid);
        body.Children.Add(_empty);
        Grid.SetRow(body, 1);
        right.Children.Add(body);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.Children.Add(left);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(new Border { Padding = new Thickness(0, 8, 0, 4), Child = _tabs });
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);
        Content = root;
    }

    public void Show(string tab)
    {
        _tab = tab;
        foreach (var item in _tabs.Items) item.IsSelected = item.Tag as string == tab;
        Rebuild();
        Activate();
    }

    private void Rebuild()
    {
        var cats = _tab switch
        {
            "scenes" => new[] { "Professional", "Social", "Streaming", "Utility" },
            "backgrounds" => new[] { "Studio", "Abstract", "Imported" },
            _ => new[] { "Everyday", "Cinematic", "LUTs" },
        };
        _collections.Items.Clear();
        AddCollection("All", "", "");
        AddCollection("Favourites", "*fav", "");
        _collections.Items.Add(new ListViewItem { Content = new TextBlock { Text = "Explore", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) }, IsEnabled = false });
        foreach (var c in cats) AddCollection(c, c, "");
        _collections.SelectedIndex = 0;

        _actions.Children.Clear();
        switch (_tab)
        {
            case "scenes":
                _actions.Children.Add(ActionButton("", "Create new", () =>
                {
                    var s = SceneLibrary.CreateBlank();
                    _main.ScenesChanged(s.Id);
                    RebuildGrid();
                }));
                _actions.Children.Add(ActionButton("", "Import", async () =>
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    picker.FileTypeFilter.Add(".okscene");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    if (await picker.PickSingleFileAsync() is not { } f) return;
                    try { var s = SceneLibrary.Import(f.Path); _main.ScenesChanged(s.Id); RebuildGrid(); }
                    catch (Exception ex) { await Message("Import failed", ex.Message); }
                }));
                break;
            case "backgrounds":
                _actions.Children.Add(ActionButton("", "Add a background", async () =>
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    foreach (var ext in new[] { ".png", ".jpg", ".jpeg" }) picker.FileTypeFilter.Add(ext);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    if (await picker.PickSingleFileAsync() is not { } f) return;
                    BackgroundLibrary.Import(f.Path);
                    _main.BackgroundsChanged();
                    RebuildGrid();
                }));
                break;
            default:
                _actions.Children.Add(ActionButton("", "Add a LUT", async () => { await _main.ImportLutAsync(); RebuildGrid(); }));
                _actions.Children.Add(ActionButton("", "Refresh the previews", RebuildGrid));
                _actions.Children.Add(ActionButton("", "Remove the current filter", () => { _main.UseFilter(FilterPreset.None); _main.UseLut(null); }));
                break;
        }
        RebuildGrid();
    }

    private void AddCollection(string text, string tag, string glyph) =>
        _collections.Items.Add(new ListViewItem
        {
            Tag = tag,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text } } },
        });

    private static Button ActionButton(string glyph, string text, Action click)
    {
        var b = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = glyph, FontSize = 13 }, new TextBlock { Text = text } } } };
        b.Click += (_, _) => click();
        return b;
    }

    private bool IsFav(string key) => _main.Settings.Favourites.Contains(key);

    private void ToggleFav(string key)
    {
        if (!_main.Settings.Favourites.Add(key)) _main.Settings.Favourites.Remove(key);
        _main.SaveFavourites();
        RebuildGrid();
    }

    private void RebuildGrid()
    {
        _grid.Items.Clear();
        _heading.Text = _collection switch { "" => "All", "*fav" => "Favourites", var c => c };
        switch (_tab)
        {
            case "scenes": FillScenes(); break;
            case "backgrounds": FillBackgrounds(); break;
            default: FillFilters(); break;
        }
        _empty.Visibility = _grid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _collection == "*fav"
            ? "No favourites yet. Use the … menu on any item and choose Add to favourites."
            : "Nothing here yet.";
    }

    private bool InCollection(string key, string category) =>
        _collection == "" || (_collection == "*fav" ? IsFav(key) : category == _collection);

    private void FillScenes()
    {
        foreach (var s in SceneLibrary.All())
        {
            string key = "scene:" + s.Id;
            if (!InCollection(key, s.Category)) continue;
            var canvas = new Canvas();
            SceneRenderer.Render(canvas, s, ThumbW, ThumbH, withBackground: true);
            var menu = new MenuFlyout();
            menu.Items.Add(Item("Use this scene", "", () => _main.UseScene(s.Id)));
            menu.Items.Add(Item(IsFav(key) ? "Remove from favourites" : "Add to favourites", "", () => ToggleFav(key)));
            menu.Items.Add(Item("Duplicate", "", () => { var c = SceneLibrary.Duplicate(s); _main.ScenesChanged(c.Id); RebuildGrid(); }));
            menu.Items.Add(Item("Export…", "", async () =>
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = s.Name };
                picker.FileTypeChoices.Add("OpenKamera scene", [".okscene"]);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                if (await picker.PickSaveFileAsync() is { } f) SceneLibrary.Export(s, f.Path);
            }));
            if (!s.IsTemplate || SceneLibrary.HasEdits(s.Id))
                menu.Items.Add(Item(s.IsTemplate ? "Reset to original" : "Delete", "", () =>
                {
                    SceneLibrary.ResetOrDelete(s.Id);
                    _main.ScenesChanged(s.IsTemplate ? s.Id : "blank");
                    RebuildGrid();
                }));
            _grid.Items.Add(Card(new Viewbox { Child = canvas }, s.Name, s.Id == _main.CurrentSceneId, menu, () => _main.UseScene(s.Id)));
        }
    }

    private void FillBackgrounds()
    {
        foreach (var b in BackgroundLibrary.All())
        {
            string key = "bg:" + b.Id;
            if (!InCollection(key, b.Category)) continue;
            var thumb = Bitmap(BackgroundLibrary.Render(b.Id, ThumbW, ThumbH));
            var menu = new MenuFlyout();
            menu.Items.Add(Item("Use as background", "", () => _main.UseBackground(b.Id)));
            menu.Items.Add(Item(IsFav(key) ? "Remove from favourites" : "Add to favourites", "", () => ToggleFav(key)));
            if (b.FilePath is { } path)
                menu.Items.Add(Item("Delete", "", () => { File.Delete(path); _main.BackgroundsChanged(); RebuildGrid(); }));
            _grid.Items.Add(Card(thumb, b.Name, b.Id == _main.CurrentBackgroundId, menu, () => _main.UseBackground(b.Id)));
        }
    }

    private void FillFilters()
    {
        var src = _main.PreviewSource(ThumbW, ThumbH);
        foreach (var f in Enum.GetValues<FilterPreset>())
        {
            string key = "filter:" + f;
            string cat = f is FilterPreset.Cinematic or FilterPreset.Bleach or FilterPreset.Fade or FilterPreset.BlackAndWhite ? "Cinematic" : "Everyday";
            if (!InCollection(key, cat)) continue;
            var menu = new MenuFlyout();
            menu.Items.Add(Item("Use this filter", "", () => _main.UseFilter(f)));
            menu.Items.Add(Item(IsFav(key) ? "Remove from favourites" : "Add to favourites", "", () => ToggleFav(key)));
            string name = f switch { FilterPreset.None => "No filter", FilterPreset.BlackAndWhite => "Black & white", FilterPreset.Bleach => "Bleach bypass", _ => f.ToString() };
            _grid.Items.Add(Card(Preview(src, new ImageFilters.Adjustments(Filter: f)), name, f == _main.CurrentFilter, menu, () => _main.UseFilter(f)));
        }
        foreach (var lut in _main.Luts)
        {
            string key = "lut:" + lut.Name;
            if (!InCollection(key, "LUTs")) continue;
            var menu = new MenuFlyout();
            menu.Items.Add(Item("Use this LUT", "", () => _main.UseLut(lut.Name)));
            menu.Items.Add(Item(IsFav(key) ? "Remove from favourites" : "Add to favourites", "", () => ToggleFav(key)));
            _grid.Items.Add(Card(Preview(src, new ImageFilters.Adjustments(Lut: lut)), lut.Name, lut.Name == _main.CurrentLutName, menu, () => _main.UseLut(lut.Name)));
        }
    }

    /// <summary>The live camera through a look; a neutral gradient when no camera is connected.</summary>
    private static UIElement Preview((byte[] Pixels, int W, int H)? src, ImageFilters.Adjustments adj)
    {
        byte[] px;
        int w = ThumbW, h = ThumbH;
        if (src is { } s) { px = (byte[])s.Pixels.Clone(); w = s.W; h = s.H; }
        else
        {
            px = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(120 + 80 * x / w); px[i + 1] = (byte)(90 + 90 * y / h); px[i + 2] = (byte)(200 - 80 * x / w); px[i + 3] = 255;
            }
        }
        ImageFilters.Apply(px, w, h, adj);
        return Bitmap(px, w, h);
    }

    private static UIElement Bitmap(byte[]? px, int w = ThumbW, int h = ThumbH)
    {
        if (px is null) return new FontIcon { Glyph = "", FontSize = 32 };
        var wb = new WriteableBitmap(w, h);
        using (var stream = wb.PixelBuffer.AsStream()) stream.Write(px, 0, Math.Min(px.Length, (int)stream.Length));
        wb.Invalidate();
        return new Image { Source = wb, Stretch = Stretch.UniformToFill };
    }

    private FrameworkElement Card(UIElement thumb, string name, bool current, MenuFlyout menu, Action use)
    {
        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Flyout = menu };
        AutomationProperties.SetName(more, $"More actions for {name}");
        var label = new Grid { Margin = new Thickness(2, 6, 0, 0) };
        label.ColumnDefinitions.Add(new ColumnDefinition());
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.Children.Add(new TextBlock
        {
            Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = current ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        });
        Grid.SetColumn(more, 1);
        label.Children.Add(more);
        var card = new StackPanel
        {
            Width = 260, Tag = use,
            Children =
            {
                new Border
                {
                    Height = 146, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 21, 26, 32)),
                    BorderThickness = new Thickness(current ? 2 : 0), BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                    Child = thumb,
                },
                label,
            },
        };
        AutomationProperties.SetName(card, current ? $"{name} (in use)" : name);
        return card;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => click();
        return item;
    }

    private async Task Message(string title, string text) =>
        await new ContentDialog { Title = title, Content = text, CloseButtonText = "OK", XamlRoot = Content.XamlRoot }.ShowAsync();
}
