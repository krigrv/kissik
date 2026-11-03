using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OpenKamera.Core;

/// <summary>
/// Captures what the PC is playing (WASAPI loopback of the default speakers) and hands it over as 48 kHz stereo
/// float blocks, ready for <see cref="AudioEngine.PushAudio"/>.
/// </summary>
public sealed class SystemSound : IDisposable
{
    private WasapiLoopbackCapture? _capture;

    public event Action<float[]>? Samples;
    public bool IsRunning => _capture is not null;
    public string? DeviceName { get; private set; }

    public void Start()
    {
        Stop();
        var cap = new WasapiLoopbackCapture();
        DeviceName = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
        var fmt = cap.WaveFormat;
        cap.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded == 0) return;
            var block = Convert(e.Buffer, e.BytesRecorded, fmt);
            if (block.Length > 0) Samples?.Invoke(block);
        };
        cap.StartRecording();
        _capture = cap;
    }

    public void Stop()
    {
        if (_capture is { } c) { try { c.StopRecording(); } catch { } c.Dispose(); }
        _capture = null;
    }

    public void Dispose() => Stop();

    /// <summary>Any loopback mix format (float or 16-bit, any channels, any rate) → 48 kHz stereo float.</summary>
    public static float[] Convert(byte[] buffer, int bytes, WaveFormat fmt)
    {
        int ch = Math.Max(1, fmt.Channels);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
            || (fmt is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        int bps = fmt.BitsPerSample / 8;
        int frames = bytes / (bps * ch);
        if (frames == 0 || (bps != 4 && bps != 2)) return [];
        float Sample(int frame, int c)
        {
            int i = (frame * ch + c) * bps;
            return isFloat && bps == 4 ? BitConverter.ToSingle(buffer, i) : bps == 2 ? BitConverter.ToInt16(buffer, i) / 32768f : BitConverter.ToInt32(buffer, i) / 2147483648f;
        }
        int outFrames = (int)Math.Round(frames * (double)AudioEngine.SampleRate / fmt.SampleRate);
        var dst = new float[outFrames * 2];
        double step = fmt.SampleRate / (double)AudioEngine.SampleRate;
        for (int o = 0; o < outFrames; o++)
        {
            double pos = o * step; int a = Math.Min((int)pos, frames - 1), b = Math.Min(a + 1, frames - 1); float t = (float)(pos - a);
            float l = Sample(a, 0) + (Sample(b, 0) - Sample(a, 0)) * t;
            float r = ch > 1 ? Sample(a, 1) + (Sample(b, 1) - Sample(a, 1)) * t : l;
            dst[2 * o] = l; dst[2 * o + 1] = r;
        }
        return dst;
    }
}
