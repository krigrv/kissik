using System.Diagnostics;

namespace OpenKamera.Core;

public enum UsbState { NoAdb, NoPhone, Unauthorized, Offline, Ready, Connected, Failed }

/// <summary>Result of a USB connect attempt, with a plain-language next step.</summary>
public sealed record UsbResult(UsbState State, string Message, string? Device = null);

/// <summary>
/// Android phone over a USB cable: uses Google's adb tool to forward the phone's "localhost" to KISSIK, then opens
/// the KISSIK page on the phone. Works without Wi-Fi and without any certificate warning.
/// </summary>
public static class PhoneUsb
{
    /// <summary>adb on PATH, in the Android SDK, or next to a scrcpy install.</summary>
    public static string? FindAdb()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), "adb.exe"));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "platform-tools", "adb.exe"));
        candidates.Add(@"C:\platform-tools\adb.exe");
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), local })
            if (Directory.Exists(root))
                foreach (var d in Directory.EnumerateDirectories(root, "scrcpy*")) candidates.Add(Path.Combine(d, "adb.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Parses `adb devices` output into (serial, state).</summary>
    public static List<(string Serial, string State)> ParseDevices(string output) =>
        output.Split('\n').Skip(1)
            .Select(l => l.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2)
            .Select(p => (p[0], p[1])).ToList();

    /// <summary>Forwards the phone's localhost:<paramref name="port"/> to this PC and opens <paramref name="link"/> on the phone.</summary>
    public static async Task<UsbResult> ConnectAsync(int port, string link)
    {
        var adb = FindAdb();
        if (adb is null)
            return new(UsbState.NoAdb, "USB needs Google's free “Android platform tools” (adb). Download them, unzip to C:\\platform-tools, then try again.");
        var (code, devices) = await RunAsync(adb, "devices");
        if (code != 0) return new(UsbState.Failed, "adb didn't start: " + devices.Trim());
        var list = ParseDevices(devices);
        if (list.Count == 0)
            return new(UsbState.NoPhone, "No phone found on USB. Plug it in with a data cable (some cables only charge), and turn on USB debugging: Settings → About phone → tap Build number 7 times, then Developer options → USB debugging.");
        var (serial, state) = list.FirstOrDefault(d => d.State == "device") is { Serial: not null } ok && ok.State == "device" ? ok : list[0];
        if (state == "unauthorized")
            return new(UsbState.Unauthorized, "Your phone is asking “Allow USB debugging?”. Unlock it, tick “Always allow from this computer”, tap Allow, then try again.", serial);
        if (state != "device")
            return new(UsbState.Offline, $"The phone is connected but {state}. Unplug and replug the cable, or switch the USB mode to “File transfer”, then try again.", serial);
        var (rc, rout) = await RunAsync(adb, $"-s {serial} reverse tcp:{port} tcp:{port}");
        if (rc != 0) return new(UsbState.Failed, "Couldn't link the phone to this PC: " + rout.Trim(), serial);
        var (oc, oout) = await RunAsync(adb, $"-s {serial} shell am start -a android.intent.action.VIEW -d \"{link}\"");
        return oc == 0
            ? new(UsbState.Connected, "Opened KISSIK on your phone. Tap Start camera there.", serial)
            : new(UsbState.Ready, $"Linked over USB. On your phone, open {link}", serial);
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { p.Kill(); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, await outTask + await errTask);
    }
}
