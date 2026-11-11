using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace OpenKamera.Core;

/// <summary>
/// Records processed frames (exactly what the preview shows) to an H.264 MP4, plus an optional AAC track
/// from the audio mix. Frames and audio are pushed from any thread with times on the same clock; the encoder
/// pulls them in order.
/// </summary>
public sealed class CompositeRecorder : IRecorderPart
{
    private readonly BlockingCollection<(byte[] Pixels, TimeSpan Time)> _queue = new(boundedCapacity: 45);
    private readonly BlockingCollection<(byte[] Pcm, TimeSpan Time, TimeSpan Duration)> _audio = new(boundedCapacity: 400);
    private VideoStreamDescriptor? _videoStream;
    private AudioStreamDescriptor? _audioStream;
    private readonly AudioTimeline _timeline = new();
    private readonly object _audioLock = new();
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
    public bool HasAudio { get; }
    public int AudioBlocksWritten { get; private set; }

    public CompositeRecorder(int width, int height, int fps, bool withAudio = false)
    {
        HasAudio = withAudio;
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
        _videoStream = new VideoStreamDescriptor(input);
        if (HasAudio)
        {
            var pcm = AudioEncodingProperties.CreatePcm(AudioEngine.SampleRate, AudioEngine.Channels, 32);
            pcm.Subtype = MediaEncodingSubtypes.Float;
            _audioStream = new AudioStreamDescriptor(pcm);
            _source = new MediaStreamSource(_videoStream, _audioStream);
        }
        else _source = new MediaStreamSource(_videoStream);
        _source.BufferTime = TimeSpan.Zero; _source.CanSeek = false;
        _source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = HasAudio ? AudioEncodingProperties.CreateAac(AudioEngine.SampleRate, AudioEngine.Channels, 192_000) : null;
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

    public bool TakesTextures => false;

    /// <summary>This recorder only takes bytes: a texture is dropped (and disposed).</summary>
    public void PushTexture(GpuFrame frame, TimeSpan captureTime) { FramesDropped++; frame.Dispose(); }

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

    /// <summary>
    /// Queues interleaved stereo float samples (48 kHz). Audio is laid end to end from the first video frame,
    /// so it never drifts from its own clock; <paramref name="captureTime"/> only anchors the start.
    /// </summary>
    public void PushAudio(float[] interleaved, TimeSpan captureTime)
    {
        if (!HasAudio || _stopping || _transcode is null || interleaved.Length == 0) return;
        if (_first is null) return; // start with the first video frame so both tracks begin together
        var duration = TimeSpan.FromSeconds(interleaved.Length / (double)AudioEngine.Channels / AudioEngine.SampleRate);
        var bytes = new byte[interleaved.Length * sizeof(float)];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
        lock (_audioLock)
        {
            // Placed by the recording clock (see AudioTimeline): a break in the sound becomes silence of the same
            // length instead of pulling everything after it earlier.
            if (!_timeline.Place(captureTime - _first.Value, out var gap)) return;
            if (gap > TimeSpan.Zero)
            {
                int frames = (int)Math.Round(gap.TotalSeconds * AudioEngine.SampleRate);
                var silent = TimeSpan.FromSeconds(frames / (double)AudioEngine.SampleRate);
                if (frames > 0 && _audio.TryAdd((new byte[frames * AudioEngine.Channels * sizeof(float)], _timeline.End, silent))) _timeline.Advance(silent);
            }
            if (_audio.TryAdd((bytes, _timeline.End, duration))) _timeline.Advance(duration);
        }
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
        // Compare by type: the projected descriptor object is not reference-equal to ours.
        if (_audioStream is not null && args.Request.StreamDescriptor is AudioStreamDescriptor)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        if (_audio.TryTake(out var block, 100))
                        {
                            var sample = MediaStreamSample.CreateFromBuffer(block.Pcm.AsBuffer(), block.Time);
                            sample.Duration = block.Duration;
                            args.Request.Sample = sample;
                            AudioBlocksWritten++;
                            return;
                        }
                        if (_stopping) { args.Request.Sample = null; return; }
                    }
                }
                finally { deferral.Complete(); }
            });
            return;
        }
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
        _audio.Dispose();
    }
}
