using System.Collections.Concurrent;
using System.Text;
using Whisper.net;

namespace OpenKamera.Core;

/// <summary>
/// "Accurate captions": the instant recogniser shows words live; when it finishes a sentence, this re-transcribes
/// that sentence's audio with Whisper (whisper.cpp, MIT) on this PC and reports a corrected line about a second later.
/// One worker thread, newest sentences first if it falls behind; nothing leaves the PC.
/// </summary>
public sealed class WhisperRefiner : IDisposable
{
    public const string ModelFile = "ggml-base.bin";
    public const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
    public const int ModelSizeMb = 142;

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly BlockingCollection<(string Draft, float[] Audio)> _queue = new(boundedCapacity: 4);
    private readonly Thread _worker;
    private volatile bool _disposed;

    /// <summary>(draft from the instant recogniser, corrected text). Raised on the worker thread.</summary>
    public event Action<string, string>? Corrected;

    public WhisperRefiner(string modelPath, string language = "en")
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder().WithLanguage(language).WithNoContext().Build();
        _worker = new Thread(Work) { IsBackground = true, Name = "Accurate captions", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public static string ModelPath(string speechRoot) => Path.Combine(speechRoot, "whisper", ModelFile);

    public static async Task<string> DownloadAsync(string speechRoot, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string path = ModelPath(speechRoot), part = path + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        using var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? ModelSizeMb * 1024L * 1024;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n; progress?.Report((double)done / total); }
        }
        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>Queues a finished sentence (16 kHz mono audio). If the worker is behind, the oldest waiting one is dropped.</summary>
    public void Refine(string draft, float[] audio16k)
    {
        if (_disposed || audio16k.Length < 16000 / 4) return; // under 0.25 s: nothing to gain
        while (!_queue.TryAdd((draft, audio16k))) _queue.TryTake(out _);
    }

    private void Work()
    {
        foreach (var (draft, audio) in _queue.GetConsumingEnumerable())
        {
            if (_disposed) break;
            try
            {
                var sb = new StringBuilder();
                foreach (var seg in _processor.ProcessAsync(audio).ToBlockingEnumerable()) sb.Append(seg.Text);
                string text = Clean(sb.ToString());
                if (text.Length > 0 && !string.Equals(text, draft, StringComparison.OrdinalIgnoreCase)) Corrected?.Invoke(draft, text);
            }
            catch { /* keep the draft */ }
        }
    }

    /// <summary>Drops Whisper's non-speech markers ("[BLANK_AUDIO]", "(music)") and tidies spaces.</summary>
    internal static string Clean(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]|\([^)]*\)", " ");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        _disposed = true;
        _queue.CompleteAdding();
        _worker.Join(3000);
        _processor.Dispose(); _factory.Dispose();
    }
}
