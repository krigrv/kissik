namespace OpenKamera.Core;

/// <summary>
/// Keeps the last <see cref="Keep"/> of the output in short encoded pieces on disk, so a moment that already happened
/// can be saved after the fact ("save the last 30 seconds"). Older pieces are deleted as new ones finish.
/// </summary>
public sealed class ReplayBuffer : IAsyncDisposable
{
    public TimeSpan Keep { get; }
    public TimeSpan PieceLength { get; }
    private readonly string _dir;
    private readonly int _w, _h, _fps; private readonly bool _audio;
    private CompositeRecorder? _current;
    private int _index, _rotating;
    private TimeSpan _pieceStart = TimeSpan.MinValue;
    private readonly List<(string Path, Task Done)> _pieces = [];
    private readonly object _gate = new();

    public ReplayBuffer(string dir, int width, int height, int fps, bool withAudio, TimeSpan? keep = null, TimeSpan? piece = null)
    {
        _dir = dir; _w = width & ~1; _h = height & ~1; _fps = fps; _audio = withAudio;
        Keep = keep ?? TimeSpan.FromSeconds(30); PieceLength = piece ?? TimeSpan.FromSeconds(10);
    }

    public async Task StartAsync()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        Directory.CreateDirectory(_dir);
        _current = await NewPieceAsync();
    }

    private async Task<CompositeRecorder> NewPieceAsync()
    {
        var rec = new CompositeRecorder(_w, _h, _fps, _audio);
        await rec.StartAsync(Path.Combine(_dir, $"part{Interlocked.Increment(ref _index):D6}.mp4"));
        return rec;
    }

    public void Push(byte[] bgra, int w, int h, TimeSpan time)
    {
        var rec = _current;
        if (rec is null) return;
        if (_pieceStart == TimeSpan.MinValue) _pieceStart = time;
        rec.Push(bgra, w, h, time);
        // Retry on the next frame if a rotation is still opening the next piece, so pieces never run long.
        if (time - _pieceStart >= PieceLength && Volatile.Read(ref _rotating) == 0) { _pieceStart = time; _ = RotateAsync(); }
    }

    public void PushAudio(float[] interleaved, TimeSpan time) => _current?.PushAudio(interleaved, time);

    /// <summary>Starts a new piece and finishes the old one in the background, then drops pieces older than <see cref="Keep"/>.</summary>
    private async Task RotateAsync()
    {
        if (Interlocked.Exchange(ref _rotating, 1) == 1) return;
        try
        {
            var next = await NewPieceAsync();
            var old = Interlocked.Exchange(ref _current, next);
            if (old is not null) lock (_gate) _pieces.Add((old.Path, FinishAsync(old)));
            int max = (int)Math.Ceiling(Keep / PieceLength) + 1;
            List<(string Path, Task Done)> drop;
            lock (_gate) { drop = _pieces.Take(Math.Max(0, _pieces.Count - max)).ToList(); _pieces.RemoveRange(0, drop.Count); }
            foreach (var p in drop) { try { await p.Done; File.Delete(p.Path); } catch { } }
        }
        catch { /* keep writing the current piece */ }
        finally { Volatile.Write(ref _rotating, 0); }
    }

    private static async Task FinishAsync(CompositeRecorder rec)
    {
        try { await rec.StopAsync(); } catch { }
        try { await rec.DisposeAsync(); } catch { }
    }

    /// <summary>Saves what the buffer holds (about the last <see cref="Keep"/>) to <paramref name="output"/>. The buffer keeps running.</summary>
    public async Task<bool> SaveAsync(string output)
    {
        while (Volatile.Read(ref _rotating) == 1) await Task.Delay(20);
        await RotateAsync(); // close the piece being written so it can be included
        List<(string Path, Task Done)> pieces;
        lock (_gate) pieces = [.. _pieces];
        await Task.WhenAll(pieces.Select(p => p.Done));
        int want = (int)Math.Ceiling(Keep / PieceLength);
        var take = pieces.Skip(Math.Max(0, pieces.Count - want)).Where(p => File.Exists(p.Path)).ToList();
        if (take.Count == 0) return false;
        string stage = Path.Combine(Path.GetTempPath(), "KissikReplay-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stage);
        for (int i = 0; i < take.Count; i++) File.Copy(take[i].Path, Path.Combine(stage, $"part{i + 1:D4}.mp4"));
        return await SegmentedRecorder.JoinAsync(stage, output, _w, _h, _fps);
    }

    public async ValueTask DisposeAsync()
    {
        var last = Interlocked.Exchange(ref _current, null);
        if (last is not null) await FinishAsync(last);
        List<Task> all; lock (_gate) all = _pieces.Select(p => p.Done).ToList();
        try { await Task.WhenAll(all); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
