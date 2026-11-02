using OpenKamera.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace OpenKamera_App;

public sealed partial class MainPage : Page
{
    private readonly CameraCatalog _catalog = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private CaptureSession? _session;
    private CameraController? _controller;
    private bool _updatingPreview;
    private int _processing, _skipped;
    private readonly FpsCounter _fps = new();
    private OutputProfile _profile = OutputProfile.P720;
    private SegmentedRecorder? _recorder;
    private DonationInfo _donation = new();
    private readonly System.Diagnostics.Stopwatch _recordClock = new();
    private DateTimeOffset _lastSaved = DateTimeOffset.MinValue;
    // Immutable snapshots, swapped on the UI thread and read on the frame thread.
    private volatile ImageFilters.Adjustments _adj = ImageFilters.Adjustments.Neutral;
    private volatile FrameTransform _transform = FrameTransform.Identity;
    private readonly List<Lut3D> _luts = [];
    private readonly BackgroundPipeline _bgPipeline = new();
    private volatile BackgroundSettings _bg = BackgroundSettings.Off;
    private bool _restoring;

    public MainPage()
    {
        InitializeComponent();
        Current = this;
        Loaded += MainPage_Loaded;
        SizeChanged += (_, _) => FitCapsule();
        Unloaded += async (_, _) => { if (_session is not null) await _session.DisposeAsync(); _bgPipeline.Dispose(); };
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        BuildProfileMenu();
        InitScenes();
        AddShortcuts();
        _donation = DonationInfo.Load(Path.Combine(AppContext.BaseDirectory, "donate.json"));
        DonateBtn.Visibility = _donation.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        RestoreLook();
        BuildLookStrip();
        try { InitVirtualCamera(); } catch (Exception ex) { Status("Meeting camera unavailable: " + ex.Message); }
        try { InitFraming(); } catch { }
        try { InitGuardAndMirror(); } catch { }
        try { InitTheme(); } catch { }
        try { InitSystemSound(); } catch { }
        try { InitAudio(); } catch (Exception ex) { Status("Audio unavailable: " + ex.Message); }
        try { InitScreenLight(); } catch (Exception ex) { Status("Screen light unavailable: " + ex.Message); LightBtn.IsEnabled = false; }
        await RefreshMicrophonesAsync();
        await RefreshDevicesAsync();
        // Don't make people press Connect every time: open the last camera straight away.
        if (_session is null && DeviceBox.SelectedItem is CameraDeviceInfo c && c != PhoneDevice) await ConnectAsync();
        StartConnectWatch();
        StartSceneThumbs();
        StartAudioWatchdog();
        _ = ShowWelcomeOnceAsync();
        ResumePairedPhone();
        try { InitGlobalHotkeys(); } catch (Exception ex) { Status("Global shortcuts unavailable: " + ex.Message); }
        Unloaded += async (_, _) => await StopPhoneAsync();
        _ = RecoverRecordingsAsync();
    }

    /// <summary>Ctrl+1…9 switch scenes, Ctrl+R records, Ctrl+Shift+S takes a snapshot.</summary>
    private void AddShortcuts()
    {
        for (int i = 0; i < 9; i++)
        {
            int n = i + 1;
            var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
            {
                Key = Windows.System.VirtualKey.Number1 + i,
                Modifiers = Windows.System.VirtualKeyModifiers.Control,
            };
            acc.Invoked += (_, a) => { SelectSceneByShortcut(n); a.Handled = true; };
            KeyboardAccelerators.Add(acc);
        }
        foreach (var (key, mods, act) in new (Windows.System.VirtualKey, Windows.System.VirtualKeyModifiers, Action)[]
        {
            (Windows.System.VirtualKey.Z, Windows.System.VirtualKeyModifiers.Control, Undo),
            (Windows.System.VirtualKey.Y, Windows.System.VirtualKeyModifiers.Control, Redo),
            (Windows.System.VirtualKey.Delete, Windows.System.VirtualKeyModifiers.None, DeleteSelectedLayer),
            (Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, TogglePause),
            (Windows.System.VirtualKey.H, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
                () => SetShutter(_shutter == ShutterMode.Off ? ShutterMode.Away : ShutterMode.Off)),
        })
        {
            var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = mods };
            acc.Invoked += (_, a) =>
            {
                // Leave Ctrl+Z / Delete to text boxes while typing.
                if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
                act(); a.Handled = true;
            };
            KeyboardAccelerators.Add(acc);
        }
        var rec = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.R, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        rec.Invoked += (_, a) => { if (RecordBtn.IsEnabled) RecordBtn_Click(RecordBtn, new RoutedEventArgs()); a.Handled = true; };
        KeyboardAccelerators.Add(rec);
        var snap = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.S,
            Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
        };
        snap.Invoked += (_, a) => { if (SnapshotBtn.IsEnabled) SnapshotBtn_Click(SnapshotBtn, new RoutedEventArgs()); a.Handled = true; };
        KeyboardAccelerators.Add(snap);
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
    }

    private async Task RefreshMicrophonesAsync()
    {
        try
        {
            var mics = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                Windows.Devices.Enumeration.DeviceClass.AudioCapture);
            var names = mics.Where(m => m.IsEnabled).Select(m => m.Name).ToList();
            MicList.ItemsSource = names;
            MicEmpty.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { Status("Microphone list failed: " + ex.Message); }
    }

    private void SidebarTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (CameraPanel is null) return; // fires during InitializeComponent
        var tab = sender.SelectedItem;
        CameraPanel.Visibility = tab == TabCamera ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = tab == TabAudio ? Visibility.Visible : Visibility.Collapsed;
        LayersPanel.Visibility = tab == TabLayers ? Visibility.Visible : Visibility.Collapsed;
    }

    // Glass panels float over the video, so hiding them just reveals more picture.
    private void LeftPaneToggle_Click(object sender, RoutedEventArgs e) => TogglePanes(LeftPane, AudioCard);
    private void RightPaneToggle_Click(object sender, RoutedEventArgs e) => TogglePanes(RightPane, Filmstrip);

    private void TogglePanes(params FrameworkElement[] panes)
    {
        bool show = panes[0].Visibility != Visibility.Visible;
        foreach (var p in panes) p.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateStageMargin();
    }

    /// <summary>Keeps the video in the space the panels leave free, so nothing in the picture is ever hidden.</summary>
    private void UpdateStageMargin()
    {
        bool left = LeftPane.Visibility == Visibility.Visible, right = RightPane.Visibility == Visibility.Visible;
        StageViewbox.Margin = new Thickness(left ? 340 : 12, 60, right ? 260 : 12, right ? 142 : 12);
        Filmstrip.Margin = new Thickness(left ? 340 : 12, 0, right ? 260 : 12, 12);
    }

    private void NewSceneBtn_Click(object sender, RoutedEventArgs e)
    {
        var s = SceneLibrary.CreateBlank();
        RefreshSceneRail(s.Id);
        TabLayers.IsSelected = true;
        Status("New scene created: add layers in the Layers tab.");
    }

    // ── Background tiles (drive the BgMode state holder) ──
    private void BgTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string t }) return;
        BgMode.SelectedItem = t switch { "blur" => BgBlur, "replace" => BgReplace, "remove" => BgRemove, _ => BgOff };
        UpdateBackground();
    }

    private void SyncBgTiles(BackgroundMode mode)
    {
        var accent = Token("DgAccentBrush");
        var none = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var (tile, m) in new (ToggleButton, BackgroundMode)[]
            { (TileOff, BackgroundMode.Off), (TileBlur, BackgroundMode.Blur), (TileReplace, BackgroundMode.Replace), (TileRemove, BackgroundMode.Remove) })
        {
            tile.IsChecked = m == mode;
            tile.BorderBrush = m == mode ? accent : none;
        }
        BgValue.Text = mode switch
        {
            BackgroundMode.Blur => $"Blur {BgBlurStrength.Value:0}%",
            BackgroundMode.Replace => (BgPicker.SelectedItem as BackgroundInfo)?.Name ?? "Replace",
            BackgroundMode.Remove => "Removed",
            _ => "Off",
        };
    }

    /// <summary>Switches profile; a connected camera is restarted in the matching mode.</summary>
    private async Task ChangeProfileAsync(OutputProfile p)
    {
        if (_recorder is not null)
        {
            Status("Stop recording before changing the profile.");
            BuildProfileMenu(); // re-check the current profile
            return;
        }
        ApplyProfile(p);
        _settings.ProfileName = p.Name; _settings.Save();
        if (_session is not null && DeviceBox.SelectedItem is CameraDeviceInfo cam)
        {
            Status($"Switching camera to suit {p.Name}…");
            try
            {
                await _session.StartAsync(cam.Id, p);
                _bgPipeline.Dispose(); // masks were sized for the old output
                Status($"Profile: {p.Name} ({p.Details}) — camera mode {_session.Mode?.ToString() ?? "default"}");
            }
            catch (Exception ex) { Status("Profile switch failed: " + ex.Message); }
        }
        else Status($"Profile: {p.Name} ({p.Details})");
    }

    private void ApplyProfile(OutputProfile p)
    {
        _profile = p;
        ProfileName.Text = p.Name;
        ProfilePillTop.Text = p.PillTop;
        ProfilePillBottom.Text = p.PillBottom;
        ProfilePillTopRun.Text = p.PillTop;
        ProfilePillBottomRun.Text = p.PillBottom.ToLowerInvariant();
        Stage.Width = p.IsVertical ? 720 : 1280;
        Stage.Height = p.IsVertical ? 1280 : 720;
        RenderScene(); // layers are normalised; re-lay them out and re-capture at the new size
    }

    /// <summary>Radio menu of output profiles; the details ("1080p 16:9 at 30 FPS") sit on the right.</summary>
    private void BuildProfileMenu()
    {
        var current = OutputProfile.All.FirstOrDefault(p => p.Name == _settings.ProfileName) ?? OutputProfile.P720;
        ProfileMenu.Items.Clear();
        foreach (var p in OutputProfile.All)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = p.Name, GroupName = "profiles", IsChecked = p == current,
                KeyboardAcceleratorTextOverride = p.Details, Tag = p,
            };
            item.Click += async (_, _) => await ChangeProfileAsync(p);
            ProfileMenu.Items.Add(item);
        }
        ApplyProfile(current);
    }

    private void FocusMode_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_controller is null || _restoring) return;
        bool auto = sender.SelectedItem == FocusAuto;
        bool ok = _controller.TrySet("Focus", _controller.GetProperties().FirstOrDefault(p => p.Name == "Focus")?.Value ?? 0, auto);
        Status(ok ? $"Focus: {(auto ? "auto" : "manual")}" : "Focus mode rejected by device");
    }


    private void MinimapCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMinimap();

    /// <summary>Draws the visible crop window inside the minimap.</summary>
    private void UpdateMinimap()
    {
        if (MinimapCanvas is null || MinimapCrop is null) return;
        double w = MinimapCanvas.ActualWidth, h = MinimapCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var t = _transform;
        double zoom = Math.Max(1, t.Zoom);
        double cw = w / zoom, ch = h / zoom;
        MinimapCrop.Width = cw; MinimapCrop.Height = ch;
        Canvas.SetLeft(MinimapCrop, (w - cw) / 2 * (1 + t.PanX));
        Canvas.SetTop(MinimapCrop, (h - ch) / 2 * (1 + t.PanY));
        ZoomLabel.Text = $"{zoom:0.0}×";
    }

    private IEnumerable<Slider> EnhanceSliders =>
        [EnhBrightness, EnhContrast, EnhGamma, EnhSaturation, EnhVibrance, EnhHue, EnhTemperature, EnhTint, EnhSharpness, LutStrength];

    /// <summary>Loads saved Enhance/Filter/LUT/Framing state and the LUT library.</summary>
    private void RestoreLook()
    {
        _restoring = true;
        FilterBox.ItemsSource = Enum.GetValues<FilterPreset>().Select(FilterLabel).ToList();
        FilterBox.SelectedIndex = (int)_settings.Filter;
        foreach (var s in EnhanceSliders)
            if (_settings.Enhance.TryGetValue(s.Name, out var v)) s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
        _luts.Clear();
        if (Directory.Exists(AppSettings.LutFolder))
            foreach (var f in Directory.EnumerateFiles(AppSettings.LutFolder, "*.cube"))
                try { _luts.Add(Lut3D.Load(f)); } catch (Exception ex) { Status($"Skipped LUT {Path.GetFileName(f)}: {ex.Message}"); }
        RefreshLutBox(_settings.LutName);
        MirrorToggle.IsChecked = _settings.Mirror;
        RestoreBackground();
        RefreshPresetBox(null);
        _restoring = false;
        UpdateBackground();
        _transform = new FrameTransform(_settings.Mirror, _settings.Rotation);
        UpdateAdjustments();
    }

    private static string FilterLabel(FilterPreset f) => f switch
    {
        FilterPreset.None => "No filter",
        FilterPreset.BlackAndWhite => "Black & white",
        _ => f.ToString(),
    };

    private void RefreshLutBox(string? select)
    {
        LutBox.ItemsSource = _luts.Select(l => l.Name).ToList();
        LutBox.SelectedItem = _luts.FirstOrDefault(l => l.Name == select)?.Name;
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var cams = (await _catalog.GetCamerasAsync()).ToList();
            cams.Add(PhoneDevice);
            DeviceBox.ItemsSource = cams;
            var last = cams.FirstOrDefault(c => c.Id == _settings.LastDeviceId && c != PhoneDevice) ?? cams.FirstOrDefault();
            DeviceBox.SelectedItem = last;
            EmptyText.Visibility = cams.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;
            Status($"Found {cams.Count - 1} camera(s), plus your phone.");
        }
        catch (Exception ex) { Status("Camera enumeration failed: " + ex.Message); }
    }

    private void Status(string msg) => StatusText.Text = msg;

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e) => await ConnectAsync(toggle: true);

    private async void EmptyConnect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async Task ConnectAsync(bool toggle = false)
    {
        if (!toggle && _session is not null) return;
        if (_session is not null)
        {
            if (_recorder is not null) await StopRecordingAsync();
            // The phone server keeps running so a paired phone can come back by itself.
            await _session.DisposeAsync();
            _session = null; _controller = null;
            ConnectBtn.Content = "Connect";
            SourceDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(102, 255, 255, 255));
            SourceInfo.Text = "";
            SnapshotBtn.IsEnabled = RecordBtn.IsEnabled = false;
            if (_live is not null) await StopLiveAsync();
            LiveBtn.IsEnabled = false;
            ControlsPanel.Children.Clear();
            FocusMode.IsEnabled = false;
            EmptyText.Visibility = Visibility.Visible;
            PreviewImage.Source = null;
            HudText.Text = "No device open";
            Status("Disconnected.");
            return;
        }
        if (DeviceBox.SelectedItem is not CameraDeviceInfo cam)
        {
            Status("No device open! Please connect the webcam!");
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            ConnectBtn.IsEnabled = false;
            _session = new CaptureSession();
            _session.FrameAvailable += OnFrame;
            if (cam == PhoneDevice) { await _session.StartExternalAsync(); await StartPhoneAsync(); }
            else await _session.StartAsync(cam.Id, _profile);
            _controller = _session.Controls is { } c ? new CameraController(c) : null;
            if (cam != PhoneDevice) _settings.LastDeviceId = cam.Id;
            _settings.Save();
            BuildControls();
            var focus = _controller?.GetProperties().FirstOrDefault(p => p.Name == "Focus");
            FocusMode.IsEnabled = focus is { CanAuto: true };
            if (focus is not null) { _restoring = true; FocusMode.SelectedItem = focus.Auto ? FocusAuto : FocusManual; _restoring = false; }
            CameraPanel.ChangeView(null, 0, null, disableAnimation: true);
            ConnectBtn.Content = "Disconnect";
            SourceDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 91, 255, 143));
            SnapshotBtn.IsEnabled = RecordBtn.IsEnabled = LiveBtn.IsEnabled = true;
            EmptyText.Visibility = Visibility.Collapsed;
            EmptyBusy.Text = "";
            HudText.Text = cam.Name;
            Status($"Connected: {cam.Name} — camera mode {_session.Mode?.ToString() ?? "default"} for {_profile.Name} ({_profile.Details})" + LimitNote(_session.Mode));
        }
        catch (UnauthorizedAccessException)
        {
            // Windows camera privacy denied (or never answered) for this app.
            Status("Camera access is off for KISSIK. Turn it on in Settings → Privacy & security → Camera, then Connect again.");
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));
            if (_session is not null) { await _session.DisposeAsync(); _session = null; }
        }
        catch (Exception ex)
        {
            var busy = DeviceUsage.InUseBy(PrivacyDevice.Camera).Distinct().ToList();
            EmptyBusy.Text = busy.Count > 0 ? $"Your camera is open in {string.Join(", ", busy)}. Close it there, then press Connect." : "";
            EmptyText.Visibility = Visibility.Visible;
            Status("Connect failed: " + ex.Message);
            if (_session is not null) { await _session.DisposeAsync(); _session = null; }
        }
        finally { ConnectBtn.IsEnabled = true; }
    }

    /// <summary>The look for the next frame. Overlays are excluded for the preview (XAML draws them live).</summary>
    private (byte[] Src, int W, int H, byte[] Scaled)? _behindCache, _frontCache;

    /// <summary>The scene's front layers at the processed size, so smooth-first halving never drops them from the output.</summary>
    private byte[]? FrontAt(int w, int h)
    {
        if (_overlay is not { } o) return null;
        if (o.Width == w && o.Height == h) return o.Pixels;
        if (_frontCache is { } c && ReferenceEquals(c.Src, o.Pixels) && c.W == w && c.H == h) return c.Scaled;
        var scaled = OutputFit.Stretch(o.Pixels, o.Width, o.Height, w, h).Pixels;
        _frontCache = (o.Pixels, w, h, scaled);
        return scaled;
    }

    /// <summary>The "behind me" layers at the size frames are processed at (smooth-first may halve it), cached per size.</summary>
    private byte[]? BehindAt(int w, int h)
    {
        if (_overlayBehind is not { } src) return null;
        if (src.Length == w * h * 4) return src;
        if (_behindCache is { } c && ReferenceEquals(c.Src, src) && c.W == w && c.H == h) return c.Scaled;
        var p = _profile;
        if (src.Length != p.Width * p.Height * 4) return null;
        var scaled = OutputFit.Stretch(src, p.Width, p.Height, w, h).Pixels;
        _behindCache = (src, w, h, scaled);
        return scaled;
    }

    private FrameRecipe CurrentRecipe(bool withOverlay = false)
    {
        var p = _profile;
        var overlay = _overlay;
        int level = _smooth.Level;
        var bg = level >= 1 && _bg.Quality == SegmentationQuality.High ? _bg with { Quality = SegmentationQuality.Fast } : _bg;
        bool steady = _stabilise && level < 2;
        // Level 3 halves the processing size, but never mid-recording and never below 720p.
        bool half = level >= 3 && _recorder is null && p.Height >= 1440;
        int ow = half ? (p.Width / 2) & ~1 : p.Width, oh = half ? (p.Height / 2) & ~1 : p.Height;
        return new FrameRecipe(_adj, steady ? _stabilizer.Apply(_transform) : _transform, bg, ow, oh, _sceneSnapshot,
            withOverlay && overlay is { Width: var vw, Height: var vh } && vw == ow && vh == oh ? overlay.Pixels : null,
            BehindAt(ow, oh));
    }

    /// <summary>
    /// Frame-reader thread: process every frame once (at profile size), feed the recorder, then hand the
    /// result to the preview if the UI is free. Frames that arrive while one is processing are skipped.
    /// </summary>
    private double _procMs;
    private volatile byte[]? _lastBaked, _fadeFrom;
    private DateTime _fadeStart;

    /// <summary>Starts a cross-fade from the current output to whatever comes next (call when the scene changes).</summary>
    private void BeginSceneFade() { _fadeFrom = _lastBaked; _fadeStart = DateTime.UtcNow; }
    private readonly SmoothGovernor _smooth = new();

    private void OnFrame(SoftwareBitmap bmp)
    {
        if (_smooth.Level < 2) { TrackFace(bmp); StabiliseMeasure(bmp); }
        if (Interlocked.Exchange(ref _processing, 1) == 1) { Interlocked.Increment(ref _skipped); return; }
        byte[] pixels; int w, h;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { (pixels, w, h) = FrameProcessor.ProcessPixels(bmp, CurrentRecipe(), _bgPipeline); }
        catch { Volatile.Write(ref _processing, 0); return; }
        // Smoothed effects time per frame: the delay OpenKamera adds on top of the camera itself.
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _procMs = _procMs <= 0 ? ms : _procMs * 0.9 + ms * 0.1;
        if (_smooth.Update(_procMs, _profile.Fps, DateTime.UtcNow))
        {
            int lv = _smooth.Level;
            DispatcherQueue.TryEnqueue(() => Status(lv switch
            {
                0 => "Smooth-first: effects back to full quality.",
                1 => "Smooth-first: lighter background edges to keep the video smooth.",
                2 => "Smooth-first: also pausing Steady picture and face tracking to keep the video smooth.",
                _ => "Smooth-first: processing at half size until there's headroom again (not while recording).",
            }));
        }
        _fps.Tick();
        _lastFrame = (pixels, w, h);
        // Meeting camera and recordings get the scene layers baked in; the preview shows them as live XAML.
        byte[]? baked = null;
        byte[] Baked()
        {
            if (baked is not null) return baked;
            baked = pixels;
            if (FrontAt(w, h) is { } front) { baked = (byte[])pixels.Clone(); SceneCompositor.BlendOver(baked, front); }
            // Scene switch: cross-fade from the last frame of the old scene.
            if (_fadeFrom is { } from && from.Length == baked.Length)
            {
                double t = (DateTime.UtcNow - _fadeStart).TotalSeconds / 0.4;
                if (t >= 1) _fadeFrom = null;
                else { if (ReferenceEquals(baked, pixels)) baked = (byte[])pixels.Clone(); SceneCompositor.CrossFade(baked, from, t); }
            }
            _lastBaked = baked;
            baked = BurnCaptions(baked, w, h);
            baked = PrivacyShutter.Apply(baked, w, h, _shutter != ShutterMode.Off ? _shutter : _guardHiding ? ShutterMode.Blur : ShutterMode.Off);
            return baked;
        }
        if (_vcam.HasClients) FeedVirtualCamera(Baked(), w, h);
        if (_live is { } live) live.Push(Baked(), w, h);
        if (_recorder is { } rec)
        {
            // Recording gets the scene layers baked in; the preview shows them as live XAML instead.
            if (!_recPaused) rec.Push(Baked(), w, h, _recordClock.Elapsed);
        }
        Volatile.Write(ref _processing, 0);

        if (_updatingPreview) return;
        _updatingPreview = true;
        SoftwareBitmap copy;
        try { copy = FrameProcessor.ToBitmap(pixels, w, h); }
        catch { _updatingPreview = false; return; }
        bool queued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var src = new SoftwareBitmapSource();
                await src.SetBitmapAsync(copy);
                PreviewImage.Source = src;
                string fx = _bg.NeedsMask
                    ? (_bgPipeline.LastError is { } err ? $" · effects error: {err}" : $" · effects {_bgPipeline.LastMs:0} ms{(_bgPipeline.UsesGpu ? " GPU" : " CPU")}")
                    : "";
                if (_recorder is not null)
                {
                    RecordLabel.Text = $"{_recordClock.Elapsed:mm\\:ss}";
                    RecPillText.Text = _recPaused ? $"❚❚ PAUSED {_recordClock.Elapsed:mm\\:ss}" : $"● REC {_recordClock.Elapsed:mm\\:ss}";
                }
                HudText.Text = $"{copy.PixelHeight}p {_fps.Fps:0} fps · {_procMs:0} ms{(_smooth.Level > 0 ? " · smooth-first" : "")}{fx}";
                if (_session?.Mode is { } m) SourceInfo.Text = $"{m.Width}×{m.Height} @ {m.Fps:0}";
            }
            catch (Exception ex) when (_session is not null) { Status("Preview error: " + ex.Message); }
            catch { /* frame raced with disconnect */ }
            finally { copy.Dispose(); _updatingPreview = false; }
        });
        if (!queued) { copy.Dispose(); _updatingPreview = false; }
    }

    private void BuildControls()
    {
        ControlsPanel.Children.Clear();
        if (_controller is null) { ControlsHint.Text = "This camera exposes no controllable properties."; return; }
        var props = _controller.GetProperties();
        ControlsHint.Text = props.Count == 0
            ? "This camera exposes no controllable properties."
            : "Ranges come from the device. Focus/Zoom sliders drive while off-centre.";
        foreach (var p in props)
        {
            bool bidirectional = p.Name is "Focus" or "Zoom";
            var label = new TextBlock { Text = p.Name, FontSize = 12 };
            ControlsPanel.Children.Add(label);
            if (bidirectional)
            {
                var s = new Slider { Minimum = -100, Maximum = 100, Value = 0, Tag = p.Name };
                s.ValueChanged += Lens_ValueChanged;
                s.PointerReleased += (_, _) => s.Value = 0;
                ControlsPanel.Children.Add(s);
            }
            else
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var s = new Slider
                {
                    Minimum = p.Min, Maximum = p.Max,
                    StepFrequency = p.Step == 0 ? 1 : p.Step,
                    Value = Math.Clamp(p.Value, p.Min, p.Max), Tag = p.Name,
                };
                s.ValueChanged += Property_ValueChanged;
                Grid.SetColumn(s, 0);
                row.Children.Add(s);
                if (p.CanAuto)
                {
                    var auto = new CheckBox { Content = "Auto", IsChecked = p.Auto, Tag = p.Name };
                    auto.Click += Auto_Click;
                    Grid.SetColumn(auto, 1);
                    row.Children.Add(auto);
                }
                ControlsPanel.Children.Add(row);
            }
        }
    }

    private void Property_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_controller is null || sender is not Slider { Tag: string name }) return;
        bool ok = _controller.TrySet(name, e.NewValue, auto: false);
        var saved = _settings.ForDevice(_settings.LastDeviceId ?? "");
        saved.Values[name] = e.NewValue;
        _settings.Save();
        Status($"{name} → {e.NewValue:0} ({(ok ? "applied" : "rejected by device")})");
    }

    private void Lens_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_controller is null || sender is not Slider { Tag: string name }) return;
        int v = (int)e.NewValue;
        if (v == 0) return;
        double? applied = _controller.Drive(name, v);
        Status(applied is null ? $"{name}: not supported" : $"{name} driving… value {applied:0}");
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null || sender is not CheckBox { Tag: string name } cb) return;
        _controller.TrySet(name, 0, auto: cb.IsChecked == true);
        _settings.ForDevice(_settings.LastDeviceId ?? "").Auto[name] = cb.IsChecked == true;
        _settings.Save();
    }

    private ImageFilters.Adjustments CurrentAdjustments() => _adj;

    private void UpdateAdjustments()
    {
        if (_restoring) return;
        _adj = new ImageFilters.Adjustments(
            Brightness: EnhBrightness.Value / 100.0,
            Contrast: EnhContrast.Value / 100.0,
            Saturation: EnhSaturation.Value / 100.0,
            Sharpness: EnhSharpness.Value / 100.0,
            Gamma: EnhGamma.Value / 100.0,
            Hue: EnhHue.Value,
            Vibrance: EnhVibrance.Value / 100.0,
            Temperature: EnhTemperature.Value / 100.0,
            Tint: EnhTint.Value / 100.0,
            Filter: (FilterPreset)Math.Max(0, FilterBox.SelectedIndex),
            Lut: _luts.FirstOrDefault(l => l.Name == LutBox.SelectedItem as string),
            LutStrength: LutStrength.Value / 100.0);
    }

    private void SaveLook()
    {
        if (_restoring) return;
        foreach (var s in EnhanceSliders) _settings.Enhance[s.Name] = s.Value;
        _settings.Filter = _adj.Filter;
        _settings.LutName = _adj.Lut?.Name;
        _settings.Mirror = _transform.Mirror;
        _settings.Rotation = _transform.Rotation;
        _settings.Save();
    }

    private void Enhance_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_restoring || EnhSharpness is null || LutStrength is null) return; // fires during InitializeComponent
        UpdateAdjustments(); SaveLook();
    }

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateAdjustments(); SaveLook(); SyncLookTiles(); }
    private void LutBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateAdjustments(); SaveLook(); SyncLookTiles(); }

    private void EnhanceReset_Click(object sender, RoutedEventArgs e)
    {
        _restoring = true;
        EnhBrightness.Value = EnhContrast.Value = EnhGamma.Value = EnhSaturation.Value = 100;
        EnhVibrance.Value = EnhHue.Value = EnhTemperature.Value = EnhTint.Value = EnhSharpness.Value = 0;
        FilterBox.SelectedIndex = 0;
        LutBox.SelectedItem = null;
        LutStrength.Value = 100;
        _restoring = false;
        UpdateAdjustments(); SaveLook();
    }

    private async void ImportLutBtn_Click(object sender, RoutedEventArgs e) => await ImportLutCoreAsync();

    /// <summary>Picks, validates and stores a .cube LUT, then selects it. Returns null when cancelled or invalid.</summary>
    internal async Task<Lut3D?> ImportLutCoreAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".cube");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return null;
        try
        {
            var lut = Lut3D.Load(file.Path); // validate before copying
            Directory.CreateDirectory(AppSettings.LutFolder);
            File.Copy(file.Path, Path.Combine(AppSettings.LutFolder, Path.GetFileName(file.Path)), overwrite: true);
            _luts.RemoveAll(l => l.Name == lut.Name);
            _luts.Add(lut);
            RefreshLutBox(lut.Name);
            Status($"LUT imported: {lut.Name} ({lut.Size}³)");
            return lut;
        }
        catch (Exception ex) { Status("LUT import failed: " + ex.Message); return null; }
    }

    private void ClearLutBtn_Click(object sender, RoutedEventArgs e) => LutBox.SelectedItem = null;

    // ── Background, Spotlight, Retouch ──────────────────────────────────

    private void RestoreBackground()
    {
        var b = _settings.Background ?? BackgroundSettings.Off;
        BgMode.SelectedItem = b.Mode switch
        {
            BackgroundMode.Blur => BgBlur, BackgroundMode.Replace => BgReplace, BackgroundMode.Remove => BgRemove, _ => BgOff,
        };
        BgBlurStrength.Value = b.BlurStrength * 100;
        BgQuality.SelectedItem = b.Quality == SegmentationQuality.High ? BgQualityHigh : BgQualityFast;
        SpotlightToggle.IsOn = b.Spotlight > 0;
        if (b.Spotlight > 0) SpotlightAmount.Value = b.Spotlight * 100;
        RetouchToggle.IsOn = b.Retouch > 0;
        if (b.Retouch > 0) RetouchAmount.Value = b.Retouch * 100;
        GreenScreenToggle.IsOn = b.GreenScreen;
        KeyColorBar.SelectedItem = b.KeyColor == BlueKey ? KeyBlue : KeyGreen;
        KeySimilarity.Value = b.Similarity * 100; KeySmoothness.Value = b.Smoothness * 100; KeySpill.Value = b.Spill * 100;
        RefreshBackgroundPicker(b.BackgroundId);
    }

    private void RefreshBackgroundPicker(string? select)
    {
        var all = BackgroundLibrary.All();
        BgPicker.ItemsSource = all;
        BgPicker.DisplayMemberPath = nameof(BackgroundInfo.Name);
        BgPicker.SelectedItem = all.FirstOrDefault(b => b.Id == select) ?? all.FirstOrDefault();
    }

    private const string GreenKey = "#00B140", BlueKey = "#0047BB";

    private void UpdateBackground()
    {
        if (_restoring || RetouchAmount is null || BgPicker is null || KeySpill is null) return; // fires during InitializeComponent
        var item = BgMode.SelectedItem;
        var mode = item == BgBlur ? BackgroundMode.Blur : item == BgReplace ? BackgroundMode.Replace
                 : item == BgRemove ? BackgroundMode.Remove : BackgroundMode.Off;
        BgBlurStrength.Visibility = mode == BackgroundMode.Blur ? Visibility.Visible : Visibility.Collapsed;
        BgPickerRow.Visibility = mode == BackgroundMode.Replace ? Visibility.Visible : Visibility.Collapsed;
        SyncBgTiles(mode);
        SpotlightAmount.IsEnabled = SpotlightToggle.IsOn;
        GreenScreenPanel.Visibility = GreenScreenToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        BgQuality.Visibility = GreenScreenToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        RetouchAmount.IsEnabled = RetouchToggle.IsOn;
        SpotlightValue.Text = SpotlightToggle.IsOn ? $"{SpotlightAmount.Value:0} %" : "";
        _bg = new BackgroundSettings(
            Mode: mode,
            BlurStrength: BgBlurStrength.Value / 100.0,
            Spotlight: SpotlightToggle.IsOn ? SpotlightAmount.Value / 100.0 : 0,
            Retouch: RetouchToggle.IsOn ? RetouchAmount.Value / 100.0 : 0,
            BackgroundId: (BgPicker.SelectedItem as BackgroundInfo)?.Id,
            Quality: BgQuality.SelectedItem == BgQualityHigh ? SegmentationQuality.High : SegmentationQuality.Fast,
            GreenScreen: GreenScreenToggle.IsOn,
            KeyColor: KeyColorBar.SelectedItem == KeyBlue ? BlueKey : GreenKey,
            Similarity: KeySimilarity.Value / 100.0, Smoothness: KeySmoothness.Value / 100.0, Spill: KeySpill.Value / 100.0);
        _settings.Background = _bg;
        _settings.Save();
    }

    private void BgMode_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => UpdateBackground();
    private void BgQuality_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => UpdateBackground();
    private void BgPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBackground();
    private void Background_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdateBackground();
    private void Background_Toggled(object sender, RoutedEventArgs e) => UpdateBackground();

    private async void BgImportBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            string id = BackgroundLibrary.Import(file.Path);
            RefreshBackgroundPicker(id);
            Status($"Background added: {file.Name}");
        }
        catch (Exception ex) { Status("Background import failed: " + ex.Message); }
    }

    private void UpdateTransform()
    {
        if (_restoring || FrPanY is null) return;
        _transform = new FrameTransform(
            Mirror: MirrorToggle.IsChecked == true,
            Rotation: _transform.Rotation,
            Zoom: FrZoom.Value / 100.0,
            PanX: FrPanX.Value / 100.0,
            PanY: FrPanY.Value / 100.0);
        UpdateMinimap();
        SaveLook();
    }

    private void Framing_Changed(object sender, RoutedEventArgs e) => UpdateTransform();
    private void Framing_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdateTransform();

    private void RotateBtn_Click(object sender, RoutedEventArgs e)
    {
        _transform = _transform with { Rotation = (_transform.Rotation + 90) % 360 };
        UpdateTransform();
    }

    private void FramingReset_Click(object sender, RoutedEventArgs e)
    {
        _restoring = true;
        MirrorToggle.IsChecked = false;
        FrZoom.Value = 100; FrPanX.Value = FrPanY.Value = 0;
        _restoring = false;
        _transform = FrameTransform.Identity;
        UpdateTransform();
    }

    private async void SnapshotBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        try
        {
            string folder = string.IsNullOrWhiteSpace(_settings.ImageFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OpenKamera")
                : _settings.ImageFolder;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"OpenKamera-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await _session.SaveSnapshotAsync(path, CurrentRecipe(withOverlay: true), _bgPipeline);
            _lastSaved = DateTimeOffset.Now;
            Status("Snapshot saved: " + path);
        }
        catch (Exception ex) { Status("Snapshot failed: " + ex.Message); }
    }

    private async void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        try
        {
            if (_recorder is not null) { await StopRecordingAsync(); return; }
            if (_countdown is { } running) { running.Cancel(); return; } // pressing Record again cancels the countdown
            if (!await CountdownAsync()) { Status("Recording cancelled."); return; }
            string folder = RecordingFolder();
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"KISSIK-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            var rec = new SegmentedRecorder(_profile.Width, _profile.Height, _profile.Fps, withAudio: _audio.IsRunning);
            await rec.StartAsync(path);
            _recordClock.Restart();
            _recorder = rec;
            RecordLabel.Text = "00:00"; RecordIcon.Glyph = "";
            RecordBtn.Background = (Microsoft.UI.Xaml.Media.Brush)Resources["DgRecBrush"];
            RecordBtn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            RecPill.Visibility = Visibility.Visible;
            _recPaused = false; PauseBtn.Visibility = Visibility.Visible; SetPauseLook();
            AutomationProperties.SetName(RecordBtn, "■ Stop");
            Status($"Recording {rec.Width}×{rec.Height} @ {rec.Fps} fps: {rec.Path}");
        }
        catch (Exception ex) { _recorder = null; Status("Record failed: " + ex.Message); }
    }

    /// <summary>Donation dialog: PayPal link and a scannable UPI QR with copyable UPI ID.</summary>
    private async void DonateBtn_Click(object sender, RoutedEventArgs e)
    {
        var d = _donation;
        var body = new StackPanel { Spacing = 14, MaxWidth = 380 };
        if (!string.IsNullOrWhiteSpace(d.Message))
            body.Children.Add(new TextBlock { Text = d.Message, TextWrapping = TextWrapping.Wrap });

        if (d.HasPayPal)
        {
            var paypal = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new FontIcon { Glyph = "", FontSize = 14 }, new TextBlock { Text = "Donate with PayPal" } },
                },
            };
            AutomationProperties.SetName(paypal, "Donate with PayPal");
            paypal.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(d.PayPalUrl));
            body.Children.Add(paypal);
            body.Children.Add(new TextBlock
            {
                Text = d.PayPalUrl, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
        }

        if (d.HasUpi)
        {
            body.Children.Add(new TextBlock { Text = "UPI (India)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var qr = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using (var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                await ms.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(d.UpiQrPng()));
                ms.Seek(0);
                await qr.SetSourceAsync(ms);
            }
            var qrImage = new Image { Source = qr, Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(qrImage, $"UPI QR code for {d.UpiId}");
            body.Children.Add(new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center,
                Child = qrImage,
            });
            body.Children.Add(new TextBlock
            {
                Text = "Scan with any UPI app — Google Pay, PhonePe, Paytm or BHIM.", FontSize = 12,
                TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
            });
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var id = new TextBox { Text = d.UpiId, IsReadOnly = true };
            AutomationProperties.SetName(id, "UPI ID");
            var copy = new Button { Content = "Copy" };
            copy.Click += (_, _) =>
            {
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(d.UpiId);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                copy.Content = "Copied";
            };
            Grid.SetColumn(copy, 1);
            row.Children.Add(id); row.Children.Add(copy);
            body.Children.Add(row);
        }

        var dialog = new ContentDialog
        {
            Title = "Support KISSIK",
            Content = new ScrollViewer { Content = body },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    /// <summary>Says plainly when the camera can't reach the chosen quality, instead of capping it silently.</summary>
    private string LimitNote(CaptureMode? mode)
    {
        if (mode is not { } m) return "";
        var notes = new List<string>();
        if (m.Height < _profile.Height) notes.Add($"this camera supports up to {m.Height}p, so the picture is scaled up to {_profile.Height}p");
        if (m.Fps > 0 && m.Fps + 0.5 < _profile.Fps) notes.Add($"it runs at {m.Fps:0} fps, not {_profile.Fps}");
        return notes.Count == 0 ? "" : ". Note: " + string.Join("; ", notes) + ". Every quality is free; this is the camera's own limit.";
    }

    /// <summary>Every third saved recording, a quiet thank-you line pointing at Support. Never a pop-up.</summary>
    private string SupportNudge()
    {
        _settings.RecordingsSaved++; _settings.Save();
        return _donation.IsConfigured && _settings.RecordingsSaved % 3 == 0
            ? " · Enjoying KISSIK? It's free and built by one person. Support it with the 🙏 button."
            : "";
    }

    private string RecordingFolder() => string.IsNullOrWhiteSpace(_settings.RecordingFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "OpenKamera")
        : _settings.RecordingFolder;

    /// <summary>Joins pieces left by a recording that was cut off by a crash or power loss.</summary>
    private async Task RecoverRecordingsAsync()
    {
        try
        {
            var saved = await SegmentedRecorder.RecoverAsync(RecordingFolder());
            if (saved.Count > 0) Status($"Recovered {saved.Count} interrupted recording(s): {string.Join(", ", saved.Select(Path.GetFileName))}");
        }
        catch { /* recovery is best effort */ }
    }

    private async Task StopRecordingAsync()
    {
        var rec = _recorder;
        if (rec is null) return;
        _recorder = null; // stop feeding first
        _recordClock.Stop();
        _recPaused = false; PauseBtn.Visibility = Visibility.Collapsed;
        RecordLabel.Text = "Record"; RecordIcon.Glyph = "";
        RecordBtn.ClearValue(Control.BackgroundProperty);
        RecordBtn.ClearValue(Control.ForegroundProperty);
        RecPill.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(RecordBtn, "● Record");
        Status("Finishing recording… joining the pieces");
        try
        {
            await rec.StopAsync();
            Status($"Recording saved: {rec.Path} — {rec.FramesWritten} frames, {_recordClock.Elapsed:mm\\:ss}" +
                   (rec.FramesDropped > 0 ? $", {rec.FramesDropped} dropped (encoder busy)" : "") + SupportNudge());
        }
        catch (Exception ex) { Status("Recording failed while saving: " + ex.Message); }
        finally { await rec.DisposeAsync(); }
    }
}
