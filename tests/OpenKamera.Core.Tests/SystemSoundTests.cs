using NAudio.Wave;

namespace OpenKamera.Core.Tests;

public class SystemSoundTests
{
    [Fact]
    public void FloatStereo48k_PassesThrough()
    {
        var src = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var bytes = new byte[src.Length * 4]; Buffer.BlockCopy(src, 0, bytes, 0, bytes.Length);
        var o = SystemSound.Convert(bytes, bytes.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Assert.Equal(src, o);
    }

    [Fact]
    public void Pcm16Mono44k_BecomesStereo48k()
    {
        var bytes = new byte[441 * 2];
        for (int i = 0; i < 441; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)16384);
        var o = SystemSound.Convert(bytes, bytes.Length, new WaveFormat(44100, 16, 1));
        Assert.Equal(480 * 2, o.Length);
        Assert.All(o, v => Assert.Equal(0.5f, v, 3));
    }

    [Fact]
    public void LiveLoopback_StartsAndStops()
    {
        using var s = new SystemSound();
        s.Start();
        Assert.True(s.IsRunning);
        Assert.False(string.IsNullOrEmpty(s.DeviceName));
        s.Stop();
        Assert.False(s.IsRunning);
    }
}
