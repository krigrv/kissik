namespace OpenKamera.Core.Tests;

public class WhisperRefinerTests
{
    [Fact]
    public void CleanDropsNonSpeechMarkers() =>
        Assert.Equal("Hello there.", WhisperRefiner.Clean(" [BLANK_AUDIO] Hello  (music) there. "));

    [Fact]
    public void CaptionBufferSwapsInTheCorrectedLine()
    {
        var b = new CaptionBuffer();
        var now = DateTime.UtcNow;
        b.Final("revenue grew by eighty percent", now);
        b.Correct("revenue grew by eighty percent", "Revenue grew by 18%.");
        Assert.Equal("Revenue grew by 18%.", b.Text(now));
    }

    /// <summary>
    /// End to end with the real model, when a developer has it locally (set KISSIK_WHISPER_DIR to a folder holding
    /// ggml-base.bin and speech.wav, 16 kHz mono 16-bit). Skipped quietly otherwise, so CI needs no 142 MB download.
    /// </summary>
    [Fact]
    public async Task TranscribesRealSpeechWhenTheModelIsAvailable()
    {
        string? dir = Environment.GetEnvironmentVariable("KISSIK_WHISPER_DIR");
        if (dir is null || !File.Exists(Path.Combine(dir, "ggml-base.bin")) || !File.Exists(Path.Combine(dir, "speech.wav"))) return;
        var wav = await File.ReadAllBytesAsync(Path.Combine(dir, "speech.wav"));
        var audio = new float[(wav.Length - 44) / 2];
        for (int i = 0; i < audio.Length; i++) audio[i] = (short)(wav[44 + 2 * i] | wav[45 + 2 * i] << 8) / 32768f;
        using var r = new WhisperRefiner(Path.Combine(dir, "ggml-base.bin"));
        var done = new TaskCompletionSource<string>();
        r.Corrected += (_, text) => done.TrySetResult(text);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        r.Refine("draft", audio);
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Console.WriteLine($"whisper: \"{result}\" in {sw.ElapsedMilliseconds} ms");
        Assert.Contains("quarterly review", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("18", result.Replace("eighteen", "18", StringComparison.OrdinalIgnoreCase));
    }
}
