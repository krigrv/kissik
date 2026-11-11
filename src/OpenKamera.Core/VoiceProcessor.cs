using System.Runtime.InteropServices;

namespace OpenKamera.Core;

/// <summary>On-device noise removal for one microphone.</summary>
public enum NoiseRemoval { Off, Light, Studio }

/// <summary>Removes noise from 10 ms mono frames (480 samples at 48 kHz), in place, range −1..1.</summary>
public interface IDenoiser : IDisposable
{
    void Process(Span<float> frame);
    /// <summary>True once the denoiser hit an error; it then passes audio through untouched.</summary>
    bool Failed { get; }
}

/// <summary>
/// Per-microphone voice chain at 48 kHz: low cut (80 Hz high-pass) → noise removal → de-esser → gentle
/// auto level toward −16 LUFS (BS.1770 K-weighted) → peak guard at −1 dBFS. Works on interleaved stereo
/// of any quantum size, processes a mono mix in 10 ms frames and writes it back to both channels. Adds no
/// delay when the quantum is a multiple of 10 ms, otherwise 10 ms.
/// </summary>
public sealed class VoiceChain : IDisposable
{
    public const int FrameSize = 480;

    private readonly IDenoiser? _denoiser;
    private readonly Biquad _lowCut = Biquad.HighPass(80, AudioEngine.SampleRate);
    private readonly DeEsser? _deEss;
    private readonly AutoLevel? _level;
    private readonly bool _useLowCut;
    private readonly float[] _frame = new float[FrameSize];
    private readonly Queue<float> _in = new(), _out = new();
    private int _prefill = -1;
    private double _inPow = 1e-10, _outPow = 1e-10;
    // Which input channels carry the voice. A microphone on one input of a two-input interface arrives with
    // the other side silent; averaging the two would halve it (−6 dB), so only the live side is used then.
    private double _leftPow, _rightPow;
    private float _leftMix = 0.5f, _rightMix = 0.5f;

    public NoiseRemoval Mode { get; }
    public bool LowCut => _useLowCut;
    public bool AutoGain => _level is not null;
    public bool DeEss => _deEss is not null;
    /// <summary>Gain auto level is applying now, in dB (0 when off).</summary>
    public double LevelGainDb => _level?.GainDb ?? 0;
    /// <summary>How much the denoiser is taking out right now, in dB (smoothed over ~1 s).</summary>
    public double ReductionDb => _denoiser is null ? 0 : Math.Clamp(10 * Math.Log10(_inPow / _outPow), 0, 60);
    /// <summary>The denoiser stopped after an error and is passing audio through.</summary>
    public bool DenoiserFailed => _denoiser?.Failed == true;

    public VoiceChain(NoiseRemoval mode, bool lowCut, bool autoLevel, bool deEss = false)
    {
        Mode = mode;
        _useLowCut = lowCut;
        _level = autoLevel ? new AutoLevel() : null;
        _deEss = deEss ? new DeEsser() : null;
        _denoiser = CreateDenoiser(mode);
    }

    /// <summary>The denoiser for a mode; throws when its native library or model can't load on this PC.</summary>
    public static IDenoiser? CreateDenoiser(NoiseRemoval mode) => mode switch
    {
        NoiseRemoval.Light => new RnnoiseDenoiser(),
        NoiseRemoval.Studio => new DeepFilterDenoiser(),
        _ => null,
    };

    /// <summary>Processes interleaved stereo in place.</summary>
    public void Process(Span<float> stereo)
    {
        int n = stereo.Length / 2;
        if (_prefill < 0)
        {
            _prefill = n % FrameSize == 0 ? 0 : FrameSize;
            for (int i = 0; i < _prefill; i++) _out.Enqueue(0);
        }
        var (wantL, wantR) = ChannelMix(stereo, n);
        for (int i = 0; i < n; i++)
        {
            // Glide between mixes (about 20 ms) so a change is never a click.
            _leftMix += (wantL - _leftMix) * 0.001f; _rightMix += (wantR - _rightMix) * 0.001f;
            _in.Enqueue(stereo[2 * i] * _leftMix + stereo[2 * i + 1] * _rightMix);
        }
        while (_in.Count >= FrameSize)
        {
            for (int i = 0; i < FrameSize; i++) _frame[i] = _in.Dequeue();
            ProcessFrame(_frame);
            foreach (var s in _frame) _out.Enqueue(s);
        }
        for (int i = 0; i < n; i++)
        {
            float s = _out.Count > 0 ? _out.Dequeue() : 0; // only starves if quanta change size mid-stream
            stereo[2 * i] = stereo[2 * i + 1] = s;
        }
    }

    /// <summary>
    /// Weights for folding stereo to mono: half each normally; all of one side when the other has been at least
    /// 40 dB quieter for a while (a mono microphone on one input of a stereo device).
    /// </summary>
    private (float L, float R) ChannelMix(ReadOnlySpan<float> stereo, int n)
    {
        if (n == 0) return (_leftMix, _rightMix);
        double l = 0, r = 0;
        for (int i = 0; i < n; i++) { l += stereo[2 * i] * stereo[2 * i]; r += stereo[2 * i + 1] * stereo[2 * i + 1]; }
        // About half a second of averaging at 10 ms blocks; silence on both sides changes nothing.
        _leftPow += (l / n - _leftPow) * 0.02; _rightPow += (r / n - _rightPow) * 0.02;
        const double floor = 1e-9, ratio = 1e-4; // −90 dBFS; 40 dB apart
        if (_leftPow > floor && _rightPow < _leftPow * ratio) return (1, 0);
        if (_rightPow > floor && _leftPow < _rightPow * ratio) return (0, 1);
        return (0.5f, 0.5f);
    }

    /// <summary>Carries on from the chain this one replaces, so swapping settings mid-speech keeps the level it had reached.</summary>
    public void ContinueFrom(VoiceChain old)
    {
        _leftPow = old._leftPow; _rightPow = old._rightPow; _leftMix = old._leftMix; _rightMix = old._rightMix;
        if (_level is not null && old._level is not null) _level.ContinueFrom(old._level);
    }

    /// <summary>One 10 ms mono frame through the chain.</summary>
    public void ProcessFrame(Span<float> frame)
    {
        if (_useLowCut) _lowCut.Process(frame);
        if (_denoiser is not null)
        {
            double before = Power(frame);
            _denoiser.Process(frame);
            double after = Power(frame);
            _inPow += (before + 1e-10 - _inPow) * 0.01;
            _outPow += (after + 1e-10 - _outPow) * 0.01;
        }
        _deEss?.Process(frame);
        _level?.Process(frame);
        PeakGuard.Process(frame);
    }

    private static double Power(ReadOnlySpan<float> x) { double s = 0; foreach (var v in x) s += v * v; return s / x.Length; }

    public void Dispose() => _denoiser?.Dispose();
}

/// <summary>Direct-form-I biquad (RBJ cookbook).</summary>
internal sealed class Biquad
{
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    internal Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    { _b0 = (float)(b0 / a0); _b1 = (float)(b1 / a0); _b2 = (float)(b2 / a0); _a1 = (float)(a1 / a0); _a2 = (float)(a2 / a0); }

    /// <summary>Second-order Butterworth high-pass (12 dB/octave).</summary>
    public static Biquad HighPass(double hz, double sampleRate)
    {
        double w = 2 * Math.PI * hz / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
        return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Second-order Butterworth low-pass.</summary>
    public static Biquad LowPass(double hz, double sampleRate)
    {
        double w = 2 * Math.PI * hz / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
        return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public float Next(float x)
    {
        float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
        return y;
    }

    public void Process(Span<float> x)
    {
        for (int i = 0; i < x.Length; i++) x[i] = Next(x[i]);
        if (Math.Abs(_y1) < 1e-20f) _y1 = 0; // no denormals in silence
        if (Math.Abs(_y2) < 1e-20f) _y2 = 0;
    }
}

/// <summary>
/// Slow, gated loudness follower: K-weights the signal as BS.1770 does (high shelf + 38 Hz high-pass, 48 kHz
/// coefficients), averages speech power over ~3 s (frames under −50 LUFS are ignored, so pauses don't pump
/// the gain up) and moves gain at most 3 dB/s toward −16 LUFS, within −6..+12 dB.
/// </summary>
internal sealed class AutoLevel
{
    public const double TargetLufs = -16, MinDb = -6, MaxDb = 12, GateLufs = -50, StepDbPerFrame = 0.03;
    private readonly Biquad _shelf = new(1.53512485958697, -2.69169618940638, 1.19839281085285, 1, -1.69065929318241, 0.73248077421585);
    private readonly Biquad _rlb = new(1, -2, 1, 1, -1.99004745483398, 0.99007225036621);
    private double _power = -1, _gainDb;
    public double GainDb => _gainDb;
    public void ContinueFrom(AutoLevel old) { _power = old._power; _gainDb = old._gainDb; }

    /// <summary>Mean K-weighted power of one frame.</summary>
    private double Measure(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var s in frame) { float k = _rlb.Next(_shelf.Next(s)); sum += k * k; }
        return sum / frame.Length;
    }

    public void Process(Span<float> frame)
    {
        double ms = Measure(frame);
        if (-0.691 + 10 * Math.Log10(ms + 1e-12) > GateLufs)
        {
            _power = _power < 0 ? ms : _power + (ms - _power) * 0.0033; // ~3 s time constant at 10 ms frames
            double lufs = -0.691 + 10 * Math.Log10(_power + 1e-12);
            double want = Math.Clamp(TargetLufs - lufs, MinDb, MaxDb);
            _gainDb += Math.Clamp(want - _gainDb, -StepDbPerFrame, StepDbPerFrame);
        }
        float g = (float)Math.Pow(10, _gainDb / 20);
        for (int i = 0; i < frame.Length; i++) frame[i] *= g;
    }
}

/// <summary>
/// Split-band de-esser: the band above ~5 kHz is turned down (by up to 8 dB) only while it is louder than
/// the rest of the voice, which is what a harsh "s" looks like. Fast attack, slow release.
/// </summary>
internal sealed class DeEsser
{
    public const double MaxCutDb = 8;
    private readonly Biquad _split = Biquad.LowPass(5000, AudioEngine.SampleRate);
    private readonly Biquad _hpDetect = Biquad.HighPass(5000, AudioEngine.SampleRate), _lpDetect = Biquad.LowPass(3000, AudioEngine.SampleRate);
    private float _hiEnv, _loEnv, _gain = 1;

    public void Process(Span<float> frame)
    {
        const float attack = 0.2f, release = 0.0035f;
        float minGain = (float)Math.Pow(10, -MaxCutDb / 20);
        for (int i = 0; i < frame.Length; i++)
        {
            float x = frame[i], hi = x - _split.Next(x); // complementary split: lo + hi is exactly x
            float ah = Math.Abs(_hpDetect.Next(x)), al = Math.Abs(_lpDetect.Next(x));
            _hiEnv += (ah - _hiEnv) * (ah > _hiEnv ? attack : release);
            _loEnv += (al - _loEnv) * (al > _loEnv ? attack : release);
            float want = 1;
            if (_hiEnv > 0.01f && _hiEnv > _loEnv) want = Math.Max(minGain, _loEnv / _hiEnv); // pull the band back to the body's level
            _gain += (want - _gain) * (want < _gain ? 0.05f : 0.002f);
            frame[i] = x - hi * (1 - _gain);
        }
    }
}

/// <summary>Never lets a sample past −1 dBFS: soft knee from −2 dBFS up to the ceiling.</summary>
internal static class PeakGuard
{
    public const float Ceiling = 0.891f; // −1 dBFS

    public static void Process(Span<float> frame)
    {
        const float knee = Ceiling * 0.9f, room = Ceiling - knee;
        for (int i = 0; i < frame.Length; i++)
        {
            float a = Math.Abs(frame[i]);
            if (a > knee)
            {
                float over = a - knee;
                frame[i] = MathF.CopySign(knee + room * over / (over + room), frame[i]);
            }
        }
    }
}

/// <summary>Loads KISSIK's native voice libraries from <c>voice\x64</c> or <c>voice\x86</c> next to the app.</summary>
internal static class VoiceNative
{
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "voice", RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        var a => throw new PlatformNotSupportedException($"Noise removal isn't built for {a} yet."),
    });

    private static readonly Dictionary<string, IntPtr> s_libs = [];

    public static IntPtr Load(string file)
    {
        lock (s_libs)
        {
            if (!s_libs.TryGetValue(file, out var h)) s_libs[file] = h = NativeLibrary.Load(Path.Combine(Folder, file));
            return h;
        }
    }
}

/// <summary>RNNoise 0.2 (BSD-3-Clause): light, about 1 % of one core.</summary>
public sealed unsafe class RnnoiseDenoiser : IDenoiser
{
    private static delegate* unmanaged<IntPtr, IntPtr> s_create;
    private static delegate* unmanaged<IntPtr, void> s_destroy;
    private static delegate* unmanaged<IntPtr, float*, float*, float> s_process;

    private IntPtr _state;
    private readonly float[] _buf = new float[VoiceChain.FrameSize];
    public bool Failed => false;

    public RnnoiseDenoiser()
    {
        if (s_process is null)
        {
            var lib = VoiceNative.Load("rnnoise.dll");
            s_create = (delegate* unmanaged<IntPtr, IntPtr>)NativeLibrary.GetExport(lib, "rnnoise_create");
            s_destroy = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(lib, "rnnoise_destroy");
            s_process = (delegate* unmanaged<IntPtr, float*, float*, float>)NativeLibrary.GetExport(lib, "rnnoise_process_frame");
        }
        _state = s_create(IntPtr.Zero);
        if (_state == IntPtr.Zero) throw new InvalidOperationException("RNNoise could not start.");
    }

    public void Process(Span<float> frame)
    {
        if (_state == IntPtr.Zero) return;
        // RNNoise works on 16-bit-scaled floats.
        for (int i = 0; i < _buf.Length; i++) _buf[i] = frame[i] * 32768f;
        fixed (float* p = _buf) s_process(_state, p, p);
        for (int i = 0; i < _buf.Length; i++) frame[i] = _buf[i] / 32768f;
    }

    public void Dispose() { if (_state != IntPtr.Zero) { s_destroy(_state); _state = IntPtr.Zero; } }
}

/// <summary>
/// DeepFilterNet 3, low-latency model (MIT/Apache-2.0), through KISSIK's panic-safe wrapper
/// (<c>native/kissik-voice</c>). Runs synchronously on the caller's thread; the model adds 20 ms.
/// An internal error never takes the app down: frames pass through and <see cref="Failed"/> is set.
/// </summary>
public sealed unsafe class DeepFilterDenoiser : IDenoiser
{
    public const string ModelFile = "DeepFilterNet3_ll_onnx.tar.gz";
    private static delegate* unmanaged<byte*, float, IntPtr> s_create;
    private static delegate* unmanaged<IntPtr, nuint> s_frameLength;
    private static delegate* unmanaged<IntPtr, float*, float*, int> s_process;
    private static delegate* unmanaged<IntPtr, void> s_destroy;

    private IntPtr _state;
    public bool Failed { get; private set; }

    /// <param name="attenuationDb">Most the model may take out, in dB (100 = no limit).</param>
    public DeepFilterDenoiser(float attenuationDb = 100)
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Models", ModelFile);
        if (!File.Exists(model)) throw new InvalidOperationException("The Studio voice model is missing.");
        if (s_process is null)
        {
            var lib = VoiceNative.Load("kissik_voice.dll");
            s_create = (delegate* unmanaged<byte*, float, IntPtr>)NativeLibrary.GetExport(lib, "kv_create");
            s_frameLength = (delegate* unmanaged<IntPtr, nuint>)NativeLibrary.GetExport(lib, "kv_frame_length");
            s_process = (delegate* unmanaged<IntPtr, float*, float*, int>)NativeLibrary.GetExport(lib, "kv_process");
            s_destroy = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(lib, "kv_destroy");
        }
        var path = System.Text.Encoding.UTF8.GetBytes(model + "\0");
        fixed (byte* p = path) _state = s_create(p, attenuationDb);
        if (_state == IntPtr.Zero) throw new InvalidOperationException("Studio voice could not start.");
        if (s_frameLength(_state) != VoiceChain.FrameSize) { Dispose(); throw new InvalidOperationException("Unexpected Studio voice frame size."); }
    }

    public void Process(Span<float> frame)
    {
        if (_state == IntPtr.Zero || Failed) return;
        fixed (float* p = frame) if (s_process(_state, p, p) != 0) Failed = true;
    }

    public void Dispose() { if (_state != IntPtr.Zero) { s_destroy(_state); _state = IntPtr.Zero; } }
}

/// <summary>Minimal 16-bit / float WAV reader and writer for tests and tools.</summary>
public static class WavFile
{
    public static void WriteMono16(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in samples) w.Write((short)Math.Clamp(MathF.Round(s * 32767f), -32768, 32767));
    }

    /// <summary>Reads PCM16 or float32 WAV; multi-channel audio is averaged to mono.</summary>
    public static (float[] Samples, int SampleRate) ReadMono(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a WAV file.");
        r.ReadInt32(); r.ReadChars(4);
        int channels = 1, rate = 0, bits = 16, format = 1;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            string id = new(r.ReadChars(4)); int size = r.ReadInt32();
            if (id == "fmt ")
            {
                format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                r.BaseStream.Position += size - 16;
            }
            else if (id == "data")
            {
                int frames = size / (bits / 8) / channels;
                var mono = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += format == 3 ? r.ReadSingle() : r.ReadInt16() / 32768f;
                    mono[i] = sum / channels;
                }
                return (mono, rate);
            }
            else r.BaseStream.Position += size;
        }
        throw new InvalidDataException("WAV has no data.");
    }
}
