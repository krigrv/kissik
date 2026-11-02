using System.IO.Compression;
using System.Text.Json;
using Vosk;

namespace OpenKamera.Core;

/// <summary>An offline speech model (Vosk, Apache-2.0). Downloaded once, then everything stays on this PC.</summary>
public sealed record CaptionLanguage(string Code, string Name, string Url, int SizeMb);

/// <summary>
/// Live captions with no internet: the audio mix is turned into text on this PC by a small Vosk model.
/// Feed it the 48 kHz stereo mix; it raises partial words as you speak and the finished sentence after a pause.
/// </summary>
public sealed class OfflineCaptions : IDisposable
{
    public static IReadOnlyList<CaptionLanguage> Languages { get; } =
    [
        new("en-us", "English", "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip", 40),
        new("en-in", "English (India)", "https://alphacephei.com/vosk/models/vosk-model-small-en-in-0.4.zip", 36),
        new("hi", "Hindi", "https://alphacephei.com/vosk/models/vosk-model-small-hi-0.22.zip", 42),
        new("es", "Spanish", "https://alphacephei.com/vosk/models/vosk-model-small-es-0.42.zip", 39),
        new("fr", "French", "https://alphacephei.com/vosk/models/vosk-model-small-fr-0.22.zip", 41),
        new("de", "German", "https://alphacephei.com/vosk/models/vosk-model-small-de-0.15.zip", 45),
    ];

    private readonly Model _model;
    private readonly VoskRecognizer _rec;
    private readonly object _lock = new();
    private readonly System.Collections.Concurrent.BlockingCollection<float[]> _queue = new(boundedCapacity: 50);
    private readonly Thread _worker;
    private volatile bool _disposed;

    public event Action<string>? Partial;
    public event Action<string>? Final;

    static OfflineCaptions() { try { Vosk.Vosk.SetLogLevel(-1); } catch { } }

    public OfflineCaptions(string modelDir)
    {
        _model = new Model(modelDir);
        _rec = new VoskRecognizer(_model, 16000f);
        _rec.SetMaxAlternatives(0);
        _rec.SetWords(false);
        // One worker owns the recognizer: audio callbacks only enqueue, so they never block and never race Dispose.
        _worker = new Thread(() => { foreach (var block in _queue.GetConsumingEnumerable()) { try { Feed(block); } catch { } } })
        { IsBackground = true, Name = "KISSIK captions" };
        _worker.Start();
    }

    /// <summary>From the audio thread: queue a block (dropped if speech recognition is far behind).</summary>
    public void Enqueue(float[] stereo48k) { if (!_disposed) _queue.TryAdd(stereo48k); }

    /// <summary>Folder that holds the downloaded model for <paramref name="code"/>, or null if not downloaded yet.</summary>
    public static string? ModelDir(string root, string code)
    {
        string dir = Path.Combine(root, code);
        if (!Directory.Exists(dir)) return null;
        // The zip holds one top folder (e.g. vosk-model-small-en-us-0.15) with am/, conf/, graph/.
        return Directory.EnumerateDirectories(dir).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "am")))
            ?? (Directory.Exists(Path.Combine(dir, "am")) ? dir : null);
    }

    /// <summary>Downloads and unpacks a model (reports 0..1). Only from the URLs in <see cref="Languages"/>.</summary>
    public static async Task<string> DownloadAsync(CaptionLanguage lang, string root, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Languages.Contains(lang)) throw new ArgumentException("Unknown model.");
        string dir = Path.Combine(root, lang.Code), zip = Path.Combine(root, lang.Code + ".zip");
        Directory.CreateDirectory(root);
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var resp = await http.GetAsync(lang.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? lang.SizeMb * 1_048_576L;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n; progress?.Report(Math.Min(1, done / (double)total)); }
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ZipFile.ExtractToDirectory(zip, dir);
        File.Delete(zip);
        return ModelDir(root, lang.Code) ?? throw new InvalidDataException("The download didn't contain a speech model.");
    }

    /// <summary>Feeds 48 kHz stereo floats (the audio mix).</summary>
    public void Feed(float[] stereo48k)
    {
        var pcm = ToPcm16Mono16k(stereo48k);
        if (pcm.Length == 0) return;
        string? fin = null, part = null;
        lock (_lock)
        {
            if (_disposed) return;
            if (_rec.AcceptWaveform(pcm, pcm.Length)) fin = Text(_rec.Result(), "text");
            else part = Text(_rec.PartialResult(), "partial");
        }
        if (!string.IsNullOrWhiteSpace(fin)) Final?.Invoke(fin);
        else if (!string.IsNullOrWhiteSpace(part)) Partial?.Invoke(part);
    }

    /// <summary>48 kHz stereo float → 16 kHz mono 16-bit (average 3 frames, both channels).</summary>
    public static byte[] ToPcm16Mono16k(float[] stereo)
    {
        int frames = stereo.Length / 2, outN = frames / 3;
        var bytes = new byte[outN * 2];
        for (int o = 0; o < outN; o++)
        {
            float sum = 0;
            for (int k = 0; k < 3; k++) { int i = (o * 3 + k) * 2; sum += stereo[i] + stereo[i + 1]; }
            short v = (short)Math.Clamp(sum / 6f * 32767f, short.MinValue, short.MaxValue);
            bytes[2 * o] = (byte)v; bytes[2 * o + 1] = (byte)(v >> 8);
        }
        return bytes;
    }

    private static string? Text(string json, string field)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty(field, out var t) ? t.GetString() : null; }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(3)); // let the current block finish before freeing native memory
        lock (_lock) { _rec.Dispose(); _model.Dispose(); }
    }
}
