using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace OpenKamera_App;

/// <summary>Recordings and snapshots: browse, play, trim, share, show in folder, delete (to the Recycle Bin).</summary>
public sealed class RecordingsWindow : Window
{
    private readonly string[] _folders;
    private readonly GridView _grid = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(16) };
    private readonly TextBlock _empty = new() { Text = "No recordings or snapshots yet.", Margin = new Thickness(24), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 0, 16, 12), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly IntPtr _hwnd;
    private StorageFile? _shareFile;

    public RecordingsWindow(params string[] folders)
    {
        _folders = folders;
        Title = "KISSIK recordings";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); // follows the theme, like the Gallery
        var bar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, HorizontalAlignment = HorizontalAlignment.Left };
        bar.PrimaryCommands.Add(Cmd("", "Play", Play));
        bar.PrimaryCommands.Add(Cmd("", "Trim", Trim));
        bar.PrimaryCommands.Add(Cmd("", "Share", Share));
        bar.PrimaryCommands.Add(Cmd("", "Show in folder", ShowInFolder));
        bar.PrimaryCommands.Add(Cmd("", "Delete", Delete));
        bar.PrimaryCommands.Add(Cmd("", "Refresh", () => { _ = LoadAsync(); return Task.CompletedTask; }));
        _grid.ItemTemplate = null;
        _grid.DoubleTapped += async (_, _) => await Play();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_grid, "Recordings and snapshots");
        Grid.SetRow(_grid, 1); Grid.SetRow(_empty, 1); Grid.SetRow(_status, 2);
        root.Children.Add(bar); root.Children.Add(_grid); root.Children.Add(_empty); root.Children.Add(_status);
        Content = root;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(980, 700));
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dtm = DataTransferManagerInterop.GetForWindow(_hwnd); // share sheet for a desktop window
        dtm.DataRequested += (_, a) =>
        {
            if (_shareFile is null) return;
            a.Request.Data.Properties.Title = _shareFile.Name;
            a.Request.Data.SetStorageItems([_shareFile]);
        };
        _ = LoadAsync();
    }

    private static AppBarButton Cmd(string glyph, string label, Func<Task> run)
    {
        var b = new AppBarButton { Icon = new FontIcon { Glyph = glyph }, Label = label };
        AutomationProperties.SetName(b, label);
        b.Click += async (_, _) => await run();
        return b;
    }

    private async Task LoadAsync()
    {
        var items = RecordingTools.List(_folders);
        _grid.Items.Clear();
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var it in items)
        {
            var img = new Image { Width = 240, Height = 135, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
            var name = new TextBlock { Text = Path.GetFileName(it.Path), TextTrimming = TextTrimming.CharacterEllipsis, Width = 240 };
            var meta = new TextBlock { Text = $"{(it.IsVideo ? "Video" : "Snapshot")} · {it.Created:d MMM, HH:mm} · {it.Bytes / 1_048_576.0:0.#} MB", FontSize = 12, Opacity = 0.7 };
            var card = new StackPanel { Spacing = 4, Tag = it };
            card.Children.Add(new Border { CornerRadius = new CornerRadius(6), Child = img });
            card.Children.Add(name); card.Children.Add(meta);
            AutomationProperties.SetName(card, $"{Path.GetFileName(it.Path)}, {meta.Text}");
            _grid.Items.Add(card);
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(it.Path);
                using var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 240);
                var bmp = new BitmapImage(); await bmp.SetSourceAsync(thumb); img.Source = bmp;
            }
            catch { /* no thumbnail: keep the empty frame */ }
        }
        _status.Text = $"{items.Count} item(s) in {string.Join(" and ", _folders.Where(Directory.Exists))}";
    }

    private MediaItem? Selected => (_grid.SelectedItem as FrameworkElement)?.Tag as MediaItem;

    private async Task<bool> NeedSelection()
    {
        if (Selected is not null) return true;
        _status.Text = "Pick a recording or snapshot first.";
        await Task.CompletedTask; return false;
    }

    private async Task Play()
    {
        if (!await NeedSelection()) return;
        await Windows.System.Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(Selected!.Path));
    }

    private async Task ShowInFolder()
    {
        if (!await NeedSelection()) return;
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Selected!.Path}\"");
    }

    private async Task Share()
    {
        if (!await NeedSelection()) return;
        _shareFile = await StorageFile.GetFileFromPathAsync(Selected!.Path);
        DataTransferManagerInterop.ShowShareUIForWindow(_hwnd);
    }

    private async Task Delete()
    {
        if (!await NeedSelection()) return;
        var it = Selected!;
        var ok = await new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Move to the Recycle Bin?", Content = Path.GetFileName(it.Path),
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
        if (ok != ContentDialogResult.Primary) return;
        await (await StorageFile.GetFileFromPathAsync(it.Path)).DeleteAsync(StorageDeleteOption.Default); // Recycle Bin, can be restored
        _status.Text = $"Moved {Path.GetFileName(it.Path)} to the Recycle Bin.";
        await LoadAsync();
    }

    private async Task Trim()
    {
        if (!await NeedSelection()) return;
        var it = Selected!;
        if (!it.IsVideo) { _status.Text = "Only videos can be trimmed."; return; }
        var props = await (await StorageFile.GetFileFromPathAsync(it.Path)).Properties.GetVideoPropertiesAsync();
        double total = Math.Max(1, props.Duration.TotalSeconds);
        var start = new Slider { Header = "Start", Minimum = 0, Maximum = total, StepFrequency = 0.1, Value = 0 };
        var end = new Slider { Header = "End", Minimum = 0, Maximum = total, StepFrequency = 0.1, Value = total };
        var info = new TextBlock { Opacity = 0.8 };
        void Upd() => info.Text = $"Keep {TimeSpan.FromSeconds(start.Value):mm\\:ss\\.f} to {TimeSpan.FromSeconds(end.Value):mm\\:ss\\.f} ({end.Value - start.Value:0.0} s of {total:0.0} s). The original stays as it is.";
        start.ValueChanged += (_, _) => { if (start.Value > end.Value - 0.5) start.Value = Math.Max(0, end.Value - 0.5); Upd(); };
        end.ValueChanged += (_, _) => { if (end.Value < start.Value + 0.5) end.Value = Math.Min(total, start.Value + 0.5); Upd(); };
        Upd();
        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        panel.Children.Add(start); panel.Children.Add(end); panel.Children.Add(info);
        var res = await new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Trim " + Path.GetFileName(it.Path), Content = panel,
            PrimaryButtonText = "Save trimmed copy", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();
        if (res != ContentDialogResult.Primary) return;
        _status.Text = "Trimming…";
        try
        {
            string outPath = await RecordingTools.TrimAsync(it.Path, TimeSpan.FromSeconds(start.Value), TimeSpan.FromSeconds(end.Value));
            _status.Text = "Saved " + Path.GetFileName(outPath);
            await LoadAsync();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
