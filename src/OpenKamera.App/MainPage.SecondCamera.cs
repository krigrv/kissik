using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;
using Windows.Graphics.Imaging;

namespace OpenKamera_App;

// Second camera in a scene (picture-in-picture): another webcam or the phone, shown in a box or circle over the
// main camera — e.g. an overhead view of your desk while you talk. Drawn into every frame, so the preview,
// meeting camera, recordings and streams all match.
public sealed partial class MainPage
{
    private CaptureSession? _second;
    private string? _secondSource;
    private sealed record SourceFrame(byte[] Px, int W, int H);
    private volatile SourceFrame? _secondFrame;
    private int _secondBusy;
    private DateTime _secondAt;

    private async Task AddSecondCameraAsync()
    {
        var cams = (DeviceBox.ItemsSource as IEnumerable<CameraDeviceInfo> ?? []).Where(c => (DeviceBox.SelectedItem as CameraDeviceInfo)?.Id != c.Id).ToList();
        if (cams.Count == 0) { Status("Connect another webcam, or pair your phone, to add a second camera."); return; }
        var pick = new ComboBox { ItemsSource = cams, DisplayMemberPath = nameof(CameraDeviceInfo.Name), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pick, "Second camera");
        var res = await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Add a second camera", Content = pick,
            PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();
        if (res != ContentDialogResult.Primary || pick.SelectedItem is not CameraDeviceInfo cam) return;
        BeginEdit();
        _scene.Layers.Add(new SceneLayer { Kind = "camera2", Name = cam.Name, Source = cam.Id, X = 0.66, Y = 0.06, W = 0.3, H = 0.3, CornerRadius = 0.02, Stroke = "#FFFFFFFF", StrokeWidth = 0.004 });
        Commit();
        await EnsureSecondCameraAsync();
    }

    /// <summary>Starts or stops the second source to match the current scene.</summary>
    private async Task EnsureSecondCameraAsync()
    {
        string? want = _scene.Layers.FirstOrDefault(l => l.Kind == "camera2" && l.Visible)?.Source;
        if (want == _secondSource) return;
        if (_second is { } old) { _second = null; await old.DisposeAsync(); }
        _secondSource = want; _secondFrame = null;
        if (want is null || want == PhoneDevice.Id) return; // the phone's frames are routed in by the phone server
        try
        {
            var s = new CaptureSession();
            s.FrameAvailable += OnSecondFrame;
            await s.StartAsync(want, OutputProfile.P720);
            _second = s;
        }
        catch (Exception ex) { _secondSource = null; Status("The second camera couldn't start: " + ex.Message); }
    }

    /// <summary>Second webcam frame (its own thread). Kept at most ~15 times a second, at most 960 px wide.</summary>
    private void OnSecondFrame(SoftwareBitmap bmp)
    {
        var now = DateTime.UtcNow;
        if (now - _secondAt < TimeSpan.FromMilliseconds(60) || Interlocked.Exchange(ref _secondBusy, 1) == 1) return;
        _secondAt = now;
        try
        {
            var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(px));
            var (sp, sw, sh) = Shrink(px, bmp.PixelWidth, bmp.PixelHeight); _secondFrame = new SourceFrame(sp, sw, sh);
        }
        catch { }
        finally { Volatile.Write(ref _secondBusy, 0); }
    }

    /// <summary>Phone frames when the phone is the second camera (decoded here, not by the main session).</summary>
    private async Task OnSecondPhoneJpegAsync(byte[] jpeg)
    {
        if (_secondSource != PhoneDevice.Id || Interlocked.Exchange(ref _secondBusy, 1) == 1) return;
        try
        {
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ms.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(jpeg));
            ms.Seek(0);
            var dec = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, ms);
            using var bmp = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(px));
            var (sp, sw, sh) = Shrink(px, bmp.PixelWidth, bmp.PixelHeight); _secondFrame = new SourceFrame(sp, sw, sh);
        }
        catch { }
        finally { Volatile.Write(ref _secondBusy, 0); }
    }

    private static (byte[], int, int) Shrink(byte[] px, int w, int h)
    {
        if (w <= 960) return (px, w, h);
        int nh = (int)Math.Round(h * 960.0 / w);
        return (OutputFit.Stretch(px, w, h, 960, nh).Pixels, 960, nh);
    }

    /// <summary>Frame thread: draws the second camera into the processed frame.</summary>
    private void DrawSecondCamera(byte[] pixels, int w, int h)
    {
        if (_secondFrame is not { } f || _sceneSnapshot is not { } scene) return;
        foreach (var l in scene.Layers)
            if (l.Kind == "camera2" && l.Visible && l.Source == _secondSource)
                SceneCompositor.PlaceSource(pixels, w, h, f.Px, f.W, f.H, l);
    }
}
