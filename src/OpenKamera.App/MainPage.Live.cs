using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Go live: stream KISSIK's finished picture and sound to one or more RTMP destinations through FFmpeg.
public sealed partial class MainPage
{
    private LiveStream? _live;
    private readonly System.Diagnostics.Stopwatch _liveClock = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _liveTimer;

    private async void LiveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_live is not null) { await StopLiveAsync(); return; }
        if (await EditDestinationsAsync() is not { Count: > 0 } outputs) return;
        try
        {
            var live = new LiveStream(_profile.Width, _profile.Height, _profile.Fps);
            await live.StartAsync(outputs, withAudio: _audio.IsRunning);
            _live = live;
            _liveClock.Restart();
            LiveBtn.Background = Token("DgAccentBrush");
            LiveBtn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            AutomationProperties.SetName(LiveBtn, "Stop the livestream");
            _liveTimer ??= DispatcherQueue.CreateTimer();
            _liveTimer.Interval = TimeSpan.FromSeconds(1);
            _liveTimer.Tick -= LiveTick; _liveTimer.Tick += LiveTick;
            _liveTimer.Start();
            Status($"Live to {outputs.Count} destination(s) at {live.Width}×{live.Height} {live.Fps} fps.");
        }
        catch (Exception ex) { _live = null; Status("Couldn't go live: " + ex.Message); }
    }

    private async void LiveTick(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object o)
    {
        if (_live is not { } live) return;
        if (!live.IsRunning)
        {
            string why = live.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "the connection closed";
            await StopLiveAsync();
            Status("The livestream stopped: " + why.Trim() + " Check the stream key and your internet connection.");
            return;
        }
        LiveLabel.Text = $"Live {_liveClock.Elapsed:mm\\:ss}";
        int total = live.FramesSent + live.FramesDropped;
        if (total > 300 && live.FramesDropped * 5 > total)
            HudText.Text = $"Live · network is slow ({100 * live.FramesDropped / total}% frames skipped)";
    }

    private async Task StopLiveAsync()
    {
        var live = _live; _live = null;
        _liveTimer?.Stop();
        LiveLabel.Text = "Go live";
        LiveBtn.ClearValue(Control.BackgroundProperty); LiveBtn.ClearValue(Control.ForegroundProperty);
        AutomationProperties.SetName(LiveBtn, "Go live");
        if (live is not null) { Status($"Livestream ended after {_liveClock.Elapsed:mm\\:ss}."); await live.DisposeAsync(); }
    }

    /// <summary>Destinations dialog: pick services, paste stream keys (stored encrypted). Returns the URLs to stream to.</summary>
    private async Task<List<string>?> EditDestinationsAsync()
    {
        if (LiveStream.FindFfmpeg() is null)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Livestreaming needs FFmpeg",
                Content = "KISSIK streams through FFmpeg, the free tool most streaming apps use. Install it (for example run: winget install Gyan.FFmpeg), then press Go live again.",
                CloseButtonText = "OK",
            }.ShowAsync();
            return null;
        }
        var dests = _settings.StreamDestinations;
        if (dests.Count == 0) dests.Add(new StreamDestination { Name = "YouTube", Url = StreamDestination.Presets[0].Url });
        var rows = new StackPanel { Spacing = 12, MinWidth = 460 };
        void Build()
        {
            rows.Children.Clear();
            foreach (var d in dests.ToList())
            {
                var on = new CheckBox { IsChecked = d.Enabled, Content = d.Name };
                var service = new ComboBox { ItemsSource = StreamDestination.Presets.Select(p => p.Name).ToList(), SelectedItem = d.Name, MinWidth = 140 };
                var url = new TextBox { Text = d.Url, PlaceholderText = "rtmp://server/app", Header = "Server" };
                var key = new PasswordBox { Password = d.Key, PlaceholderText = "Paste your stream key", Header = "Stream key" };
                var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
                AutomationProperties.SetName(remove, $"Remove {d.Name}"); AutomationProperties.SetName(on, $"Stream to {d.Name}");
                on.Checked += (_, _) => d.Enabled = true; on.Unchecked += (_, _) => d.Enabled = false;
                service.SelectionChanged += (_, _) =>
                {
                    if (service.SelectedItem is not string n) return;
                    d.Name = n; on.Content = n;
                    url.Text = d.Url = StreamDestination.Presets.First(p => p.Name == n).Url;
                };
                url.TextChanged += (_, _) => d.Url = url.Text.Trim();
                key.PasswordChanged += (_, _) => d.Key = key.Password.Trim();
                remove.Click += (_, _) => { dests.Remove(d); Build(); };
                var head = new Grid { ColumnSpacing = 8 };
                head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(service, 1); Grid.SetColumn(remove, 2);
                head.Children.Add(on); head.Children.Add(service); head.Children.Add(remove);
                var card = new StackPanel { Spacing = 6 };
                card.Children.Add(head); card.Children.Add(url); card.Children.Add(key);
                rows.Children.Add(card);
            }
            var add = new Button { Content = "Add another destination" };
            add.Click += (_, _) => { dests.Add(new StreamDestination { Name = "Twitch", Url = StreamDestination.Presets[1].Url }); Build(); };
            rows.Children.Add(add);
            rows.Children.Add(new TextBlock { Text = "Keys are stored encrypted for your Windows account. Several destinations stream at once from one encode.", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        }
        Build();
        var res = await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Go live", Content = new ScrollViewer { Content = rows, MaxHeight = 520 },
            PrimaryButtonText = "Start streaming", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();
        _settings.Save();
        if (res != ContentDialogResult.Primary) return null;
        var ready = dests.Where(d => d.Enabled && (d.Url.Contains("://") || d.Url.StartsWith("file:")) && (d.Name == "Custom RTMP" || !string.IsNullOrEmpty(d.Key))).Select(d => d.FullUrl).ToList();
        if (ready.Count == 0) Status("Add a stream key for at least one destination (find it in your YouTube Studio or Twitch dashboard).");
        return ready;
    }
}
