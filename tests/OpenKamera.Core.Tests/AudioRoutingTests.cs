using OpenKamera.Core;

namespace OpenKamera.Core.Tests;

public class AudioRoutingTests
{
    private static readonly (string Id, string Name)[] Present = [("usb", "USB mic"), ("built-in", "Laptop mic")];

    [Fact]
    public void SavedMicThatIsConnected_IsUsed()
    {
        var chosen = AudioEngine.ChooseMics(["built-in"], Present, out bool fellBack);
        Assert.False(fellBack);
        Assert.Equal("built-in", Assert.Single(chosen).Id);
    }

    [Fact]
    public void SavedMicThatIsUnplugged_FallsBackToAnotherInsteadOfSilence()
    {
        var chosen = AudioEngine.ChooseMics(["headset-that-left"], Present, out bool fellBack);
        Assert.True(fellBack);
        Assert.Single(chosen);
    }

    [Fact]
    public void NoMicAtAll_ChoosesNothing()
    {
        Assert.Empty(AudioEngine.ChooseMics(["x"], [], out _));
        Assert.Empty(AudioEngine.ChooseMics([], [], out _));
    }

    [Theory]
    [InlineData("en-us", "en")]
    [InlineData("en-in", "en")]
    [InlineData("hi", "hi")]
    [InlineData("pt_BR", "pt")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void WhisperGetsPlainLanguageCodes(string? given, string expected) =>
        Assert.Equal(expected, WhisperRefiner.LanguageCode(given));

    private static double Rms(ReadOnlySpan<float> x) { double s = 0; foreach (var v in x) s += v * v; return Math.Sqrt(s / x.Length); }

    /// <summary>One second of a 300 Hz tone as interleaved stereo with the given level on each side.</summary>
    private static float[] Tone(float left, float right, int seconds = 1)
    {
        var x = new float[AudioEngine.SampleRate * seconds * 2];
        for (int i = 0; i < x.Length / 2; i++)
        {
            float s = MathF.Sin(2 * MathF.PI * 300 * i / AudioEngine.SampleRate);
            x[2 * i] = s * left; x[2 * i + 1] = s * right;
        }
        return x;
    }

    private static double LevelAfter(float left, float right)
    {
        using var chain = new VoiceChain(NoiseRemoval.Off, lowCut: false, autoLevel: false);
        var x = Tone(left, right, seconds: 3);
        // 10 ms blocks, as the audio engine delivers them.
        for (int at = 0; at < x.Length; at += 960) chain.Process(x.AsSpan(at, 960));
        return Rms(x.AsSpan(x.Length - 9600)); // the last 100 ms, once the mix has settled
    }

    [Fact]
    public void MicOnOneInputOfAStereoDevice_KeepsItsFullLevel()
    {
        double both = LevelAfter(0.2f, 0.2f), leftOnly = LevelAfter(0.2f, 0f), rightOnly = LevelAfter(0f, 0.2f);
        Assert.InRange(leftOnly / both, 0.97, 1.03);  // was half (−6 dB)
        Assert.InRange(rightOnly / both, 0.97, 1.03);
    }

    [Fact]
    public void QuietSideThatIsStillSound_IsStillAveraged()
    {
        // 20 dB apart is a real stereo pair, not a dead input.
        double mixed = LevelAfter(0.2f, 0.02f), both = LevelAfter(0.2f, 0.2f);
        Assert.InRange(mixed / both, 0.5, 0.6);
    }

    [Fact]
    public void SwappingSettings_KeepsTheLevelAutoLevelHadReached()
    {
        using var first = new VoiceChain(NoiseRemoval.Off, lowCut: true, autoLevel: true);
        var quiet = Tone(0.01f, 0.01f, seconds: 6);
        for (int at = 0; at < quiet.Length; at += 960) first.Process(quiet.AsSpan(at, 960));
        Assert.True(first.LevelGainDb > 6, $"auto level only reached {first.LevelGainDb:0.0} dB");

        using var next = new VoiceChain(NoiseRemoval.Off, lowCut: true, autoLevel: true, deEss: true);
        next.ContinueFrom(first);
        Assert.Equal(first.LevelGainDb, next.LevelGainDb, 3);
    }
}
