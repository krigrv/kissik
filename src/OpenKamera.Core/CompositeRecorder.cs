using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace OpenKamera.Core;

/// <summary>
/// Records processed frames (exactly what the preview shows) to an H.264 MP4.
/// Frames are pushed from any thread with their capture time; the encoder pulls them in order.
/// </summary>
public sealed class CompositeRecorder : IAsyncDisposable
{
    private readonly BlockingCollection<(byte[] Pixels, TimeSpan Time)> _queue = new(boundedCapacity: 45);
    private MediaStreamSource? _source;
    private Task? _transcode;
    private TimeSpan? _first;
    private TimeSpan _lastTime = TimeSpan.Zero;
    private volatile bool _stopping;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public string Path { get; private set; } = "";
    public int FramesWritten { get; private set; }
    public int FramesDropped { get; private set; }
    public bool IsRecording => _transcode is { IsCompleted: false };

    public CompositeRecorder(int width, int height, int fps)
    {
        // H.264 needs even dimensions.
        Width = width & ~1; Height = height & ~1; Fps = Math.Max(1, fps);
    }

    /// <summary>Bitrate tuned for screen-sharing quality: ~0.1 bits per pixel per frame, 2–40 Mbps.</summary>
    public static uint BitrateFor(int w, int h, int fps) => (uint)Math.Clamp(w * (long)h * fps / 10, 2_000_000, 40_000_000);

    public async Task StartAsync(string path)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(System.IO.Path.GetFileName(path), CreationCollisionOption.GenerateUniqueName);
        Path = file.Path;

        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)Width, (uint)Height);
        input.FrameRate.Numerator = (uint)Fps; input.FrameRate.Denominator = 1;
        _source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero, CanSeek = false };
        _source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video!.Width = (uint)Width; profile.Video.Height = (uint)Height;
        profile.Video.FrameRate.Numerator = (uint)Fps; profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = BitrateFor(Width, Height, Fps);

        var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(_source, stream, profile);
        if (!prepared.CanTranscode)
        {
            stream.Dispose();
            throw new InvalidOperationException($"Encoder unavailable: {prepared.FailureReason}");
        }
        _transcode = Task.Run(async () =>
        {
            try { await prepared.TranscodeAsync(); }
            finally { stream.Dispose(); }
        });
    }

    /// <summary>Queues a frame. Frames of the wrong size are dropped (the profile changed mid-recording).</summary>
    public void Push(byte[] bgra, int width, int height, TimeSpan captureTime)
    {
        if (_stopping || _transcode is null) return;
        if (width != Width || height != Height)
        {
            if (width >= Width && height >= Height) bgra = Crop(bgra, width, Width, Height); // odd → even trim
            else { FramesDropped++; return; }
        }
        _first ??= captureTime;
        var t = captureTime - _first.Value;
        if (t <= _lastTime && FramesWritten + _queue.Count > 0) t = _lastTime + TimeSpan.FromMilliseconds(1);
        _lastTime = t;
        if (!_queue.TryAdd(((byte[])bgra.Clone(), t))) FramesDropped++; // encoder behind: drop, never block capture
    }

    private static byte[] Crop(byte[] src, int srcW, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(src, y * srcW * 4, dst, y * w * 4, w * 4);
        return dst;
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        _ = Task.Run(() =>
        {
            try
            {
                // Wait for the next frame; null sample = end of stream.
                while (true)
                {
                    if (_queue.TryTake(out var frame, 100))
                    {
                        var sample = MediaStreamSample.CreateFromBuffer(frame.Pixels.AsBuffer(), frame.Time);
                        sample.Duration = TimeSpan.FromSeconds(1.0 / Fps);
                        sample.KeyFrame = FramesWritten % (Fps * 2) == 0;
                        args.Request.Sample = sample;
                        FramesWritten++;
                        return;
                    }
                    if (_stopping) { args.Request.Sample = null; return; }
                }
            }
            finally { deferral.Complete(); }
        });
    }

    public async Task StopAsync()
    {
        if (_transcode is null) return;
        _stopping = true;
        try { await _transcode; }
        finally
        {
            if (_source is not null) _source.SampleRequested -= OnSampleRequested;
            _transcode = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _queue.Dispose();
    }
}
