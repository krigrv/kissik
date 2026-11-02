using System.Diagnostics;
using OpenKamera.Core;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>These measure CPU time for the whole process, so they run alone, not beside the heavy image tests.</summary>
[CollectionDefinition(nameof(VoiceTests), DisableParallelization = true)]
public class VoiceTestsCollection;

[Collection("Timing")]
public class VoiceTests(ITestOutputHelper output)
{
    private const int Sr = 48000;
    private const int Seconds = 6;

    /// <summary>
    /// A voice-like test clip: 400 ms "syllables" of a gliding harmonic tone (formant-shaped), 300 ms gaps,
    /// over steady fan-like noise and 50 Hz hum. Written to and read back from a real WAV file.
    /// </summary>
    private static (float[] Noisy, bool[] IsGap) NoisyWav(float voicePeak = 0.3f, float noiseRms = 0.02f)
    {
        var rng = new Random(7);
        int n = Sr * Seconds;
        var x = new float[n];
        var gap = new bool[n];
        double phase = 0, brown = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Sr, inCycle = t % 0.7;
            bool talking = inCycle < 0.4 && t > 0.5; // first 500 ms is noise only (warm-up)
            gap[i] = !talking && inCycle > 0.45 && inCycle < 0.65;
            double f0 = 140 + 30 * Math.Sin(2 * Math.PI * 1.3 * t);
            phase += 2 * Math.PI * f0 / Sr;
            double v = 0;
            if (talking)
            {
                double env = Math.Sin(Math.PI * inCycle / 0.4);
                for (int h = 1; h <= 25; h++)
                {
                    double fh = f0 * h, formant = Math.Exp(-Math.Pow((fh - 700) / 400, 2)) + 0.6 * Math.Exp(-Math.Pow((fh - 1800) / 500, 2)) + 0.15;
                    v += formant * Math.Sin(h * phase) / h;
                }
                v *= env * voicePeak * 0.8;
            }
            double white = rng.NextDouble() * 2 - 1;
            brown = 0.98 * brown + 0.2 * white;
            double noise = noiseRms * (0.9 * white * 1.7 + 0.5 * brown) + 0.01 * Math.Sin(2 * Math.PI * 50 * t);
            x[i] = (float)(v + noise);
        }
        string path = Path.Combine(Path.GetTempPath(), $"kissik-noisy-{Guid.NewGuid():N}.wav");
        try
        {
            WavFile.WriteMono16(path, x, Sr);
            var (read, rate) = WavFile.ReadMono(path);
            Assert.Equal(Sr, rate);
            return (read, gap);
        }
        finally { File.Delete(path); }
    }

    private static float[] Run(VoiceChain chain, float[] mono, out double msPerSecond)
    {
        // Push through as the engine does: interleaved stereo, 10 ms quanta.
        var outMono = new float[mono.Length];
        var q = new float[VoiceChain.FrameSize * 2];
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime; // all threads, incl. the denoiser's worker
        for (int at = 0; at + VoiceChain.FrameSize <= mono.Length; at += VoiceChain.FrameSize)
        {
            for (int i = 0; i < VoiceChain.FrameSize; i++) q[2 * i] = q[2 * i + 1] = mono[at + i];
            chain.Process(q);
            for (int i = 0; i < VoiceChain.FrameSize; i++) outMono[at + i] = q[2 * i];
        }
        msPerSecond = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds / (mono.Length / (double)Sr);
        return outMono;
    }

    private static double RmsDb(float[] x, Func<int, bool> pick, int from = Sr)
    {
        double sum = 0; int count = 0;
        for (int i = from; i < x.Length; i++) if (pick(i)) { sum += x[i] * (double)x[i]; count++; }
        return 10 * Math.Log10(sum / Math.Max(1, count) + 1e-15);
    }

    [Theory]
    [InlineData(NoiseRemoval.Light, 10)]
    [InlineData(NoiseRemoval.Studio, 15)]
    public void Noise_floor_drops_without_clipping(NoiseRemoval mode, double minDropDb)
    {
        var (noisy, gap) = NoisyWav();
        using var chain = new VoiceChain(mode, lowCut: true, autoLevel: false);
        var clean = Run(chain, noisy, out var ms);

        double before = RmsDb(noisy, i => gap[i]), after = RmsDb(clean, i => gap[i]);
        double voiceBefore = RmsDb(noisy, i => !gap[i]), voiceAfter = RmsDb(clean, i => !gap[i]);
        output.WriteLine($"{mode}: noise floor {before:0.0} → {after:0.0} dBFS, voice+noise {voiceBefore:0.0} → {voiceAfter:0.0} dBFS, CPU {ms:0.0} ms per second of audio ({ms / 10:0.0}% of one core)");

        Assert.True(before - after >= minDropDb, $"noise floor dropped only {before - after:0.0} dB");
        Assert.True(voiceAfter > after + 10, "voice should stay well above the cleaned noise floor");
        Assert.All(clean, s => Assert.True(Math.Abs(s) < 1f));
    }

    [Fact]
    public void Studio_voice_latency_is_under_40_ms()
    {
        var (noisy, _) = NoisyWav(noiseRms: 0.002f);
        using var chain = new VoiceChain(NoiseRemoval.Studio, lowCut: false, autoLevel: false);
        var clean = Run(chain, noisy, out _);
        // Find the delay by cross-correlating the outputs' envelope against the input.
        int best = 0; double bestScore = double.MinValue;
        for (int lag = 0; lag <= Sr / 10; lag += 48)
        {
            double score = 0;
            for (int i = Sr; i + lag < noisy.Length; i += 4) score += noisy[i] * clean[i + lag];
            if (score > bestScore) { bestScore = score; best = lag; }
        }
        double ms = best * 1000.0 / Sr;
        output.WriteLine($"Studio voice delay: {ms:0} ms (plus one 10 ms audio quantum in the graph)");
        Assert.True(ms + 10 < 40, $"delay {ms} ms");
    }

    [Fact]
    public void Light_mode_adds_no_frame_delay_on_10ms_quanta()
    {
        var x = new float[Sr];
        x[Sr / 2] = 0.5f;
        using var chain = new VoiceChain(NoiseRemoval.Off, lowCut: true, autoLevel: false);
        var y = Run(chain, x, out _);
        int peak = Array.IndexOf(y, y.Max());
        Assert.InRange(peak, Sr / 2, Sr / 2 + 10);
    }

    [Fact]
    public void Low_cut_removes_rumble_but_keeps_voice()
    {
        float Gain(double hz)
        {
            var x = new float[Sr];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / Sr));
            using var chain = new VoiceChain(NoiseRemoval.Off, lowCut: true, autoLevel: false);
            var y = Run(chain, x, out _);
            return (float)(RmsDb(y, _ => true, Sr / 2) - RmsDb(x, _ => true, Sr / 2));
        }
        Assert.True(Gain(25) < -15, "25 Hz rumble should drop");
        Assert.True(Gain(80) is > -4 and < -2, "−3 dB at the 80 Hz corner");
        Assert.True(Math.Abs(Gain(300)) < 0.5, "voice band untouched");
    }

    [Fact]
    public void Auto_level_brings_quiet_voice_up_toward_minus_16_LUFS_without_clipping()
    {
        var (quiet, gap) = NoisyWav(voicePeak: 0.05f, noiseRms: 0.0005f);
        var long20 = Enumerable.Range(0, 4).SelectMany(_ => quiet).ToArray(); // 24 s: let the slow follower settle
        using var chain = new VoiceChain(NoiseRemoval.Off, lowCut: true, autoLevel: true);
        var y = Run(chain, long20, out _);
        int tail = long20.Length - Sr * Seconds;
        double before = RmsDb(long20, i => !gap[i % gap.Length] , tail), after = RmsDb(y, i => !gap[i % gap.Length], tail);
        output.WriteLine($"Auto level: {before:0.0} → {after:0.0} dBFS RMS, gain {chain.LevelGainDb:0.0} dB");
        Assert.True(after - before > 6, "quiet voice should be raised");
        Assert.True(after < -12, "and not pushed past the target");

        var (loud, _) = NoisyWav(voicePeak: 1.4f, noiseRms: 0.01f); // clipping-hot input
        using var hot = new VoiceChain(NoiseRemoval.Off, lowCut: true, autoLevel: true);
        Assert.All(Run(hot, loud, out _), s => Assert.True(Math.Abs(s) <= 0.9f));
    }

    /// <summary>
    /// Real speech: Windows' built-in voice reads a sentence (so no recorded audio needs licensing), saved as a
    /// WAV, resampled to 48 kHz and mixed with fan noise and hum. The cleaned result must sit closer to the
    /// clean speech than the noisy input did.
    /// </summary>
    private static async Task<(float[] Clean, float[] Noisy)> SpokenWav()
    {
        using var tts = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
        using var stream = await tts.SynthesizeTextToStreamAsync(
            "The quick brown fox jumps over the lazy dog. She sells sea shells by the sea shore, and then we pause. " +
            "Please check the audio levels before the meeting starts.");
        var bytes = new byte[stream.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        string path = Path.Combine(Path.GetTempPath(), $"kissik-speech-{Guid.NewGuid():N}.wav");
        float[] speech; int rate;
        try { File.WriteAllBytes(path, bytes); (speech, rate) = WavFile.ReadMono(path); }
        finally { File.Delete(path); }

        int n = (int)((long)speech.Length * Sr / rate);
        var clean = new float[n + Sr]; // 1 s of lead-in so the denoisers settle first
        for (int i = 0; i < n; i++)
        {
            double at = (double)i * rate / Sr; int a = (int)at; double f = at - a;
            float v = a + 1 < speech.Length ? (float)(speech[a] * (1 - f) + speech[a + 1] * f) : 0;
            clean[Sr + i] = v * 0.5f;
        }
        var rng = new Random(3);
        var noisy = new float[clean.Length];
        double brown = 0;
        for (int i = 0; i < noisy.Length; i++)
        {
            double white = rng.NextDouble() * 2 - 1;
            brown = 0.98 * brown + 0.2 * white;
            noisy[i] = clean[i] + (float)(0.015 * (1.5 * white + 0.5 * brown) + 0.006 * Math.Sin(2 * Math.PI * 50 * i / Sr));
        }
        return (clean, noisy);
    }

    /// <summary>Signal-to-error ratio against the clean speech, after the best alignment within 60 ms.</summary>
    private static double SnrDb(float[] clean, float[] test, out int lagMs)
    {
        double best = double.MinValue; lagMs = 0;
        for (int lag = 0; lag <= Sr * 60 / 1000; lag += 48)
        {
            double sig = 0, err = 0;
            for (int i = Sr; i + lag < test.Length; i++) { double c = clean[i], e = test[i + lag] - c; sig += c * c; err += e * e; }
            double snr = 10 * Math.Log10(sig / err);
            if (snr > best) { best = snr; lagMs = lag * 1000 / Sr; }
        }
        return best;
    }

    [Theory]
    [InlineData(NoiseRemoval.Light, 12)]
    [InlineData(NoiseRemoval.Studio, 20)]
    public async Task Real_speech_gets_cleaner(NoiseRemoval mode, double minFloorDropDb)
    {
        var (clean, noisy) = await SpokenWav();
        using var chain = new VoiceChain(mode, lowCut: false, autoLevel: false);
        var y = Run(chain, noisy, out var ms);
        // Pauses: 20 ms windows where the clean speech is below −60 dBFS; speech: windows above −30 dBFS.
        const int W = 960;
        var pause = new bool[clean.Length]; var talk = new bool[clean.Length];
        for (int w = Sr; w + W <= clean.Length; w += W)
        {
            double e = 0; for (int i = w; i < w + W; i++) e += clean[i] * clean[i];
            double db = 10 * Math.Log10(e / W + 1e-15);
            for (int i = w; i < w + W; i++) { pause[i] = db < -60; talk[i] = db > -30; }
        }
        // Compare like with like: shift the output back by the chain's delay.
        int lag = mode == NoiseRemoval.Studio ? 480 : 0;
        var aligned = y.Skip(lag).Concat(new float[lag]).ToArray();
        double floorBefore = RmsDb(noisy, i => pause[i]), floorAfter = RmsDb(aligned, i => pause[i]);
        double talkBefore = RmsDb(noisy, i => talk[i]), talkAfter = RmsDb(aligned, i => talk[i]);
        double snrBefore = SnrDb(clean, noisy, out _), snrAfter = SnrDb(clean, y, out int lagMs);
        output.WriteLine($"{mode} on speech: pauses {floorBefore:0.0} → {floorAfter:0.0} dBFS, speech {talkBefore:0.0} → {talkAfter:0.0} dBFS, " +
                         $"SNR {snrBefore:0.0} → {snrAfter:0.0} dB (delay {lagMs} ms), CPU {ms / 10:0.0}% of one core");
        Assert.True(floorBefore - floorAfter >= minFloorDropDb, $"noise in pauses dropped only {floorBefore - floorAfter:0.0} dB");
        Assert.True(talkAfter - talkBefore > -4, "speech should keep its level");
        Assert.True(snrAfter > snrBefore + 2, "the result should be closer to the clean speech");
        Assert.All(y, s => Assert.True(Math.Abs(s) < 1f));
    }

    [Fact]
    public void De_esser_softens_sibilance_and_leaves_vowels()
    {
        double Change(double hz, float amp)
        {
            var x = new float[Sr];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / Sr));
            using var chain = new VoiceChain(NoiseRemoval.Off, lowCut: false, autoLevel: false, deEss: true);
            var y = Run(chain, x, out _);
            return RmsDb(y, _ => true, Sr / 2) - RmsDb(x, _ => true, Sr / 2);
        }
        double hiss = Change(8000, 0.3f), vowel = Change(500, 0.3f);
        output.WriteLine($"De-esser: 8 kHz {hiss:0.0} dB, 500 Hz {vowel:0.0} dB");
        Assert.True(hiss < -4, "a loud 8 kHz 's' should be turned down");
        Assert.True(Math.Abs(vowel) < 0.5, "a vowel should be left alone");
    }

    [Fact]
    public void Missing_studio_model_fails_cleanly_instead_of_crashing()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Models", DeepFilterDenoiser.ModelFile), moved = model + ".bak";
        File.Move(model, moved);
        try { Assert.Throws<InvalidOperationException>(() => new VoiceChain(NoiseRemoval.Studio, true, false)); }
        finally { File.Move(moved, model); }
    }

    [Fact]
    public void New_microphones_default_to_light()
    {
        var mix = new AudioMixSettings();
        Assert.Equal(NoiseRemoval.Light, mix.NoiseFor("any-mic"));
        mix.MicNoise["any-mic"] = NoiseRemoval.Off;
        Assert.Equal(NoiseRemoval.Off, mix.NoiseFor("any-mic"));
    }
}
