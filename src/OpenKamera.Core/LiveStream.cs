using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace OpenKamera.Core;

/// <summary>A place to stream to. The key is stored encrypted for this Windows user (DPAPI).</summary>
public sealed class StreamDestination
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public string Key
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedKey)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedKey), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        set => ProtectedKey = string.IsNullOrEmpty(value) ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    /// <summary>Server URL + key, as RTMP servers expect ("rtmp://server/app/key").</summary>
    public string FullUrl => string.IsNullOrEmpty(Key) ? Url : Url.TrimEnd('/') + "/" + Key;

    public static IReadOnlyList<(string Name, string Url)> Presets { get; } =
    [
        ("YouTube", "rtmp://a.rtmp.youtube.com/live2"),
        ("Twitch", "rtmp://live.twitch.tv/app"),
        ("Facebook", "rtmps://live-api-s.facebook.com:443/rtmp"),
        ("Kick", "rtmps://fa723fc1b171.global-contribute.live-video.net/app"),
        ("Custom RTMP", "rtmp://"),
    ];
}

/// <summary>
/// Livestreaming through FFmpeg: KISSIK's finished frames (BGRA) go in on stdin, the audio mix on a named pipe,
/// FFmpeg encodes H.264 + AAC once and sends it to every destination (tee muxer). Frames are dropped, never queued
/// up, when the network is slower than the picture, so the stream stays live.
/// </summary>
public sealed class LiveStream : IAsyncDisposable
{
    private readonly BlockingCollection<(byte[] Px, int W, int H)> _video = new(boundedCapacity: 8);
    private readonly BlockingCollection<float[]> _audio = new(boundedCapacity: 200);
    private Process? _ffmpeg;
    private NamedPipeServerStream? _audioPipe;
    private readonly StringBuilder _log = new();
    private Task? _videoPump, _audioPump;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int FramesSent { get; private set; }
    public int FramesDropped { get; private set; }
    public bool IsRunning => _ffmpeg is { HasExited: false };
    /// <summary>The last lines FFmpeg printed (for "why did it stop?").</summary>
    public string Log { get { lock (_log) return _log.ToString(); } }

    /// <summary>Streams at the given size, capped at 1080p (what streaming sites accept); bigger frames are scaled down on the stream's own thread.</summary>
    public LiveStream(int width, int height, int fps)
    {
        if (height > 1080) { width = (int)Math.Round(width * 1080.0 / height); height = 1080; }
        Width = width & ~1; Height = height & ~1; Fps = Math.Max(1, fps);
    }

    public static string? FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
            if (File.Exists(p)) return p;
        }
        foreach (var p in new[] { @"C:\ffmpeg\bin\ffmpeg.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe") })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Kbps for the video: about 6 Mbps at 1080p30, scaled by pixels and frame rate, 1.5–9 Mbps.</summary>
    /// <summary>A destination may be an rtmp(s)/srt URL or a local file path, never quotes or control characters.</summary>
    public static bool IsSafeDestination(string o) =>
        !string.IsNullOrWhiteSpace(o) && o.Length < 2048 && !o.Any(ch => ch == '"' || char.IsControl(ch))
        && (o.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) || o.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase)
            || o.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) || Path.IsPathFullyQualified(o));

    public static int VideoKbps(int w, int h, int fps) => (int)Math.Clamp(w * (long)h * fps * 6000L / (1920L * 1080 * 30), 1500, 9000);

    /// <summary>FFmpeg arguments: raw video on stdin, f32le audio on a pipe, H.264 (GPU encoder if any) + AAC, to one or many outputs.</summary>
    public static string BuildArgs(int w, int h, int fps, string? audioPipe, IReadOnlyList<string> outputs, string videoCodec = "libx264")
    {
        // Streaming sites take at most 1080p; bigger pictures are scaled down.
        int ow = w, oh = h;
        if (oh > 1080) { ow = (int)Math.Round(w * 1080.0 / h) & ~1; oh = 1080; }
        int kbps = VideoKbps(ow, oh, fps);
        var a = new StringBuilder("-hide_banner -loglevel warning -nostats ");
        // Start fast (no long input probing) and give each input room so a hiccup doesn't stall the other.
        a.Append($"-thread_queue_size 64 -probesize 32 -analyzeduration 0 -use_wallclock_as_timestamps 1 -f rawvideo -pix_fmt bgra -s {w}x{h} -r {fps} -i pipe:0 ");
        a.Append(audioPipe is null ? "-f lavfi -i anullsrc=r=48000:cl=stereo " : $"-thread_queue_size 512 -use_wallclock_as_timestamps 1 -f f32le -ar 48000 -ac 2 -i \"{audioPipe}\" ");
        if (oh != h) a.Append($"-vf scale={ow}:{oh} ");
        a.Append($"-c:v {videoCodec} ");
        a.Append(videoCodec == "libx264" ? "-preset veryfast -tune zerolatency " : "");
        a.Append($"-b:v {kbps}k -maxrate {kbps}k -bufsize {kbps * 2}k -g {fps * 2} -pix_fmt yuv420p ");
        a.Append("-c:a aac -b:a 160k -ar 48000 -af aresample=async=1 -vsync cfr ");
        foreach (var o in outputs)
            if (!IsSafeDestination(o)) throw new ArgumentException("That stream address has characters a stream address can't have.");
        if (outputs.Count == 1) a.Append($"-f flv \"{outputs[0]}\"");
        else a.Append("-map 0:v -map 1:a -f tee \"" + string.Join("|", outputs.Select(o => $"[f=flv:onfail=ignore]{o.Replace("|", "%7C")}")) + "\"");
        return a.ToString();
    }

    public async Task StartAsync(IReadOnlyList<string> outputs, bool withAudio, string? ffmpeg = null)
    {
        ffmpeg ??= FindFfmpeg() ?? throw new FileNotFoundException("FFmpeg isn't installed. Get it free from ffmpeg.org (or run: winget install Gyan.FFmpeg), then try again.");
        if (outputs.Count == 0) throw new ArgumentException("Add at least one destination with its stream key.");
        string? pipePath = null;
        if (withAudio)
        {
            string name = "kissik-audio-" + Guid.NewGuid().ToString("N")[..8];
            _audioPipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            pipePath = $@"\\.\pipe\{name}";
        }
        string codec = await PickEncoderAsync(ffmpeg);
        var psi = new ProcessStartInfo(ffmpeg, BuildArgs(Width, Height, Fps, pipePath, outputs, codec))
        {
            RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg didn't start.");
        _ffmpeg.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (_log) { _log.AppendLine(e.Data); if (_log.Length > 8000) _log.Remove(0, _log.Length - 8000); } };
        _ffmpeg.BeginErrorReadLine();
        var stdin = _ffmpeg.StandardInput.BaseStream;
        _videoPump = Task.Run(async () =>
        {
            try
            {
                foreach (var (px, w, h) in _video.GetConsumingEnumerable())
                {
                    var frame = w == Width && h == Height ? px : OutputFit.Stretch(px, w, h, Width, Height).Pixels;
                    await stdin.WriteAsync(frame); FramesSent++;
                }
            }
            catch { /* FFmpeg ended: the stream stopped */ }
            finally { try { stdin.Close(); } catch { } }
        });
        if (_audioPipe is { } pipe)
            _audioPump = Task.Run(async () =>
            {
                try
                {
                    await pipe.WaitForConnectionAsync();
                    foreach (var s in _audio.GetConsumingEnumerable())
                    {
                        var bytes = new byte[s.Length * 4]; Buffer.BlockCopy(s, 0, bytes, 0, bytes.Length);
                        await pipe.WriteAsync(bytes);
                    }
                }
                catch { }
            });
    }

    /// <summary>Uses the graphics card's H.264 encoder when FFmpeg can open it (much less CPU), else x264.</summary>
    private static async Task<string> PickEncoderAsync(string ffmpeg)
    {
        foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
        {
            var psi = new ProcessStartInfo(ffmpeg, $"-hide_banner -loglevel error -f lavfi -i color=black:s=320x180:r=30 -frames:v 2 -c:v {enc} -f null -")
            { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            try
            {
                using var p = Process.Start(psi)!;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await p.WaitForExitAsync(cts.Token);
                if (p.ExitCode == 0) return enc;
            }
            catch { }
        }
        return "libx264";
    }

    /// <summary>Frame thread: any size BGRA (scaled later). Dropped if FFmpeg is behind, so the stream stays live.</summary>
    public void Push(byte[] bgra, int w, int h)
    {
        if (!IsRunning) { FramesDropped++; return; }
        if (!_video.TryAdd((bgra, w, h))) FramesDropped++;
    }

    public void PushAudio(float[] stereo48k) { if (_audioPipe is not null && IsRunning) _audio.TryAdd(stereo48k); }

    public async Task StopAsync()
    {
        _video.CompleteAdding(); _audio.CompleteAdding();
        try { if (_videoPump is not null) await _videoPump.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        if (_ffmpeg is { } p)
        {
            try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); } catch { try { p.Kill(); } catch { } }
            p.Dispose();
        }
        _ffmpeg = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
