using OpenKamera.Core;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>
/// A stand-in for KISSIK's phone side, for checking the Android app (on a phone or an emulator) without touching a
/// running KISSIK. Runs only when KISSIK_PHONE_HARNESS names a folder: it writes the pairing token to token.txt,
/// a line a second to log.txt (connected, frames, video packets, sound blocks, the phone's last status), the
/// newest JPEG to frame.jpg, and sends each new line of cmd.txt to the phone. Stops when stop.txt appears or
/// after KISSIK_PHONE_SECONDS (default 900).
/// </summary>
public class PhoneHarnessTests(ITestOutputHelper log)
{
    [Fact]
    public async Task RunsAServerForAPhone()
    {
        string? dir = Environment.GetEnvironmentVariable("KISSIK_PHONE_HARNESS");
        if (dir is null) { log.WriteLine("skipped: KISSIK_PHONE_HARNESS not set"); return; }
        Directory.CreateDirectory(dir);
        int port = int.TryParse(Environment.GetEnvironmentVariable("KISSIK_PHONE_PORT"), out int p) ? p : 47830;
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("KISSIK_PHONE_SECONDS"), out int s) ? s : 900;
        foreach (string f in new[] { "log.txt", "cmd.txt", "stop.txt", "frame.jpg" }) File.Delete(Path.Combine(dir, f));
        File.WriteAllText(Path.Combine(dir, "cmd.txt"), "");

        await using var server = new PhoneServer(Path.Combine(dir, "state"));
        long frames = 0, video = 0, keys = 0, audio = 0, videoBytes = 0;
        byte[]? last = null;
        string status = "";
        server.FrameReceived += j => { Interlocked.Increment(ref frames); last = j; };
        server.VideoPacketReceived += v => { Interlocked.Increment(ref video); Interlocked.Add(ref videoBytes, v.Length); if (v.Length > 1 && (v[1] & 1) != 0) Interlocked.Increment(ref keys); };
        server.AudioReceived += _ => Interlocked.Increment(ref audio);
        server.StatusChanged += st => status = $"{st.Device} {st.Width}x{st.Height} rot {st.Rotation} {st.Facing} codec {st.Codec} codecs [{string.Join(",", st.Codecs)}] lenses {st.Lenses.Count} fps {st.Fps} battery {st.Battery}";
        server.Start(port);
        File.WriteAllText(Path.Combine(dir, "token.txt"), $"{server.Port} {server.UsbPort} {server.Token}");

        int sent = 0;
        long f0 = 0, v0 = 0, a0 = 0;
        for (int i = 0; i < seconds && !File.Exists(Path.Combine(dir, "stop.txt")); i++)
        {
            await Task.Delay(1000);
            string[] cmds;
            try { cmds = File.ReadAllLines(Path.Combine(dir, "cmd.txt")); } catch { cmds = []; }
            for (; sent < cmds.Length; sent++)
                if (cmds[sent].Trim().Length > 0)
                    try { await server.SendAsync(cmds[sent].Trim()); } catch (Exception ex) { File.AppendAllText(Path.Combine(dir, "log.txt"), $"send failed: {ex.Message}\n"); }
            long f = Interlocked.Read(ref frames), v = Interlocked.Read(ref video), a = Interlocked.Read(ref audio);
            File.AppendAllText(Path.Combine(dir, "log.txt"),
                $"{DateTime.Now:HH:mm:ss} connected {server.PhoneConnected} jpeg +{f - f0} video +{v - v0} (keys {keys}, {Interlocked.Read(ref videoBytes) / 1024} KB) sound +{a - a0} wrong {server.WrongLinkVisits} tlsfail {server.TlsFailures} | {status}\n");
            (f0, v0, a0) = (f, v, a);
            if (last is { } jpg) try { File.WriteAllBytes(Path.Combine(dir, "frame.jpg"), jpg); } catch { }
        }
    }
}
