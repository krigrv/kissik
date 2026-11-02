using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace OpenKamera.Core;

/// <summary>
/// Crash-safe recording: writes short, finished MP4 pieces into "&lt;name&gt;.parts" and joins them into the final
/// file on Stop. If the app or PC dies, only the piece being written is lost; the rest are rescued on next launch
/// by <see cref="RecoverAsync"/>. Same Push/PushAudio API as <see cref="CompositeRecorder"/>.
/// </summary>
public sealed class SegmentedRecorder : IAsyncDisposable
{
    public const string PartsSuffix = ".parts";
    private readonly int _w, _h, _fps;
    private readonly bool _audio;
    private readonly List<Task> _finishing = [];
    private volatile IRecorderPart? _current;
    private int _rotating;
    private TimeSpan _partStart;
    private string _partsDir = "";
    private int _index;

    public TimeSpan SegmentLength { get; init; } = TimeSpan.FromSeconds(60);
    public string Path { get; private set; } = "";
    public int Width => _current?.Width ?? (_w & ~1);
    public int Height => _current?.Height ?? (_h & ~1);
    public int Fps => _fps;
    public int FramesWritten { get; private set; }
    public int FramesDropped { get; private set; }
    public int Parts => _index;

    public SegmentedRecorder(int width, int height, int fps, bool withAudio = false) { _w = width; _h = height; _fps = fps; _audio = withAudio; }

    /// <summary>Write pieces with the GPU's hardware encoder when there is one (frames can then stay on the GPU).</summary>
    public bool PreferGpu { get; init; } = true;

    /// <summary>True when the piece being written takes GPU frames (<see cref="PushTexture"/>).</summary>
    public bool TakesTextures => _current?.TakesTextures == true;

    /// <summary>True when the pieces are written by the GPU's encoder (shown in the status line).</summary>
    public bool OnGpu { get; private set; }

    public async Task StartAsync(string path)
    {
        Path = UniquePath(path);
        _partsDir = Path + PartsSuffix;
        Directory.CreateDirectory(_partsDir);
        _current = await NewPartAsync();
    }

    private async Task<IRecorderPart> NewPartAsync()
    {
        string path = System.IO.Path.Combine(_partsDir, $"part{++_index:D4}.mp4");
        if (PreferGpu && GpuRecorder.Available && !_gpuFailed)
        {
            var gpu = new GpuRecorder(_w, _h, _fps, _audio);
            try { await gpu.StartAsync(path); OnGpu = true; return gpu; }
            catch
            {
                // No hardware encoder for this size, or the driver said no: the Windows transcoder from here on.
                _gpuFailed = true;
                try { await gpu.DisposeAsync(); } catch { }
                try { File.Delete(path); } catch { }
            }
        }
        var rec = new CompositeRecorder(_w, _h, _fps, _audio);
        await rec.StartAsync(path);
        OnGpu = false;
        return rec;
    }

    private bool _gpuFailed;

    public void Push(byte[] bgra, int width, int height, TimeSpan captureTime)
    {
        var rec = _current;
        if (rec is null) return;
        if (rec.FramesWritten == 0 && _partStart == default) _partStart = captureTime;
        rec.Push(bgra, width, height, captureTime);
        Rotate(captureTime);
    }

    /// <summary>Queues a GPU frame; this recorder owns it from here (a piece that only takes bytes drops it).</summary>
    public void PushTexture(GpuFrame frame, TimeSpan captureTime)
    {
        var rec = _current;
        if (rec is null) { frame.Dispose(); return; }
        if (rec.FramesWritten == 0 && _partStart == default) _partStart = captureTime;
        rec.PushTexture(frame, captureTime);
        Rotate(captureTime);
    }

    private void Rotate(TimeSpan captureTime)
    {
        if (captureTime - _partStart >= SegmentLength && Interlocked.Exchange(ref _rotating, 1) == 0)
        {
            _partStart = captureTime;
            // Open the next piece first, then switch, so no frame is lost between pieces.
            _ = Task.Run(async () =>
            {
                try
                {
                    var next = await NewPartAsync();
                    var old = _current;
                    _current = next;
                    if (old is not null) lock (_finishing) _finishing.Add(FinishAsync(old));
                }
                catch { /* keep writing the current piece */ }
                finally { Volatile.Write(ref _rotating, 0); }
            });
        }
    }

    public void PushAudio(float[] interleaved, TimeSpan captureTime) => _current?.PushAudio(interleaved, captureTime);

    private async Task FinishAsync(IRecorderPart rec)
    {
        // A piece that started just before Stop can be empty; its failure must never lose the pieces before it.
        try { await rec.StopAsync(); }
        catch { try { if (new FileInfo(rec.Path) is { Exists: true, Length: < 1024 } f) f.Delete(); } catch { } }
        finally
        {
            FramesWritten += rec.FramesWritten; FramesDropped += rec.FramesDropped;
            try { await rec.DisposeAsync(); } catch { }
        }
    }

    /// <summary>Finishes the last piece and joins all pieces into <see cref="Path"/>. Pieces are kept if joining fails.</summary>
    public async Task StopAsync()
    {
        var last = _current; _current = null;
        while (Volatile.Read(ref _rotating) == 1) await Task.Delay(20);
        if (last is not null) lock (_finishing) _finishing.Add(FinishAsync(last));
        Task[] all; lock (_finishing) all = [.. _finishing];
        await Task.WhenAll(all);
        if (!await JoinAsync(_partsDir, Path, _w & ~1, _h & ~1, _fps))
            throw new InvalidOperationException("Nothing was recorded.");
    }

    /// <summary>Joins the playable pieces in <paramref name="partsDir"/> into <paramref name="output"/>, then deletes them.</summary>
    public static async Task<bool> JoinAsync(string partsDir, string output, int width = 0, int height = 0, int fps = 0)
    {
        var parts = Directory.Exists(partsDir) ? Directory.GetFiles(partsDir, "part*.mp4").Order().ToList() : [];
        var clips = new List<MediaClip>();
        foreach (var p in parts)
        {
            try
            {
                var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(p));
                if (clip.OriginalDuration > TimeSpan.Zero) clips.Add(clip);
            }
            catch { /* the piece being written when the app died: unreadable, skip it */ }
        }
        if (clips.Count == 0) return false;

        if (clips.Count == 1)
        {
            File.Move(parts.First(p => File.Exists(p)), output, overwrite: true);
        }
        else
        {
            var comp = new MediaComposition();
            foreach (var c in clips) comp.Clips.Add(c);
            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(output)!);
            var file = await folder.CreateFileAsync(System.IO.Path.GetFileName(output), CreationCollisionOption.ReplaceExisting);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            var props = clips[0].GetVideoEncodingProperties();
            profile.Video!.Width = width > 0 ? (uint)width : props.Width;
            profile.Video.Height = height > 0 ? (uint)height : props.Height;
            if (fps > 0) { profile.Video.FrameRate.Numerator = (uint)fps; profile.Video.FrameRate.Denominator = 1; }
            profile.Video.Bitrate = CompositeRecorder.BitrateFor((int)profile.Video.Width, (int)profile.Video.Height, Math.Max(1, fps > 0 ? fps : 30));
            var result = await comp.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile);
            if (result != TranscodeFailureReason.None) return false;
        }
        try { Directory.Delete(partsDir, recursive: true); } catch { /* leave pieces if something holds them */ }
        return true;
    }

    /// <summary>Finds recordings interrupted by a crash in <paramref name="folder"/> and joins what survived.</summary>
    public static async Task<List<string>> RecoverAsync(string folder)
    {
        var saved = new List<string>();
        if (!Directory.Exists(folder)) return saved;
        foreach (var dir in Directory.GetDirectories(folder, "*" + PartsSuffix))
        {
            string output = UniquePath(dir[..^PartsSuffix.Length].Replace(".mp4", " (recovered).mp4"));
            try { if (await JoinAsync(dir, output)) saved.Add(output); } catch { }
        }
        return saved;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path + PartsSuffix)) return path;
        string dir = System.IO.Path.GetDirectoryName(path)!, name = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p + PartsSuffix)) return p;
        }
    }

    public async ValueTask DisposeAsync() { if (_current is not null) await StopAsync(); }
}
