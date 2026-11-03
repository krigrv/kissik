using Microsoft.UI.Xaml;
using OpenKamera.Core;
using Windows.Graphics.Imaging;

namespace OpenKamera_App;

// "Picture or slide" source: a still image used as the camera (a slide, a holding card, a photo), with every effect,
// scene and output working on it as on a real camera.
public sealed partial class MainPage
{
    private static readonly CameraDeviceInfo PictureDevice = new("openkamera:picture", "Picture or slide");
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pictureTimer;

    /// <summary>Asks for an image and feeds it to the session at the profile's frame rate. False when cancelled.</summary>
    private async Task<bool> StartPictureAsync(CaptureSession session)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return false;

        // Decoded once, scaled to cover the output size, then pushed like camera frames.
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        int w = _profile.Width, h = _profile.Height;
        double scale = Math.Max((double)w / decoder.PixelWidth, (double)h / decoder.PixelHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Ceiling(decoder.PixelWidth * scale), ScaledHeight = (uint)Math.Ceiling(decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        transform.Bounds = new BitmapBounds
        {
            X = (transform.ScaledWidth - (uint)w) / 2, Y = (transform.ScaledHeight - (uint)h) / 2, Width = (uint)w, Height = (uint)h,
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        byte[] pixels = data.DetachPixelData();

        _pictureTimer?.Stop();
        _pictureTimer = DispatcherQueue.CreateTimer();
        _pictureTimer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _profile.Fps));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _pictureTimer.Tick += (_, _) =>
        {
            if (_session != session) { _pictureTimer?.Stop(); return; }
            session.PushBgra(new DecodedFrame(pixels, w, h, clock.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency));
        };
        _pictureTimer.Start();
        Status($"Showing {file.Name} as the camera.");
        return true;
    }

    private void StopPicture() { _pictureTimer?.Stop(); _pictureTimer = null; }
}
