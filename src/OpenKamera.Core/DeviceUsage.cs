using Microsoft.Win32;

namespace OpenKamera.Core;

public enum PrivacyDevice { Camera, Microphone }

public sealed record DeviceUser(string App, bool InUse, DateTime? LastUsed);

/// <summary>
/// "Who has my camera?": reads the per-app usage record Windows keeps for its privacy indicator, so we can say
/// which app holds the camera when ours can't open it, and show who is listening to the microphone.
/// </summary>
public static class DeviceUsage
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static IReadOnlyList<DeviceUser> Read(PrivacyDevice device)
    {
        var list = new List<DeviceUser>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root + (device == PrivacyDevice.Camera ? "webcam" : "microphone"));
            if (key is null) return list;
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub is null) continue;
                if (name == "NonPackaged")
                    foreach (var n in sub.GetSubKeyNames()) { using var s = sub.OpenSubKey(n); Add(list, n, s); }
                else Add(list, name, sub);
            }
        }
        catch { /* the record is optional; no answer is better than a crash */ }
        return list.OrderByDescending(u => u.InUse).ThenByDescending(u => u.LastUsed).ToList();
    }

    /// <summary>
    /// This app's own privacy answer: "Allow", "Deny" or "Prompt" (never answered). "Prompt" for a packaged desktop
    /// app means Windows silently refuses the device, so the user has to switch it on in Settings.
    /// </summary>
    public static string? OwnConsent(PrivacyDevice device, string packagePrefix = "6FC3C948")
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root + (device == PrivacyDevice.Camera ? "webcam" : "microphone"));
            var name = key?.GetSubKeyNames().FirstOrDefault(n => n.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var sub = key!.OpenSubKey(name);
            return sub?.GetValue("Value") as string;
        }
        catch { return null; }
    }

    public static IEnumerable<string> InUseBy(PrivacyDevice device, bool excludeSelf = true) =>
        Read(device).Where(u => u.InUse && !(excludeSelf && u.App == "KISSIK")).Select(u => u.App);

    private static void Add(List<DeviceUser> list, string keyName, RegistryKey? k)
    {
        if (k is null) return;
        long start = k.GetValue("LastUsedTimeStart") is long a ? a : 0, stop = k.GetValue("LastUsedTimeStop") is long b ? b : 0;
        if (start == 0) return;
        var (inUse, last) = Interpret(start, stop);
        list.Add(new DeviceUser(FriendlyName(keyName), inUse, last));
    }

    /// <summary>A started-but-not-stopped record means the app has the device open right now.</summary>
    public static (bool InUse, DateTime? LastUsed) Interpret(long startFileTime, long stopFileTime)
    {
        bool inUse = startFileTime > 0 && stopFileTime == 0;
        long t = Math.Max(startFileTime, stopFileTime);
        return (inUse, t > 0 ? DateTime.FromFileTimeUtc(t).ToLocalTime() : null);
    }

    /// <summary>"C:#Program Files#Zoom#bin#Zoom.exe" → "Zoom"; "5319275A.WhatsAppDesktop_cv1g…" → "WhatsApp Desktop".</summary>
    public static string FriendlyName(string keyName)
    {
        if (keyName.Contains('#'))
        {
            var file = Path.GetFileNameWithoutExtension(keyName.Replace('#', Path.DirectorySeparatorChar));
            return file.ToLowerInvariant() switch
            {
                "msedge" => "Microsoft Edge", "chrome" => "Google Chrome", "brave" => "Brave", "firefox" => "Firefox",
                "zoom" => "Zoom", "ms-teams" or "teams" => "Microsoft Teams", "obs64" => "OBS Studio", "discord" => "Discord",
                _ => file,
            };
        }
        string n = keyName;
        int us = n.LastIndexOf('_'); if (us > 0) n = n[..us];          // drop the publisher hash
        int dot = n.IndexOf('.'); if (dot >= 0) n = n[(dot + 1)..];      // drop the publisher prefix
        if (n.StartsWith("6FC3C948", StringComparison.OrdinalIgnoreCase) || keyName.StartsWith("6FC3C948", StringComparison.OrdinalIgnoreCase)) return "KISSIK";
        if (n.StartsWith("Microsoft", StringComparison.Ordinal) && n.Length > 9) n = n[9..];
        // Split CamelCase: WindowsCamera → Windows Camera.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n.Length; i++)
        {
            if (i > 0 && char.IsUpper(n[i]) && char.IsLower(n[i - 1])) sb.Append(' ');
            sb.Append(n[i]);
        }
        return sb.ToString().Replace("Whats App", "WhatsApp");
    }
}
