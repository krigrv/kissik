using System.Diagnostics;
using OpenKamera.Core;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>
/// Opens the real default microphone through <see cref="AudioEngine"/> and switches every noise removal mode
/// while audio keeps running. Needs a microphone, so it only runs when KISSIK_LIVE_AUDIO=1.
/// </summary>
[Collection(nameof(VoiceTests))]
public class LiveAudioTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Modes_switch_live_on_a_real_microphone()
    {
        if (Environment.GetEnvironmentVariable("KISSIK_LIVE_AUDIO") != "1") return;
        await using var engine = new AudioEngine();
        var mix = new AudioMixSettings { NoiseSuppression = false, LowCut = true, AutoLevel = true, DeEss = true };
        await engine.StartAsync(mix);
        Assert.True(engine.Tracks.Count > 0, $"no microphone opened: {engine.LastError}");
        var mic = engine.Tracks[0];
        output.WriteLine($"Mic: {mic.Name}");

        long quanta = 0, samples = 0;
        engine.MixReady += s => { Interlocked.Increment(ref quanta); Interlocked.Add(ref samples, s.Length / 2); };

        foreach (var mode in new[] { NoiseRemoval.Light, NoiseRemoval.Studio, NoiseRemoval.Off, NoiseRemoval.Studio })
        {
            mix.MicNoise[mic.DeviceId] = mode;
            var sw = Stopwatch.StartNew();
            await engine.UpdateVoiceAsync();
            long swapMs = sw.ElapsedMilliseconds;
            long q0 = Interlocked.Read(ref quanta), s0 = Interlocked.Read(ref samples);
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            await Task.Delay(3000);
            double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds / 3000 * 100;
            long got = Interlocked.Read(ref samples) - s0;
            output.WriteLine($"{mode}: swap {swapMs} ms, {Interlocked.Read(ref quanta) - q0} quanta, {got / 48.0:0} ms of audio in 3000 ms, " +
                             $"running {mic.Noise}, removing {mic.NoiseReductionDb:0} dB, peak {mic.PeakL:0.00}, process CPU {cpu:0.0}% of one core" +
                             (mic.VoiceNote is { } n ? $", note: {n}" : ""));
            Assert.Equal(mode, mic.Noise);
            Assert.InRange(got, 48000 * 3 * 0.9, 48000 * 3 * 1.1); // audio kept flowing in real time through the swap
            Assert.False(engine.Failed);
        }
        Assert.Null(engine.LastError);
    }
}
