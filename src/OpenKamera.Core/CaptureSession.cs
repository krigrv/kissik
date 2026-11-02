using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace OpenKamera.Core;

/// <summary>Live capture: MediaCapture + frame reader (preview/snapshot). Recording is <see cref="CompositeRecorder"/>.</summary>
public sealed class CaptureSession : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _latest;

    public VideoDeviceController? Controls { get; private set; }

    /// <summary>The most recent raw frame (do not dispose; it is replaced as frames arrive).</summary>
    public SoftwareBitmap? Latest => _latest;
    public event Action<SoftwareBitmap>? FrameAvailable;
    /// <summary>The camera stopped working (unplugged, taken by another app, driver reset). Raised once, off the UI thread.</summary>
    public event Action<string>? Lost;
    private int _lost;

    /// <summary>The camera mode in use, e.g. "1920×1080 @ 30 fps NV12".</summary>
    public CaptureMode? Mode { get; private set; }

    public async Task StartAsync(string deviceId, OutputProfile? profile = null)
    {
        await StopAsync();
        _capture = new MediaCapture();
        await _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = deviceId,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            PhotoCaptureSource = PhotoCaptureSource.VideoPreview,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        });
        Controls = _capture.VideoDeviceController;
        _capture.Failed += (_, e) => { if (Interlocked.Exchange(ref _lost, 1) == 0) Lost?.Invoke(e.Message); };

        var source = _capture.FrameSources.Values
            .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            ?? _capture.FrameSources.Values.FirstOrDefault();
        if (source is null) throw new InvalidOperationException("Camera exposes no frame source.");

        var formats = source.SupportedFormats.Where(f => f.VideoFormat is not null).ToList();
        var modes = formats.Select(ToMode).ToList();
        int pick = FormatChooser.Choose(modes, profile ?? OutputProfile.P720);
        if (pick >= 0)
        {
            try { await source.SetFormatAsync(formats[pick]); }
            catch (Exception) { /* device refused; keep its current format */ }
        }
        Mode = source.CurrentFormat?.VideoFormat is not null ? ToMode(source.CurrentFormat) : null;
        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.FrameArrived += OnFrameArrived;
        await _reader.StartAsync();
    }

    /// <summary>Starts a session fed from outside (the phone) instead of a local camera.</summary>
    public async Task StartExternalAsync()
    {
        await StopAsync();
        _external = true;
    }

    private bool _external;
    private int _decoding;

    /// <summary>Decodes a JPEG from the phone and delivers it like a camera frame. Drops frames while busy (no lag build-up).</summary>
    public async Task PushJpegAsync(byte[] jpeg)
    {
        if (!_external || Interlocked.Exchange(ref _decoding, 1) == 1) return;
        try
        {
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ms.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(jpeg));
            ms.Seek(0);
            var dec = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, ms);
            var bmp = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            if (Mode is not { } m || m.Width != bmp.PixelWidth || m.Height != bmp.PixelHeight)
                Mode = new CaptureMode(bmp.PixelWidth, bmp.PixelHeight, 30, "PHONE JPEG");
            Interlocked.Exchange(ref _latest, bmp)?.Dispose();
            if (_external) FrameAvailable?.Invoke(bmp);
        }
        catch { /* a corrupt frame is skipped */ }
        finally { Volatile.Write(ref _decoding, 0); }
    }

    private static CaptureMode ToMode(MediaFrameFormat f) => new(
        (int)f.VideoFormat.Width, (int)f.VideoFormat.Height,
        f.FrameRate is { Denominator: > 0 } r ? (double)r.Numerator / r.Denominator : 0,
        f.Subtype.ToUpperInvariant());

    private async void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        if (frame?.VideoMediaFrame is not { } vmf) return;
        SoftwareBitmap? copy = null;
        try
        {
            if (vmf.SoftwareBitmap is { } bmp)
                copy = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            else if (vmf.Direct3DSurface is { } surface)
            {
                using var raw = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface);
                copy = SoftwareBitmap.Convert(raw, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            }
        }
        catch { /* frame raced with stop */ }
        if (copy is null) return;
        var old = Interlocked.Exchange(ref _latest, copy);
        old?.Dispose();
        FrameAvailable?.Invoke(copy);
    }

    public async Task SaveSnapshotAsync(string path, FrameRecipe recipe, BackgroundPipeline? background = null)
    {
        var bmp = _latest ?? throw new InvalidOperationException("No frame yet.");
        using var copy = FrameProcessor.Process(bmp, recipe, background);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.GenerateUniqueName);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId, stream);
        encoder.SetSoftwareBitmap(copy);
        await encoder.FlushAsync();
    }

    public async Task StopAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            await _reader.StopAsync();
            _reader.Dispose();
            _reader = null;
        }
        _external = false;
        _capture?.Dispose();
        _capture = null;
        Controls = null;
        Interlocked.Exchange(ref _latest, null)?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }
}
