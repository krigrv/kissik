using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;
using System.Runtime.InteropServices.WindowsRuntime;

namespace OpenKamera_App;

/// <summary>A captured image of the scene's overlay layers at the output size.</summary>
public sealed record OverlayImage(byte[] Pixels, int Width, int Height);

/// <summary>Scenes: rail, live overlay, editor (select/drag/resize/properties/undo) and scene actions.</summary>
public sealed partial class MainPage
{
    private List<SceneDocument> _scenes = [];
    private SceneDocument _scene = SceneDocument.Templates[0].Clone();
    private volatile SceneDocument? _sceneSnapshot;   // read on the frame thread
    private volatile OverlayImage? _overlay;          // read on the frame thread
    private string? _selectedLayerId;
    private readonly List<string> _undo = [], _redo = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _captureTimer;
    private bool _loadingScene;
    /// <summary>Diagnostics for the last overlay capture (also exposed as the overlay's UI Automation help text).</summary>
    internal string LastOverlayCapture { get; private set; } = "";

    // ── Rail ────────────────────────────────────────────────────────────

    private void InitScenes()
    {
        _captureTimer = DispatcherQueue.CreateTimer();
        _captureTimer.Interval = TimeSpan.FromMilliseconds(120);
        _captureTimer.IsRepeating = false;
        _captureTimer.Tick += async (_, _) => await CaptureOverlayAsync();
        RefreshSceneRail(_settings.SceneId);
    }

    private void RefreshSceneRail(string? selectId)
    {
        _scenes = SceneLibrary.All();
        for (int i = 0; i < _scenes.Count; i++) _scenes[i].Shortcut = i + 1;
        string tag = (SceneCategory.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        IEnumerable<SceneDocument> shown = tag switch
        {
            "" => _scenes,
            "*fav" => _scenes.Where(s => _settings.Favourites.Contains("scene:" + s.Id)),
            _ => _scenes.Where(s => s.Category == tag),
        };
        string q = SceneSearch?.Text.Trim() ?? "";
        if (q.Length > 0)
            shown = _scenes.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        _loadingScene = true;
        SceneList.ItemsSource = shown.ToList();
        _loadingScene = false;
        var target = _scenes.FirstOrDefault(s => s.Id == selectId) ?? _scenes[0];
        if (SceneList.Items.Contains(target)) SceneList.SelectedItem = target;
        else LoadScene(target);
    }

    private void SceneSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshSceneRail(_scene.Id);

    private void SelectSceneByShortcut(int n)
    {
        var s = _scenes.FirstOrDefault(x => x.Shortcut == n);
        if (s is null) return;
        if (SceneList.Items.Contains(s)) SceneList.SelectedItem = s; else LoadScene(s);
    }

    private void SceneCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SceneList is null || _scenes.Count == 0) return;
        RefreshSceneRail(_scene.Id);
    }

    private void SceneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingScene || SceneList.SelectedItem is not SceneDocument s) return;
        LoadScene(s);
    }

    /// <summary>Renders a WYSIWYG thumbnail into each rail card.</summary>
    private void SceneList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not SceneDocument s || args.ItemContainer.ContentTemplateRoot is not FrameworkElement root) return;
        if (FindChild<Canvas>(root) is { } canvas) SceneRenderer.Render(canvas, s, 320, 180, withBackground: true, camera: _session is not null && _lastFrame is not null ? _thumbSource : null);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } deeper) return deeper;
        }
        return null;
    }

    private void LoadScene(SceneDocument s)
    {
        BeginSceneFade();
        _ = EnsureSecondCameraAsync().ContinueWith(_ => { }, TaskScheduler.Default);
        _scene = (SceneLibrary.Load(s.Id) ?? s).Clone();
        _settings.SceneId = _scene.Id; _settings.Save();
        _undo.Clear(); _redo.Clear();
        _selectedLayerId = null;
        RenderScene();
        RefreshLayersTab();
        Status($"Scene: {_scene.Name}");
    }

    // ── Rendering + capture ─────────────────────────────────────────────

    /// <summary>Redraws the stage overlay from the scene and schedules a capture for recordings.</summary>
    private void RenderScene()
    {
        if (OverlayCanvas is null) return;
        SceneRenderer.Render(OverlayCanvas, _scene, Stage.Width, Stage.Height);
        _sceneSnapshot = _scene.Clone();
        UpdateAdorner();
        _captureTimer?.Stop(); _captureTimer?.Start();
    }

    private volatile byte[]? _overlayBehind;

    /// <summary>Renders the overlay canvas with the elements matching <paramref name="hide"/> hidden, at output size.</summary>
    private async Task<byte[]> CaptureLayersAsync(Func<FrameworkElement, bool> hide, OutputProfile p)
    {
        var hidden = OverlayCanvas.Children.OfType<FrameworkElement>().Where(hide).ToList();
        var saved = hidden.Select(e => e.Opacity).ToList();
        foreach (var e in hidden) e.Opacity = 0;
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(OverlayCanvas);
            var pixels = (await rtb.GetPixelsAsync()).ToArray();
            LastOverlayCapture = $"rtb {rtb.PixelWidth}×{rtb.PixelHeight}, {pixels.Length} bytes, canvas {OverlayCanvas.ActualWidth:0}×{OverlayCanvas.ActualHeight:0}, output {p.Width}×{p.Height}";
            if (rtb.PixelWidth != p.Width || rtb.PixelHeight != p.Height)
                (pixels, _, _) = OutputFit.Stretch(pixels, rtb.PixelWidth, rtb.PixelHeight, p.Width, p.Height);
            return pixels;
        }
        finally { for (int i = 0; i < hidden.Count; i++) hidden[i].Opacity = saved[i]; }
    }

    /// <summary>Captures the overlay layers at exactly the output size (premultiplied BGRA).</summary>
    private async Task CaptureOverlayAsync()
    {
        try
        {
            var p = _profile;
            if (!_scene.Overlays.Any(l => l.Visible) && !(_scene.Camera.Visible && !string.IsNullOrEmpty(_scene.Camera.Stroke)))
            { _overlay = null; _overlayBehind = null; return; }
            // Render at the canvas's natural size (its pixel size includes display scaling), then resample
            // to the output. Asking RenderTargetBitmap for the output size directly mixes in the DPI factor
            // and the overlay comes out oversized.
            // Layers dragged past the edge would otherwise grow the capture beyond the canvas.
            OverlayCanvas.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            { Rect = new Windows.Foundation.Rect(0, 0, OverlayCanvas.ActualWidth, OverlayCanvas.ActualHeight) };
            var behindIds = _scene.Layers.Where(l => l.Visible && l.Behind).Select(l => l.Id).ToHashSet();
            // Front layers go over the finished picture; "behind me" layers are blended under the person.
            var pixels = await CaptureLayersAsync(el => el.Tag is string id && behindIds.Contains(id), p);
            _overlayBehind = behindIds.Count > 0 ? await CaptureLayersAsync(el => !(el.Tag is string id && behindIds.Contains(id)), p) : null;
            _overlay = new OverlayImage(pixels, p.Width, p.Height);
            // In the preview the behind layers are drawn by the video itself, so hide their live copies.
            foreach (var el in OverlayCanvas.Children.OfType<FrameworkElement>())
                if (el.Tag is string id && behindIds.Contains(id)) el.Opacity = 0;
        }
        catch (Exception ex) { Status("Scene capture failed: " + ex.Message); }
    }

    // ── Editing core: snapshot → mutate → commit ───────────────────────

    private void BeginEdit()
    {
        _undo.Add(_scene.ToJson());
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Saves the scene, redraws stage, list, properties and the rail thumbnail.</summary>
    private void Commit(bool rebuildProps = true)
    {
        SceneLibrary.Save(_scene);
        RenderScene();
        RefreshLayerList();
        if (rebuildProps) BuildProps();
        UndoBtn.IsEnabled = _undo.Count > 0; RedoBtn.IsEnabled = _redo.Count > 0;
        RefreshRailItem();
    }

    private void RefreshRailItem()
    {
        int i = _scenes.FindIndex(s => s.Id == _scene.Id);
        if (i >= 0) { var copy = _scene.Clone(); copy.Shortcut = _scenes[i].Shortcut; _scenes[i] = copy; }
        if (SceneList.ContainerFromItem(SceneList.SelectedItem) is ListViewItem item && FindChild<Canvas>(item) is { } c)
            SceneRenderer.Render(c, _scene, 320, 180, withBackground: true);
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_scene.ToJson());
        RestoreJson(_undo[^1]); _undo.RemoveAt(_undo.Count - 1);
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_scene.ToJson());
        RestoreJson(_redo[^1]); _redo.RemoveAt(_redo.Count - 1);
    }

    private void RestoreJson(string json)
    {
        string? folder = _scene.Folder;
        _scene = SceneDocument.FromJson(json);
        _scene.Folder = folder;
        if (_scene.Layers.All(l => l.Id != _selectedLayerId)) _selectedLayerId = null;
        Commit();
        RefreshLayersTab();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private SceneLayer? Selected => _scene.Layers.FirstOrDefault(l => l.Id == _selectedLayerId);

    // ── Layers tab ──────────────────────────────────────────────────────

    private void RefreshLayersTab()
    {
        SceneNameBox.Text = _scene.Name;
        bool template = _scene.IsTemplate;
        SceneResetItem.Text = template ? "Reset to original" : "Delete scene";
        SceneResetItem.IsEnabled = !template || SceneLibrary.HasEdits(_scene.Id);
        SetColorButton(SceneBg1Btn, _scene.Background, hex => { BeginEdit(); _scene.Background = hex; Commit(); });
        SetColorButton(SceneBg2Btn, string.IsNullOrEmpty(_scene.Background2) ? _scene.Background : _scene.Background2,
            hex => { BeginEdit(); _scene.Background2 = hex; Commit(); });
        AddCameraItem.IsEnabled = _scene.Layers.All(l => l.Kind != "camera");
        UndoBtn.IsEnabled = _undo.Count > 0; RedoBtn.IsEnabled = _redo.Count > 0;
        RefreshLayerList();
        BuildProps();
    }

    /// <summary>Top layer first, like a design tool.</summary>
    private void RefreshLayerList()
    {
        _loadingScene = true;
        var items = Enumerable.Reverse(_scene.Layers).ToList();
        LayerList.ItemsSource = items;
        LayerList.SelectedItem = items.FirstOrDefault(l => l.Id == _selectedLayerId);
        _loadingScene = false;
    }

    private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingScene) return;
        _selectedLayerId = (LayerList.SelectedItem as SceneLayer)?.Id;
        UpdateAdorner();
        BuildProps();
    }

    private void LayerEye_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || _scene.Layers.FirstOrDefault(l => l.Id == id) is not { } layer) return;
        BeginEdit();
        layer.Visible = !layer.Visible;
        Commit();
    }

    private void SceneNameBox_LostFocus(object sender, RoutedEventArgs e) => RenameScene();
    private void SceneNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { RenameScene(); e.Handled = true; }
    }

    private void RenameScene()
    {
        string name = SceneNameBox.Text.Trim();
        if (name.Length == 0 || name == _scene.Name) { SceneNameBox.Text = _scene.Name; return; }
        BeginEdit();
        _scene.Name = name;
        Commit(rebuildProps: false);
        RefreshSceneRail(_scene.Id);
    }

    private async void AddLayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string kind }) return;
        if (kind == "camera2") { await AddSecondCameraAsync(); return; }
        string? galleryPick = null;
        if (kind == "gallery") { galleryPick = await PickGalleryBackgroundAsync(); if (galleryPick is null) return; kind = "bg"; }
        SceneLayer layer = kind switch
        {
            "text" => new SceneLayer { Kind = "text", Text = "New text", X = 0.1, Y = 0.1, W = 0.4, H = 0.09, FontSize = 0.06, Bold = true },
            "rect" => new SceneLayer { Kind = "shape", X = 0.1, Y = 0.1, W = 0.25, H = 0.2, Color = "#CC2EA3F2", CornerRadius = 0.015 },
            "ellipse" => new SceneLayer { Kind = "shape", Shape = "ellipse", X = 0.1, Y = 0.1, W = 0.1125, H = 0.2, Color = "#CC2EA3F2" },
            "camera" => new SceneLayer { Kind = "camera", Name = "Video source", X = 0.6, Y = 0.45, W = 0.3, H = 0.45, CornerRadius = 0.02 },
            "bg" => _scene.Layers.FirstOrDefault(x => x.Kind == "image" && x.Behind && x.W >= 0.99 && x.H >= 0.99)
                    ?? new SceneLayer { Kind = "image", X = 0, Y = 0, W = 1, H = 1, Behind = true },
            _ => new SceneLayer { Kind = "image", X = 0.75, Y = 0.05, W = 0.2, H = 0.2 },
        };
        bool replacing = kind == "bg" && _scene.Layers.Contains(layer);
        if (galleryPick is not null)
        {
            SceneLibrary.Save(_scene);
            layer.Source = SceneLibrary.AddAsset(_scene, galleryPick);
            layer.Name = "Background: " + Path.GetFileNameWithoutExtension(galleryPick);
        }
        else if (kind is "image" or "bg")
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            SceneLibrary.Save(_scene);
            layer.Source = SceneLibrary.AddAsset(_scene, file.Path);
            layer.Name = kind == "bg" ? "Background: " + Path.GetFileNameWithoutExtension(file.Name) : Path.GetFileNameWithoutExtension(file.Name);
        }
        BeginEdit();
        if (kind == "bg" && BgMode.SelectedItem == BgOff) { BgMode.SelectedItem = BgRemove; UpdateBackground(); } // the picture shows where your real room was
        if (replacing) { }
        else if (kind is "camera" or "bg") _scene.Layers.Insert(0, layer); // video sits under the other layers
        else _scene.Layers.Add(layer);
        _selectedLayerId = layer.Id;
        Commit();
        RefreshLayersTab();
    }

    /// <summary>Built-in backgrounds (Assets\Backgrounds), grouped by profession, with a search box.</summary>
    private async Task<string?> PickGalleryBackgroundAsync()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds");
        var all = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).ToList() : [];
        if (all.Count == 0) { Status("No built-in backgrounds found."); return null; }
        var grid = new GridView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 460 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, "Backgrounds");
        void Fill(string q)
        {
            grid.Items.Clear();
            foreach (var f in all.Where(f => q.Length == 0 || Path.GetFileNameWithoutExtension(f).Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var cell = new StackPanel { Width = 176, Spacing = 4, Tag = f };
                cell.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(f)) { DecodePixelWidth = 176 }, Width = 176, Height = 99, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill });
                cell.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(f), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, Path.GetFileNameWithoutExtension(f));
                grid.Items.Add(cell);
            }
        }
        var search = new TextBox { PlaceholderText = "Search: doctor, chef, gaming…" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Search backgrounds");
        search.TextChanged += (_, _) => Fill(search.Text.Trim());
        Fill("");
        var body = new StackPanel { Spacing = 10, Width = 600 };
        body.Children.Add(search); body.Children.Add(grid);
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Choose a background", Content = body,
            PrimaryButtonText = "Use background", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 680.0;
        bool picked = false;
        grid.DoubleTapped += (_, _) => { if (grid.SelectedItem is not null) { picked = true; dlg.Hide(); } };
        var res = await dlg.ShowAsync();
        return (grid.SelectedItem as FrameworkElement)?.Tag as string is { } pick && (res == ContentDialogResult.Primary || picked) ? pick : null;
    }

    private void DeleteSelectedLayer()
    {
        if (Selected is not { } l) return;
        BeginEdit();
        _scene.Layers.Remove(l);
        _selectedLayerId = null;
        Commit();
        RefreshLayersTab();
    }

    private void MoveSelected(int delta)
    {
        if (Selected is not { } l) return;
        int i = _scene.Layers.IndexOf(l), j = Math.Clamp(i + delta, 0, _scene.Layers.Count - 1);
        if (i == j) return;
        BeginEdit();
        _scene.Layers.RemoveAt(i); _scene.Layers.Insert(j, l);
        Commit();
    }

    // ── Scene actions ───────────────────────────────────────────────────

    private void SceneDuplicate_Click(object sender, RoutedEventArgs e)
    {
        var copy = SceneLibrary.Duplicate(_scene);
        RefreshSceneRail(copy.Id);
        Status($"Duplicated as \"{copy.Name}\"");
    }

    private async void SceneExport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = _scene.Name };
        picker.FileTypeChoices.Add("KISSIK scene", [".okscene"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try { SceneLibrary.Save(_scene); SceneLibrary.Export(_scene, file.Path); Status("Scene exported: " + file.Path); }
        catch (Exception ex) { Status("Export failed: " + ex.Message); }
    }

    private async void SceneResetOrDelete_Click(object sender, RoutedEventArgs e)
    {
        bool template = _scene.IsTemplate;
        var dialog = new ContentDialog
        {
            Title = template ? $"Reset \"{_scene.Name}\"?" : $"Delete \"{_scene.Name}\"?",
            Content = template ? "Your changes to this scene will be lost." : "This scene and its images will be removed.",
            PrimaryButtonText = template ? "Reset" : "Delete", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string id = _scene.Id;
        SceneLibrary.ResetOrDelete(id);
        RefreshSceneRail(template ? id : "blank");
        Status(template ? "Scene reset." : "Scene deleted.");
    }

    // ── Stage selection, drag and resize ────────────────────────────────

    private string? _dragId;
    private Windows.Foundation.Point _dragStart;
    private (double X, double Y, double W, double H) _dragOrigin;
    private bool _dragMoved;

    private FrameworkElement? ElementFor(string id) =>
        OverlayCanvas.Children.OfType<FrameworkElement>().LastOrDefault(e => e.Tag as string == id);

    private string? HitLayerId(object? source)
    {
        for (var d = source as DependencyObject; d is not null && d != OverlayCanvas; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: string id } fe && VisualTreeHelper.GetParent(fe) == OverlayCanvas) return id;
        return null;
    }

    private void Overlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        string? id = HitLayerId(e.OriginalSource);
        if (id is null && TryPhoneTapFocus(e)) { e.Handled = true; return; }
        // Clicking empty canvas selects the camera when it is a placed layer, otherwise clears the selection.
        _selectedLayerId = id;
        RefreshLayerList(); UpdateAdorner(); BuildProps();
        if (id is null || Selected is not { } l) return;
        _dragId = id; _dragMoved = false;
        _dragStart = e.GetCurrentPoint(OverlayCanvas).Position;
        _dragOrigin = (l.X, l.Y, l.W, l.H);
        OverlayCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || Selected is not { } l) return;
        var p = e.GetCurrentPoint(OverlayCanvas).Position;
        double dx = (p.X - _dragStart.X) / Stage.Width, dy = (p.Y - _dragStart.Y) / Stage.Height;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 0.003) return;
        if (!_dragMoved) { BeginEdit(); _dragMoved = true; }
        l.X = Math.Clamp(Snap(_dragOrigin.X + dx), -l.W * 0.9, 1 - l.W * 0.1);
        l.Y = Math.Clamp(Snap(_dragOrigin.Y + dy), -l.H * 0.9, 1 - l.H * 0.1);
        if (ElementFor(l.Id) is { } el) { Canvas.SetLeft(el, l.X * Stage.Width); Canvas.SetTop(el, l.Y * Stage.Height); }
        UpdateAdorner();
    }

    private void Overlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null) return;
        OverlayCanvas.ReleasePointerCapture(e.Pointer);
        string id = _dragId; _dragId = null;
        if (_dragMoved) { Commit(); return; }
        // A click (no drag) on text: edit it right there on the video.
        if (Selected is { Kind: "text" } l && ElementFor(id) is FrameworkElement el) ShowQuickEdit(l, el);
    }

    private void SelectLayersTab() { if (SidebarTabs.SelectedItem != TabLayers) SidebarTabs.SelectedItem = TabLayers; }

    /// <summary>Edit a text layer in place: text, size, bold and colour, applied live; one undo step.</summary>
    private void ShowQuickEdit(SceneLayer l, FrameworkElement anchor)
    {
        SelectLayersTab();
        BeginEdit();
        var text = new TextBox { Text = l.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 260, Header = "Text" };
        text.TextChanged += (_, _) => { l.Text = text.Text; RenderScene(); };
        var size = new Slider { Header = "Size", Minimum = 10, Maximum = 200, Value = l.FontSize * 1000 };
        size.ValueChanged += (_, e2) => { l.FontSize = e2.NewValue / 1000; RenderScene(); };
        var bold = new ToggleSwitch { Header = "Bold", IsOn = l.Bold };
        bold.Toggled += (_, _) => { l.Bold = bold.IsOn; RenderScene(); };
        var colours = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var hex in new[] { "#FFFFFFFF", "#FF111111", "#FFFFD36B", "#FF2EA3F2", "#FFFF6B6B", "#FF5BFF8F" })
        {
            var swatch = new Button { Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(14),
                Background = SceneRenderer.Brush(hex, Microsoft.UI.Colors.White) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, "Colour " + hex);
            swatch.Click += (_, _) => { l.Color = hex; RenderScene(); };
            colours.Children.Add(swatch);
        }
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(4) };
        panel.Children.Add(text); panel.Children.Add(size); panel.Children.Add(bold); panel.Children.Add(colours);
        var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        flyout.Opened += (_, _) => { text.Focus(FocusState.Programmatic); text.SelectAll(); };
        flyout.Closed += (_, _) => { Commit(); BuildProps(); };
        // Anchor to the stage, not the text element: typing redraws the scene and replaces that element,
        // which would close a flyout attached to it after the first keystroke.
        var at = anchor.TransformToVisual(Stage).TransformPoint(new Windows.Foundation.Point(anchor.ActualWidth / 2, 0));
        flyout.ShowAt(Stage, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = at, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top });
    }

    /// <summary>Snaps to the canvas edges and centre within 1 %.</summary>
    private static double Snap(double v)
    {
        foreach (var s in new[] { 0.0, 0.5, 1.0 }) if (Math.Abs(v - s) < 0.01) return s;
        return v;
    }

    private void Resize_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Selected is not { } l) return;
        _dragId = l.Id; _dragMoved = false;
        _dragStart = e.GetCurrentPoint(OverlayCanvas).Position;
        _dragOrigin = (l.X, l.Y, l.W, l.H);
        ResizeHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Resize_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || Selected is not { } l) return;
        var p = e.GetCurrentPoint(OverlayCanvas).Position;
        double dw = (p.X - _dragStart.X) / Stage.Width, dh = (p.Y - _dragStart.Y) / Stage.Height;
        if (!_dragMoved) { BeginEdit(); _dragMoved = true; }
        l.W = Math.Max(0.02, _dragOrigin.W + dw);
        l.H = Math.Max(0.02, _dragOrigin.H + dh);
        if (l.Shape == "ellipse" && l.Kind is "camera" or "shape")
            l.H = l.W * Stage.Width / Stage.Height; // keep circles round
        if (ElementFor(l.Id) is { } el) { el.Width = l.W * Stage.Width; el.Height = l.H * Stage.Height; }
        UpdateAdorner();
    }

    private void Resize_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null) return;
        ResizeHandle.ReleasePointerCapture(e.Pointer);
        _dragId = null;
        if (_dragMoved) Commit(); // full re-render applies new font/stroke sizes
    }

    private void UpdateAdorner()
    {
        if (SelectionBox is null) return;
        if (Selected is not { } l)
        {
            SelectionBox.Visibility = ResizeHandle.Visibility = Visibility.Collapsed;
            return;
        }
        double x = l.X * Stage.Width, y = l.Y * Stage.Height, w = l.W * Stage.Width, h = l.H * Stage.Height;
        SelectionBox.Width = w + 6; SelectionBox.Height = h + 6;
        Canvas.SetLeft(SelectionBox, x - 3); Canvas.SetTop(SelectionBox, y - 3);
        Canvas.SetLeft(ResizeHandle, x + w - 11); Canvas.SetTop(ResizeHandle, y + h - 11);
        SelectionBox.Visibility = ResizeHandle.Visibility = Visibility.Visible;
    }

    // ── Properties panel ────────────────────────────────────────────────

    private void BuildProps()
    {
        PropsPanel.Children.Clear();
        if (Selected is not { } l) { LayersHint.Visibility = Visibility.Visible; return; }
        LayersHint.Visibility = Visibility.Collapsed;

        var header = new Grid();
        header.Children.Add(new TextBlock { Text = l.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 110, 0) });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 2 };
        tools.Children.Add(IconButton("", "Bring forward", () => MoveSelected(+1)));
        tools.Children.Add(IconButton("", "Send backward", () => MoveSelected(-1)));
        tools.Children.Add(IconButton("", "Delete layer (Del)", DeleteSelectedLayer));
        header.Children.Add(tools);
        PropsPanel.Children.Add(header);

        if (l.Kind != "text") PropsPanel.Children.Add(TextField("Layer name", l.Name, v => l.Name = v));

        switch (l.Kind)
        {
            case "text":
                PropsPanel.Children.Add(TextField("Text", l.Text, v => l.Text = v, multiline: true));
                PropsPanel.Children.Add(SliderField("Size", l.FontSize * 1000, 10, 200, v => l.FontSize = v / 1000));
                PropsPanel.Children.Add(ToggleField("Bold", l.Bold, v => l.Bold = v));
                PropsPanel.Children.Add(ChoiceField("Align", ["left", "center", "right"], l.Align, v => l.Align = v));
                PropsPanel.Children.Add(ColorField("Colour", l.Color, v => l.Color = v));
                PropsPanel.Children.Add(ColorField("Box fill", string.IsNullOrEmpty(l.Fill) ? "#00000000" : l.Fill, v => l.Fill = v));
                break;
            case "shape":
                PropsPanel.Children.Add(ChoiceField("Shape", ["rect", "ellipse"], l.Shape, v => l.Shape = v));
                PropsPanel.Children.Add(ColorField("Fill", l.Color, v => l.Color = v));
                PropsPanel.Children.Add(ColorField("Outline", string.IsNullOrEmpty(l.Stroke) ? "#00000000" : l.Stroke, v => l.Stroke = v));
                PropsPanel.Children.Add(SliderField("Outline width", l.StrokeWidth * 1000, 0, 40, v => l.StrokeWidth = v / 1000));
                PropsPanel.Children.Add(SliderField("Corner radius", l.CornerRadius * 1000, 0, 200, v => l.CornerRadius = v / 1000));
                break;
            case "image":
                var replace = new Button { Content = "Replace image…" };
                replace.Click += async (_, _) =>
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }) picker.FileTypeFilter.Add(ext);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                    if (await picker.PickSingleFileAsync() is { } f) { BeginEdit(); l.Source = SceneLibrary.AddAsset(_scene, f.Path); Commit(); }
                };
                PropsPanel.Children.Add(replace);
                PropsPanel.Children.Add(SliderField("Corner radius", l.CornerRadius * 1000, 0, 200, v => l.CornerRadius = v / 1000));
                break;
            case "camera" or "camera2":
                PropsPanel.Children.Add(ChoiceField("Shape", ["rect", "ellipse"], l.Shape, v =>
                {
                    l.Shape = v;
                    if (v == "ellipse") l.H = l.W * Stage.Width / Stage.Height;
                }));
                PropsPanel.Children.Add(SliderField("Corner radius", l.CornerRadius * 1000, 0, 200, v => l.CornerRadius = v / 1000));
                PropsPanel.Children.Add(ColorField("Outline", string.IsNullOrEmpty(l.Stroke) ? "#00000000" : l.Stroke, v => l.Stroke = v));
                PropsPanel.Children.Add(SliderField("Outline width", l.StrokeWidth * 1000, 0, 40, v => l.StrokeWidth = v / 1000));
                var fill = new Button { Content = "Fill the whole frame" };
                fill.Click += (_, _) => { BeginEdit(); l.X = 0; l.Y = 0; l.W = 1; l.H = 1; l.Shape = "rect"; l.CornerRadius = 0; Commit(); };
                PropsPanel.Children.Add(fill);
                break;
        }
        if (l.Kind != "camera")
            PropsPanel.Children.Add(ToggleField("Behind me (in front of the background)", l.Behind, v => l.Behind = v));
        PropsPanel.Children.Add(SliderField("Opacity", l.Opacity * 100, 5, 100, v => l.Opacity = v / 100));
    }

    private Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 13 }, Padding = new Thickness(6, 4, 6, 4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private FrameworkElement TextField(string label, string value, Action<string> set, bool multiline = false)
    {
        var box = new TextBox { Header = label, Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxHeight = 120 };
        box.LostFocus += (_, _) =>
        {
            if (box.Text == value) return;
            BeginEdit(); set(box.Text); value = box.Text; Commit(rebuildProps: false);
        };
        return box;
    }

    private FrameworkElement SliderField(string label, double value, double min, double max, Action<double> set)
    {
        var s = new Slider { Header = label, Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), StepFrequency = 1 };
        bool editing = false;
        s.ValueChanged += (_, e) =>
        {
            if (!editing) { BeginEdit(); editing = true; }
            set(e.NewValue);
            SceneLibrary.Save(_scene);
            RenderScene(); // live while dragging; list refresh on release
        };
        s.PointerCaptureLost += (_, _) => { if (editing) { editing = false; Commit(rebuildProps: false); } };
        s.LostFocus += (_, _) => { if (editing) { editing = false; Commit(rebuildProps: false); } };
        return s;
    }

    private FrameworkElement ToggleField(string label, bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { Header = label, IsOn = value };
        t.Toggled += (_, _) => { BeginEdit(); set(t.IsOn); Commit(rebuildProps: false); };
        return t;
    }

    private FrameworkElement ChoiceField(string label, string[] options, string value, Action<string> set)
    {
        var bar = new SelectorBar();
        foreach (var o in options)
            bar.Items.Add(new SelectorBarItem { Text = o switch { "rect" => "Rectangle", "ellipse" => "Circle", _ => char.ToUpper(o[0]) + o[1..] }, Tag = o, IsSelected = o == value });
        bar.SelectionChanged += (s, _) =>
        {
            if (s.SelectedItem?.Tag is string v && v != value) { BeginEdit(); set(v); value = v; Commit(); }
        };
        return new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, FontSize = 12 }, bar } };
    }

    private FrameworkElement ColorField(string label, string value, Action<string> set)
    {
        var btn = new DropDownButton { Padding = new Thickness(4) };
        SetColorButton(btn, value, hex => { BeginEdit(); set(hex); Commit(rebuildProps: false); });
        var row = new Grid();
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        btn.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(btn);
        return row;
    }

    /// <summary>A swatch button whose flyout is a colour picker (with alpha); applies when the flyout closes.</summary>
    private static void SetColorButton(DropDownButton btn, string hex, Action<string> onPicked)
    {
        var color = SceneRenderer.ToColor(hex, Microsoft.UI.Colors.White);
        var swatch = new Border
        {
            Width = 22, Height = 16, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(color),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(80, 128, 128, 128)), BorderThickness = new Thickness(1),
        };
        btn.Content = swatch;
        var picker = new ColorPicker { Color = color, IsAlphaEnabled = true, IsMoreButtonVisible = true };
        picker.ColorChanged += (_, e) => swatch.Background = new SolidColorBrush(e.NewColor);
        var flyout = new Flyout { Content = picker };
        string start = hex;
        flyout.Closed += (_, _) =>
        {
            var c = picker.Color;
            string picked = ColorHex.Format(c.A, c.R, c.G, c.B);
            if (!string.Equals(picked, start, StringComparison.OrdinalIgnoreCase)) { start = picked; onPicked(picked); }
        };
        btn.Flyout = flyout;
        AutomationProperties.SetHelpText(btn, hex);
    }
}
