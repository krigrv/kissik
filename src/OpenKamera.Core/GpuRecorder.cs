using System.Collections.Concurrent;
using Vortice.MediaFoundation;

namespace OpenKamera.Core;

/// <summary>
/// One recording part as H.264 + AAC in an MP4, written by Media Foundation's sink writer on the GPU frame path's own
/// Direct3D device: frames handed over as textures (<see cref="PushTexture"/>) go to the hardware encoder without
/// coming back to the CPU, and the GPU converts them to the encoder's format. Frames as bytes (<see cref="Push"/>)
/// work too. Same timing rules as <see cref="CompositeRecorder"/>: video starts the clock, audio is laid end to end.
/// </summary>
public sealed class GpuRecorder : IRecorderPart
{
    private readonly BlockingCollection<(object Frame, TimeSpan Time)> _video = new(boundedCapacity: 8);
    private readonly BlockingCollection<(short[] Pcm, TimeSpan Time, TimeSpan Duration)> _audio = new(boundedCapacity: 400);
    private IMFSinkWriter? _writer;
    private IMFDXGIDeviceManager? _manager;
    private Thread? _thread;
    private int _videoStream, _audioStream = -1;
    private TimeSpan? _first;
    private TimeSpan _lastTime, _audioNext;
    private long _queued;
    private volatile bool _stopping;
    private Exception? _failure;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public bool HasAudio { get; }
    public string Path { get; private set; } = "";
    public int FramesWritten { get; private set; }
    public int FramesDropped { get; private set; }
    public int AudioBlocksWritten { get; private set; }
    public bool TakesTextures => true;

    public GpuRecorder(int width, int height, int fps, bool withAudio = false)
    {
        Width = width & ~1; Height = height & ~1; Fps = Math.Max(1, fps); HasAudio = withAudio;
    }

    /// <summary>True when there is a GPU whose device can feed a hardware encoder.</summary>
    public static bool Available => FrameProcessor.GpuActive && GpuEffects.Shared is { VideoSupport: true };

    private static int _mfStarted;

    public Task StartAsync(string path)
    {
        var gpu = GpuEffects.Shared ?? throw new InvalidOperationException("No GPU.");
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0) MediaFactory.MFStartup(true).CheckError();
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        _manager = MediaFactory.MFCreateDXGIDeviceManager();
        _manager.ResetDevice(gpu.Device);
        using var attrs = MediaFactory.MFCreateAttributes(4);
        attrs.Set(SinkWriterAttributeKeys.D3DManager, _manager);
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attrs);

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, CompositeRecorder.BitrateFor(Width, Height, Fps));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
            SetSizeAndRate(output);
            _videoStream = _writer.AddStream(output);
        }
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(Width * 4)); // top-down rows
            SetSizeAndRate(input);
            _writer.SetInputMediaType(_videoStream, input, null);
        }
        if (HasAudio)
        {
            using var output = MediaFactory.MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioEngine.SampleRate);
            output.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioEngine.Channels);
            output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24_000u); // 192 kbps
            _audioStream = _writer.AddStream(output);
            using var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioEngine.SampleRate);
            input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioEngine.Channels);
            input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(2 * AudioEngine.Channels));
            input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(2 * AudioEngine.Channels * AudioEngine.SampleRate));
            _writer.SetInputMediaType(_audioStream, input, null);
        }
        _writer.BeginWriting();
        _thread = new Thread(Run) { IsBackground = true, Name = "GPU recorder" };
        _thread.Start();
        return Task.CompletedTask;
    }

    private void SetSizeAndRate(IMFMediaType t)
    {
        t.Set(MediaTypeAttributeKeys.FrameSize, (ulong)(uint)Width << 32 | (uint)Height);
        t.Set(MediaTypeAttributeKeys.FrameRate, (ulong)(uint)Fps << 32 | 1u);
        t.Set(MediaTypeAttributeKeys.PixelAspectRatio, 1ul << 32 | 1u);
    }

    /// <summary>Queues a GPU frame; this recorder now owns it (disposes it after encoding, or when dropped).</summary>
    public void PushTexture(GpuFrame frame, TimeSpan captureTime)
    {
        if (frame.Width != Width || frame.Height != Height || !Queue(frame, captureTime)) { FramesDropped++; frame.Dispose(); }
    }

    /// <summary>Queues a BGRA frame. Odd sizes are trimmed to even; smaller frames are dropped.</summary>
    public void Push(byte[] bgra, int width, int height, TimeSpan captureTime)
    {
        if (width < Width || height < Height || bgra.Length < width * height * 4) { FramesDropped++; return; }
        var copy = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++) Buffer.BlockCopy(bgra, y * width * 4, copy, y * Width * 4, Width * 4);
        if (!Queue(copy, captureTime)) FramesDropped++;
    }

    private bool Queue(object frame, TimeSpan captureTime)
    {
        if (_stopping || _writer is null) return false;
        _first ??= captureTime;
        var t = captureTime - _first.Value;
        // On the frame-rate grid, one slot after the last at least: the hardware encoder drops frames that come
        // closer together than a frame (capture jitter cost ~3% of them), and a skipped camera frame stays a gap.
        long slot = TimeSpan.FromSeconds(1.0 / Fps).Ticks;
        t = TimeSpan.FromTicks((t.Ticks + slot / 2) / slot * slot);
        if (t <= _lastTime && _queued > 0) t = _lastTime + TimeSpan.FromTicks(slot);
        _queued++;
        _lastTime = t;
        return _video.TryAdd((frame, t)); // encoder behind: drop, never block capture
    }

    /// <summary>Queues interleaved stereo float samples (48 kHz), converted to 16-bit for the AAC encoder.</summary>
    public void PushAudio(float[] interleaved, TimeSpan captureTime)
    {
        if (!HasAudio || _stopping || _writer is null || interleaved.Length == 0 || _first is null) return;
        if (_audioNext == TimeSpan.Zero) _audioNext = captureTime - _first.Value;
        if (_audioNext < TimeSpan.Zero) _audioNext = TimeSpan.Zero;
        var duration = TimeSpan.FromSeconds(interleaved.Length / (double)AudioEngine.Channels / AudioEngine.SampleRate);
        var pcm = new short[interleaved.Length];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp((int)MathF.Round(interleaved[i] * 32767f), short.MinValue, short.MaxValue);
        if (_audio.TryAdd((pcm, _audioNext, duration))) _audioNext += duration;
    }

    private void Run()
    {
        var frameTime = TimeSpan.FromSeconds(1.0 / Fps);
        try
        {
            while (true)
            {
                bool any = false;
                while (_audio.TryTake(out var a)) { WriteAudio(a.Pcm, a.Time, a.Duration); any = true; }
                if (_video.TryTake(out var v, 20))
                {
                    try { WriteVideo(v.Frame, v.Time, frameTime); _videoEnd = v.Time + frameTime; }
                    finally { (v.Frame as IDisposable)?.Dispose(); }
                    FramesWritten++;
                    any = true;
                    // The MP4 writer holds video back until there's audio to interleave it with: if the mix goes quiet
                    // or falls behind, fill the gap with silence so video never waits (it used to drop to ~1 fps).
                    if (HasAudio && _audio.Count == 0 && v.Time - _audioEnd > AudioSlack) WriteSilence(v.Time - AudioSlack / 2);
                }
                if (!any && _stopping && _video.Count == 0 && _audio.Count == 0)
                {
                    // The MP4 ends where its shortest track does: make the audio reach the last frame.
                    if (HasAudio) WriteSilence(_videoEnd);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _failure = ex;
            while (_video.TryTake(out var v)) (v.Frame as IDisposable)?.Dispose();
        }
    }

    private void WriteVideo(object frame, TimeSpan time, TimeSpan duration)
    {
        using var sample = MediaFactory.MFCreateSample();
        IMFMediaBuffer buffer;
        if (frame is GpuFrame g)
        {
            buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID, g.Texture, 0, false);
            // DXGI buffers start empty; the whole frame is valid.
            using var two = buffer.QueryInterface<IMF2DBuffer>();
            buffer.CurrentLength = two.ContiguousLength;
        }
        else
        {
            var px = (byte[])frame;
            buffer = MediaFactory.MFCreateMemoryBuffer(px.Length);
            buffer.Lock(out nint ptr, out _, out _);
            try { System.Runtime.InteropServices.Marshal.Copy(px, 0, ptr, px.Length); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = px.Length;
        }
        using (buffer) sample.AddBuffer(buffer);
        sample.SampleTime = time.Ticks;
        sample.SampleDuration = duration.Ticks;
        _writer!.WriteSample(_videoStream, sample);
    }

    /// <summary>How far the audio track may trail the video before silence fills the gap.</summary>
    private static readonly TimeSpan AudioSlack = TimeSpan.FromMilliseconds(250);
    private TimeSpan _audioEnd; // end of the audio written so far
    private TimeSpan _videoEnd; // end of the last frame written
    private short[]? _silence;

    /// <summary>Writes silence from the end of the audio track up to <paramref name="until"/>, in 10 ms blocks.</summary>
    private void WriteSilence(TimeSpan until)
    {
        int perBlock = AudioEngine.SampleRate / 100 * AudioEngine.Channels;
        _silence ??= new short[perBlock];
        var block = TimeSpan.FromMilliseconds(10);
        while (_audioEnd < until) WriteAudio(_silence, _audioEnd, block);
    }

    private void WriteAudio(short[] pcm, TimeSpan time, TimeSpan duration)
    {
        // Audio that comes back after a gap lands after what's written (timestamps must only go forward).
        if (time < _audioEnd) time = _audioEnd;
        _audioEnd = time + duration;
        int bytes = pcm.Length * 2;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        buffer.Lock(out nint ptr, out _, out _);
        try { System.Runtime.InteropServices.Marshal.Copy(pcm, 0, ptr, pcm.Length); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = bytes;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time.Ticks;
        sample.SampleDuration = duration.Ticks;
        _writer!.WriteSample(_audioStream, sample);
        AudioBlocksWritten++;
    }

    public async Task StopAsync()
    {
        if (_writer is null) return;
        _stopping = true;
        if (_thread is { } t) await Task.Run(t.Join);
        try { if (FramesWritten > 0) _writer.Finalize(); }
        finally
        {
            _writer.Dispose(); _writer = null;
            _manager?.Dispose(); _manager = null;
        }
        if (_failure is { } f) throw new InvalidOperationException("Recording failed: " + f.Message, f);
        if (FramesWritten == 0) throw new InvalidOperationException("Nothing was recorded.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { /* the caller already heard about it from StopAsync */ }
        while (_video.TryTake(out var v)) (v.Frame as IDisposable)?.Dispose();
        _video.Dispose(); _audio.Dispose();
    }
}

/// <summary>One piece of a recording (see <see cref="SegmentedRecorder"/>): the Windows transcoder or the GPU sink writer.</summary>
public interface IRecorderPart : IAsyncDisposable
{
    int Width { get; }
    int Height { get; }
    string Path { get; }
    int FramesWritten { get; }
    int FramesDropped { get; }
    /// <summary>True when <see cref="PushTexture"/> works (frames stay on the GPU).</summary>
    bool TakesTextures { get; }
    Task StartAsync(string path);
    void Push(byte[] bgra, int width, int height, TimeSpan captureTime);
    void PushTexture(GpuFrame frame, TimeSpan captureTime);
    void PushAudio(float[] interleaved, TimeSpan captureTime);
    Task StopAsync();
}
