namespace OpenKamera.Core.Tests;

public class PhoneAudioTests
{
    [Fact]
    public void Resamples44kMonoTo48kStereo()
    {
        var mono = Enumerable.Range(0, 441).Select(i => (float)Math.Sin(i / 10.0) * 0.5f).ToArray();
        var st = PhoneAudio.ToStereo48k(PhoneAudio.Encode(mono, 44100));
        Assert.Equal(480 * 2, st.Length);
        Assert.Equal(st[100], st[101]); // both channels equal
        Assert.True(st.All(v => Math.Abs(v) <= 0.5001f));
    }

    [Fact]
    public void JpegIsNotAudio()
    {
        Assert.False(PhoneAudio.IsAudio([0xFF, 0xD8, 0, 0, 0, 0]));
        Assert.True(PhoneAudio.IsAudio(PhoneAudio.Encode([0f], 48000)));
    }

    [Fact]
    public void BadRateGivesNothing() => Assert.Empty(PhoneAudio.ToStereo48k(PhoneAudio.Encode([0.1f], 5)));
}
