using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace OpenKamera.Core;

/// <summary>One microphone in the mix.</summary>
public sealed class AudioTrack
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    internal IAudioInputNode? Input;
    internal AudioEngine.PushSource? Push;
    internal AudioFrameOutputNode? Tap;
    /// <summary>Raw device audio for the voice chain; the chain's output feeds <see cref="Processed"/>.</summary>
    internal AudioFrameOutputNode? Raw;
    internal AudioEngine.PushSource? Processed;
    internal VoiceChain? Chain;
    /// <summary>Held by the audio thread while the chain runs, so a live swap never frees it mid-frame.</summary>
    internal readonly object ChainLock = new();
    /// <summary>Why the chosen noise removal isn't running on this mic, or null.</summary>
    public string? VoiceNote { get; internal set; }
    /// <summary>How much noise removal is taking out now, in dB.</summary>
    public double NoiseReductionDb => Chain?.ReductionDb ?? 0;
    /// <summary>Noise removal hit an error and is passing this mic through untouched.</summary>
    public bool NoiseFailed => Chain?.DenoiserFailed == true;
    private double _volume = 1;
    private bool _muted;

    /// <summary>0..2 (1 = unity).</summary>
    public double Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 2); Apply(); } }
    public bool Muted { get => _muted; set { _muted = value; Apply(); } }
    /// <summary>Last quantum's peak per channel, 0..1 (before volume, so you can see a muted mic).</summary>
    public float PeakL { get; internal set; }
    public float PeakR { get; internal set; }

    /// <summary>Noise removal actually running on this mic (Off if it couldn't start).</summary>
    public NoiseRemoval Noise => Chain is { DenoiserFailed: false } c ? c.Mode : NoiseRemoval.Off;

    internal void Apply() { if (Input is not null) Input.OutgoingGain = _muted ? 0 : _volume; }
}

/// <summary>Master bus settings.</summary>
public sealed class AudioMixSettings
{
    public double MasterVolume { get; set; } = 1;
    public bool Limiter { get; set; } = true;
    /// <summary>Four equaliser bands in dB (−12..+12): low, low-mid, high-mid, high.</summary>
    public double[] EqDb { get; set; } = [0, 0, 0, 0];
    public bool NoiseSuppression { get; set; } = true;
    /// <summary>On-device noise removal per microphone id (missing = Light, which costs about 1 % of one core).</summary>
    public Dictionary<string, NoiseRemoval> MicNoise { get; set; } = [];
    public NoiseRemoval NoiseFor(string micId) => MicNoise.TryGetValue(micId, out var m) ? m : NoiseRemoval.Light;
    /// <summary>80 Hz high-pass on every microphone (rumble, desk bumps, air conditioning).</summary>
    public bool LowCut { get; set; } = true;
    /// <summary>Slowly levels each microphone toward −16 LUFS.</summary>
    public bool AutoLevel { get; set; }
    /// <summary>Softens harsh "s" sounds on every microphone.</summary>
    public bool DeEss { get; set; }
    public List<string> MicIds { get; set; } = [];
    public Dictionary<string, double> MicVolumes { get; set; } = [];
    public HashSet<string> MutedMics { get; set; } = [];
    /// <summary>Render device that receives the finished mix (e.g. a virtual cable whose other end meetings use as a mic). Null = off.</summary>
    public string? MeetingOutputId { get; set; }
}

/// <summary>
/// Microphone mixer on Windows AudioGraph: inputs → submix (EQ, limiter, master volume) → frame tap.
/// Raises <see cref="MixReady"/> with interleaved float samples for the recorder and keeps peak meters.
/// Nothing is played to the speakers; the mix only goes to <see cref="AudioMixSettings.MeetingOutputId"/> when one is chosen
/// (a virtual cable, so meetings hear the processed voice).
/// </summary>
public sealed class AudioEngine : IAsyncDisposable
{
    public const int SampleRate = 48000, Channels = 2;
    public static readonly double[] EqCentres = [100, 600, 2500, 8000];

    private AudioGraph? _graph;
    private AudioSubmixNode? _submix;
    private AudioFrameOutputNode? _masterTap;
    private EqualizerEffectDefinition? _eq;
    private LimiterEffectDefinition? _limiter;
    private readonly List<AudioTrack> _tracks = [];

    public IReadOnlyList<AudioTrack> Tracks => _tracks;
    public AudioMixSettings Mix { get; private set; } = new();
    public float MasterPeakL { get; private set; }
    public float MasterPeakR { get; private set; }
    public bool IsRunning => _graph is not null;
    public string? LastError { get; private set; }
    /// <summary>Non-fatal messages from the last start (e.g. a mic that could not use noise suppression).</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Interleaved stereo float samples of the final mix (48 kHz), raised once per audio quantum.</summary>
    public event Action<float[]>? MixReady;

    public static async Task<IReadOnlyList<(string Id, string Name)>> ListMicrophonesAsync()
    {
        var all = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
        return all.Where(d => d.IsEnabled).Select(d => (d.Id, d.Name)).ToList();
    }

    /// <summary>The system default microphone if present in the list, else the first one.</summary>
    public static List<(string Id, string Name)> DefaultMic(IReadOnlyList<(string Id, string Name)> mics)
    {
        if (mics.Count == 0) return [];
        foreach (var role in new[] { Windows.Media.Devices.AudioDeviceRole.Communications, Windows.Media.Devices.AudioDeviceRole.Default })
        {
            string id = Windows.Media.Devices.MediaDevice.GetDefaultAudioCaptureId(role);
            var hit = mics.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (hit.Id is not null) return [hit];
        }
        return [mics[0]];
    }

    /// <summary>
    /// Asks Windows for microphone permission (shows the system consent box the first time). AudioGraph never
    /// prompts on its own and reports an unanswered consent as DeviceNotAvailable.
    /// </summary>
    public static async Task<bool> RequestAccessAsync()
    {
        try
        {
            using var probe = new MediaCapture();
            await probe.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio });
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (Exception) { return true; } // other failures (no mic) are reported later by the graph
    }

    public async Task StartAsync(AudioMixSettings mix)
    {
        await StopAsync();
        Mix = mix;
        Notes.Clear(); LastError = null;
        var settings = new AudioGraphSettings(AudioRenderCategory.Media)
        {
            EncodingProperties = AudioEncodingProperties.CreatePcm(SampleRate, Channels, 32),
            QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault,
        };
        settings.EncodingProperties.Subtype = MediaEncodingSubtypes.Float;
        DeviceInformation? output = null;
        if (mix.MeetingOutputId is { Length: > 0 } outId)
        {
            try { output = await DeviceInformation.CreateFromIdAsync(outId); }
            catch { Notes.Add("The chosen meeting output is not connected; sending nowhere."); }
            if (output is not null) settings.PrimaryRenderDevice = output;
        }
        var created = await AudioGraph.CreateAsync(settings);
        if (created.Status != AudioGraphCreationStatus.Success)
            throw new InvalidOperationException($"Audio engine unavailable: {created.Status}");
        _graph = created.Graph;

        _submix = _graph.CreateSubmixNode();
        _eq = new EqualizerEffectDefinition(_graph);
        for (int i = 0; i < 4; i++) { _eq.Bands[i].FrequencyCenter = EqCentres[i]; _eq.Bands[i].Bandwidth = 1.5; }
        _limiter = new LimiterEffectDefinition(_graph) { Loudness = 1000, Release = 10 };
        _submix.EffectDefinitions.Add(_eq);
        _submix.EffectDefinitions.Add(_limiter);
        _masterTap = _graph.CreateFrameOutputNode(_graph.EncodingProperties);
        _submix.AddOutgoingConnection(_masterTap);
        if (output is not null)
        {
            var o = await _graph.CreateDeviceOutputNodeAsync();
            if (o.Status == AudioDeviceNodeCreationStatus.Success) { _submix.AddOutgoingConnection(o.DeviceOutputNode); OutputName = output.Name; }
            else Notes.Add($"Couldn't open {output.Name}: {o.Status}");
        }

        var mics = await ListMicrophonesAsync();
        // Default to Windows' own default microphone (communications role, then console) when none is chosen yet.
        var chosen = mix.MicIds.Count > 0 ? mics.Where(m => mix.MicIds.Contains(m.Id)).ToList() : DefaultMic(mics);
        foreach (var (id, name) in chosen) await AddTrackAsync(id, name);

        ApplyMix();
        _graph.QuantumStarted += OnQuantum;
        _graph.UnrecoverableErrorOccurred += (_, e) => { LastError = e.Error.ToString(); Failed = true; };
        Failed = false;
        LastQuantumAt = DateTime.UtcNow;
        _graph.Start();
    }

    private async Task AddTrackAsync(string id, string name)
    {
        if (_graph is null || _submix is null) return;
        var device = await DeviceInformation.CreateFromIdAsync(id);
        // Communications category turns on Windows' voice processing (noise suppression, echo cancellation).
        var category = Mix.NoiseSuppression ? MediaCategory.Communications : MediaCategory.Media;
        var result = await _graph.CreateDeviceInputNodeAsync(category, _graph.EncodingProperties, device);
        if (result.Status != AudioDeviceNodeCreationStatus.Success && category == MediaCategory.Communications)
        {
            // Some USB mics refuse Windows voice processing; open them plainly rather than not at all.
            var plain = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media, _graph.EncodingProperties, device);
            if (plain.Status == AudioDeviceNodeCreationStatus.Success)
            {
                result = plain;
                Notes.Add($"{name}: noise suppression isn't available for this microphone, so it's captured as is.");
            }
        }
        if (result.Status != AudioDeviceNodeCreationStatus.Success) { LastError = $"{name}: {result.Status}"; return; }
        var track = new AudioTrack { DeviceId = id, Name = name, Input = result.DeviceInputNode };
        (track.Chain, track.VoiceNote) = await Task.Run(() => BuildChain(id));
        // Device → raw tap → voice chain (in OnQuantum) → frame input node → mix. Always wired this way, so
        // noise removal, low cut and auto level can change while audio keeps running.
        track.Raw = _graph.CreateFrameOutputNode(_graph.EncodingProperties);
        result.DeviceInputNode.AddOutgoingConnection(track.Raw);
        var processed = _graph.CreateFrameInputNode(_graph.EncodingProperties);
        track.Processed = new PushSource(processed);
        track.Input = processed;
        processed.Start();
        track.Tap = _graph.CreateFrameOutputNode(_graph.EncodingProperties);
        track.Input!.AddOutgoingConnection(_submix);
        track.Input.AddOutgoingConnection(track.Tap, 1.0); // meter tap sees the raw mic
        track.Volume = Mix.MicVolumes.TryGetValue(id, out var v) ? v : 1;
        track.Muted = Mix.MutedMics.Contains(id);
        // The tap connection must stay at unity even when the mix gain changes: OutgoingGain applies to all
        // outgoing connections, so meters show post-gain; that is what users expect from a fader meter.
        _tracks.Add(track);
    }

    /// <summary>The voice chain this mic's settings ask for (null when every step is off), and a note if its noise removal can't run.</summary>
    private (VoiceChain? Chain, string? Note) BuildChain(string id)
    {
        var mode = Mix.NoiseFor(id);
        bool any = Mix.LowCut || Mix.AutoLevel || Mix.DeEss;
        if (mode == NoiseRemoval.Off && !any) return (null, null);
        try { return (new VoiceChain(mode, Mix.LowCut, Mix.AutoLevel, Mix.DeEss), null); }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                                       or InvalidOperationException or PlatformNotSupportedException)
        {
            string note = mode == NoiseRemoval.Studio ? "Studio voice can't run on this PC." : "Noise removal can't run on this PC.";
            return (any ? new VoiceChain(NoiseRemoval.Off, Mix.LowCut, Mix.AutoLevel, Mix.DeEss) : null, note);
        }
    }

    /// <summary>
    /// Applies changed noise removal, low cut, auto level or de-esser settings to every open microphone
    /// without stopping audio: new chains are built off the audio thread and swapped in between quanta.
    /// </summary>
    public async Task UpdateVoiceAsync()
    {
        foreach (var t in _tracks.ToList())
        {
            var (chain, note) = await Task.Run(() => BuildChain(t.DeviceId));
            VoiceChain? old;
            lock (t.ChainLock) { old = t.Chain; t.Chain = chain; t.VoiceNote = note; }
            old?.Dispose();
        }
    }

    /// <summary>Pushes master volume, limiter and EQ settings into the graph.</summary>
    /// <summary>Silences the whole mix (privacy shutter "away") without touching the saved volumes.</summary>
    public bool MuteAll { get; set; }

    public void ApplyMix()
    {
        if (_submix is null || _eq is null || _limiter is null) return;
        _submix.OutgoingGain = MuteAll ? 0 : Math.Clamp(Mix.MasterVolume, 0, 2);
        for (int i = 0; i < 4; i++) _eq.Bands[i].Gain = DbToEqGain(Mix.EqDb.ElementAtOrDefault(i));
        if (Mix.Limiter) _submix.EnableEffectsByDefinition(_limiter); else _submix.DisableEffectsByDefinition(_limiter);
        bool flat = Mix.EqDb.All(d => Math.Abs(d) < 0.05);
        if (flat) _submix.DisableEffectsByDefinition(_eq); else _submix.EnableEffectsByDefinition(_eq);
        foreach (var t in _tracks)
        {
            Mix.MicVolumes[t.DeviceId] = t.Volume;
            if (t.Muted) Mix.MutedMics.Add(t.DeviceId); else Mix.MutedMics.Remove(t.DeviceId);
        }
    }

    /// <summary>AudioGraph EQ gain is linear 0.126..7.94 (−18..+18 dB); 1 = flat.</summary>
    public static double DbToEqGain(double db) => Math.Clamp(Math.Pow(10, Math.Clamp(db, -12, 12) / 20.0), 0.126, 7.94);

    /// <summary>When the engine last processed audio (UTC). A stall means the device or driver stopped.</summary>
    public DateTime LastQuantumAt { get; private set; }
    /// <summary>Set when Windows reports the audio graph can't continue (device unplugged, driver reset).</summary>
    public bool Failed { get; private set; }

    /// <summary>True when the engine should be restarted: it failed, or it stopped producing audio for <paramref name="stall"/>.</summary>
    public bool NeedsRestart(DateTime nowUtc, TimeSpan stall) => IsRunning && (Failed || (LastQuantumAt != default && nowUtc - LastQuantumAt > stall));

    private void OnQuantum(AudioGraph sender, object args)
    {
        LastQuantumAt = DateTime.UtcNow;
        try { ProcessQuantum(); }
        catch (Exception ex) { LastError = ex.Message; } // runs 100×/s: never let one bad buffer flood the crash log
    }

    private void ProcessQuantum()
    {
        foreach (var t in _tracks)
            if (t is { Raw: { } raw, Processed: { } push })
            {
                var (_, _, raw48) = Read(raw.GetFrame(), wantSamples: true);
                if (raw48 is not { Length: > 0 }) continue;
                // Voice processing must never cost the voice: on any error, pass the untouched sound and switch it off.
                var safe = (float[])raw48.Clone();
                try { lock (t.ChainLock) t.Chain?.Process(raw48); }
                catch (Exception ex) { raw48 = safe; lock (t.ChainLock) t.Chain = null; t.VoiceNote = "Voice processing stopped: " + ex.Message; LastError = ex.Message; }
                push.Write(raw48);
            }
        foreach (var t in _tracks)
            if (t.Tap is { } tap) { var (l, r, _) = Read(tap.GetFrame(), wantSamples: false); t.PeakL = Decay(t.PeakL, l); t.PeakR = Decay(t.PeakR, r); }
        if (_masterTap is null) return;
        var (ml, mr, samples) = Read(_masterTap.GetFrame(), wantSamples: MixReady is not null);
        MasterPeakL = Decay(MasterPeakL, ml); MasterPeakR = Decay(MasterPeakR, mr);
        if (samples is { Length: > 0 }) MixReady?.Invoke(samples);
    }

    /// <summary>Meter ballistics: instant attack, ~300 ms release at 10 ms quanta.</summary>
    public static float Decay(float previous, float now) => now >= previous ? now : Math.Max(now, previous * 0.92f);

    /// <summary>Feeds an AudioFrameInputNode from a small FIFO of pushed samples.</summary>
    internal sealed class PushSource
    {
        private readonly Queue<float> _fifo = new();
        private readonly object _lock = new();
        public PushSource(AudioFrameInputNode node) => node.QuantumStarted += OnQuantum;

        public void Write(float[] stereo)
        {
            lock (_lock)
            {
                foreach (var f in stereo) _fifo.Enqueue(f);
                int max = SampleRate * Channels / 5; // 200 ms
                while (_fifo.Count > max) _fifo.Dequeue();
            }
        }

        private unsafe void OnQuantum(AudioFrameInputNode node, FrameInputNodeQuantumStartedEventArgs args)
        {
            int samples = args.RequiredSamples;
            if (samples <= 0) return;
            int floats = samples * Channels;
            var frame = new AudioFrame((uint)(floats * sizeof(float)));
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            {
                ((IMemoryBufferByteAccess)WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference)).GetBuffer(out byte* data, out uint _);
                var dst = (float*)data;
                lock (_lock) for (int i = 0; i < floats; i++) dst[i] = _fifo.Count > 0 ? _fifo.Dequeue() : 0f;
            }
            node.AddFrame(frame);
        }
    }

    private static unsafe (float L, float R, float[]? Samples) Read(AudioFrame frame, bool wantSamples)
    {
        using (frame)
        using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Read))
        using (var reference = buffer.CreateReference())
        {
            // CsWinRT objects need As<T>() for classic COM interfaces; a direct cast throws InvalidCastException.
            WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference).GetBuffer(out byte* data, out uint capacity);
            int count = (int)(buffer.Length / sizeof(float));
            var span = new ReadOnlySpan<float>(data, count);
            var (l, r) = Peaks(span);
            return (l, r, wantSamples ? span.ToArray() : null);
        }
    }

    /// <summary>Peak absolute value per channel of interleaved stereo samples.</summary>
    public static (float L, float R) Peaks(ReadOnlySpan<float> interleaved)
    {
        float l = 0, r = 0;
        for (int i = 0; i + 1 < interleaved.Length; i += 2)
        {
            l = Math.Max(l, Math.Abs(interleaved[i]));
            r = Math.Max(r, Math.Abs(interleaved[i + 1]));
        }
        return (Math.Min(l, 1), Math.Min(r, 1));
    }

    /// <summary>Name of the device the mix is being sent to, or null.</summary>
    public string? OutputName { get; private set; }

    /// <summary>Playback devices the mix can be sent to.</summary>
    public static async Task<List<(string Id, string Name)>> ListOutputsAsync()
    {
        var all = await DeviceInformation.FindAllAsync(Windows.Media.Devices.MediaDevice.GetAudioRenderSelector());
        return all.Select(d => (d.Id, d.Name)).ToList();
    }

    /// <summary>A virtual cable's playback end (VB-Audio CABLE, VoiceMeeter), if one is installed.</summary>
    public static (string Id, string Name)? FindVirtualCable(IEnumerable<(string Id, string Name)> outputs) =>
        outputs.Cast<(string Id, string Name)?>().FirstOrDefault(o => IsVirtualCable(o!.Value.Name));

    public static bool IsVirtualCable(string name) =>
        name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a track fed by code instead of a device (the phone's microphone). Push 48 kHz stereo floats with
    /// <see cref="PushAudio"/>; gaps play as silence and a backlog over ~200 ms is dropped to keep latency low.
    /// </summary>
    public AudioTrack? AddPushTrack(string id, string name)
    {
        if (_graph is null || _submix is null) return null;
        var existing = _tracks.FirstOrDefault(t => t.DeviceId == id);
        if (existing is not null) return existing;
        var node = _graph.CreateFrameInputNode(_graph.EncodingProperties);
        var push = new PushSource(node);
        var track = new AudioTrack { DeviceId = id, Name = name, Input = node, Push = push };
        (track.Chain, track.VoiceNote) = BuildChain(id);
        track.Tap = _graph.CreateFrameOutputNode(_graph.EncodingProperties);
        node.AddOutgoingConnection(_submix);
        node.AddOutgoingConnection(track.Tap, 1.0);
        track.Volume = Mix.MicVolumes.TryGetValue(id, out var v) ? v : 1;
        track.Muted = Mix.MutedMics.Contains(id);
        _tracks.Add(track);
        node.Start();
        return track;
    }

    public void PushAudio(string trackId, float[] stereo48k)
    {
        var t = _tracks.FirstOrDefault(t => t.DeviceId == trackId);
        if (t?.Push is null) return;
        lock (t.ChainLock) t.Chain?.Process(stereo48k);
        t.Push.Write(stereo48k);
    }

    public void RemovePushTrack(string id)
    {
        var t = _tracks.FirstOrDefault(x => x.DeviceId == id && x.Push is not null);
        if (t is null) return;
        _tracks.Remove(t);
        try { t.Input?.Stop(); (t.Input as IDisposable)?.Dispose(); t.Tap?.Dispose(); } catch { }
        lock (t.ChainLock) { t.Chain?.Dispose(); t.Chain = null; }
    }

    public Task StopAsync()
    {
        if (_graph is { } g)
        {
            g.QuantumStarted -= OnQuantum;
            g.Stop();
            g.Dispose();
        }
        OutputName = null;
        _graph = null; _submix = null; _masterTap = null; _eq = null; _limiter = null;
        foreach (var t in _tracks) lock (t.ChainLock) { t.Chain?.Dispose(); t.Chain = null; } // graph stopped first
        _tracks.Clear();
        MasterPeakL = MasterPeakR = 0;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess { void GetBuffer(out byte* buffer, out uint capacity); }
}
