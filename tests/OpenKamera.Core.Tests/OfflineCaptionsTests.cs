namespace OpenKamera.Core.Tests;

[Collection("Timing")]
public class OfflineCaptionsTests
{
    [Fact]
    public void Downmix_48kStereoTo16kMono()
    {
        var st = new float[4800 * 2]; for (int i = 0; i < st.Length; i++) st[i] = 0.5f;
        var pcm = OfflineCaptions.ToPcm16Mono16k(st);
        Assert.Equal(1600 * 2, pcm.Length);
        short v = (short)(pcm[0] | pcm[1] << 8);
        Assert.InRange(v, 16000, 16500);
    }

    [Fact]
    public void Languages_AreTheOfficialSmallModels() =>
        Assert.All(OfflineCaptions.Languages, l => Assert.StartsWith("https://alphacephei.com/vosk/models/vosk-model-small-", l.Url));

    [Fact]
    public void ModelDir_IsNullUntilDownloaded() =>
        Assert.Null(OfflineCaptions.ModelDir(Path.Combine(Path.GetTempPath(), "ok-none-" + Guid.NewGuid()), "en-us"));

    [Fact]
    public async Task RecognisesSpokenWords_Offline()
    {
        string root = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "vosk");
        var en = OfflineCaptions.Languages[0];
        string? dir = OfflineCaptions.ModelDir(root, en.Code);
        if (dir is null)
        {
            try { dir = await OfflineCaptions.DownloadAsync(en, root); }
            catch (HttpRequestException) { return; } // offline machine: nothing to test
        }
        // Windows' built-in voice says a sentence; the offline model should write it down.
        using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
        using var stream = await synth.SynthesizeTextToStreamAsync("hello world this is a test of offline captions");
        var bytes = new byte[stream.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
        { await reader.LoadAsync((uint)stream.Size); reader.ReadBytes(bytes); }
        int rate = BitConverter.ToInt32(bytes, 24); short ch = BitConverter.ToInt16(bytes, 22);
        int data = 44; // standard WAV header
        int samples = (bytes.Length - data) / 2 / ch;
        // Make 48 kHz stereo floats like the audio mix.
        int outN = (int)((long)samples * 48000 / rate);
        var stereo = new float[outN * 2];
        for (int o = 0; o < outN; o++)
        {
            int si = (int)((long)o * rate / 48000);
            float v = BitConverter.ToInt16(bytes, data + si * 2 * ch) / 32768f;
            stereo[2 * o] = stereo[2 * o + 1] = v;
        }
        using var cap = new OfflineCaptions(dir);
        var heard = new System.Text.StringBuilder();
        cap.Final += t => heard.Append(t).Append(' ');
        string lastPartial = "";
        cap.Partial += t => lastPartial = t;
        for (int i = 0; i < stereo.Length; i += 9600) cap.Feed(stereo[i..Math.Min(stereo.Length, i + 9600)]);
        cap.Feed(new float[48000 * 2]); // a second of silence ends the sentence
        string text = (heard + " " + lastPartial).ToLowerInvariant();
        Assert.Contains("hello", text);
        Assert.Contains("test", text);
    }

    [Fact]
    public void DisposingWhileBusy_NeverHangsOrCrashes()
    {
        string? dir = OfflineCaptions.ModelDir(Path.Combine(Path.GetTempPath(), "OpenKameraTests", "vosk"), "en-us");
        if (dir is null) return; // model not downloaded on this machine
        var noise = new float[9600]; var r = new Random(1); for (int i = 0; i < noise.Length; i++) noise[i] = (float)(r.NextDouble() - 0.5);
        for (int round = 0; round < 3; round++)
        {
            var cap = new OfflineCaptions(dir);
            for (int i = 0; i < 40; i++) cap.Enqueue(noise);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            cap.Dispose();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"dispose took {sw.Elapsed}");
            cap.Enqueue(noise); // after dispose: ignored, no crash
        }
    }
}
