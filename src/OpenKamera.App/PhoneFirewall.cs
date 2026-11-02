using System.Diagnostics;

namespace OpenKamera_App;

/// <summary>
/// Windows Firewall blocks phones from reaching the phone-camera server until there's an inbound rule. This adds one
/// narrow rule (one TCP port, local-network devices only) after the user approves the admin prompt.
/// </summary>
public static class PhoneFirewall
{
    public const string RuleName = "OpenKemera phone camera"; // internal: keep so the rule users already approved still counts

    public static bool RuleExists(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && output.Contains(port.ToString());
        }
        catch { return false; }
    }

    /// <summary>Adds the rule (UAC prompt). Returns false if the user declined or it failed.</summary>
    public static async Task<bool> AllowAsync(int port)
    {
        string args = $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} remoteip=localsubnet profile=any";
        var psi = new ProcessStartInfo("netsh", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch { return false; } // declined
    }
}
