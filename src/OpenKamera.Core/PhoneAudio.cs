namespace OpenKamera.Core;

/// <summary>
/// Phone microphone packets: 'A', sample rate (uint32 LE), then mono float32 samples. Converted here to the
/// 48 kHz stereo the audio engine mixes.
/// </summary>
public static class PhoneAudio
{
    public const byte Tag = (byte)'A';

    public static bool IsAudio(byte[] data) => data.Length >= 5 && data[0] == Tag;

    public static float[] ToStereo48k(byte[] packet)
    {
        int rate = (int)BitConverter.ToUInt32(packet, 1);
        int n = (packet.Length - 5) / 4;
        if (n <= 0 || rate is < 8000 or > 192000) return [];
        var mono = new float[n];
        Buffer.BlockCopy(packet, 5, mono, 0, n * 4);
        int outN = (int)Math.Round(n * (double)AudioEngine.SampleRate / rate);
        var stereo = new float[outN * 2];
        double step = rate / (double)AudioEngine.SampleRate;
        for (int i = 0; i < outN; i++)
        {
            double pos = i * step; int a = Math.Min((int)pos, n - 1), b = Math.Min(a + 1, n - 1);
            float v = (float)(mono[a] + (mono[b] - mono[a]) * (pos - a));
            stereo[2 * i] = stereo[2 * i + 1] = Math.Clamp(v, -1f, 1f);
        }
        return stereo;
    }

    public static byte[] Encode(float[] mono, int rate)
    {
        var p = new byte[5 + mono.Length * 4];
        p[0] = Tag; BitConverter.TryWriteBytes(p.AsSpan(1), (uint)rate);
        Buffer.BlockCopy(mono, 0, p, 5, mono.Length * 4);
        return p;
    }
}
