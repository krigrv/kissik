using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenKamera.Core;

/// <summary>
/// Opt-in automatic crash reports. Sends only the error text (scrubbed of user names and paths), the KISSIK
/// version and the Windows version to the project's Sentry inbox. Never video, audio, files or settings.
/// </summary>
public static class CrashReports
{
    /// <summary>
    /// The Sentry project DSN (Settings → Client Keys in Sentry). Empty = not set up yet: nothing is ever sent and the
    /// app falls back to "copy the report".
    /// </summary>
    public const string Dsn = "";

    public static bool IsConfigured => TryParseDsn(Dsn, out _, out _, out _);

    /// <summary>Removes anything personal: user folder names, e-mail addresses, machine name.</summary>
    public static string Scrub(string text)
    {
        // User folder names may contain spaces, so match up to the next separator rather than the next space.
        text = Regex.Replace(text, @"(?i)([a-z]:\\Users\\)[^\\""'\r\n]+", "$1<user>");
        text = Regex.Replace(text, @"(?i)(/Users/|/home/)[^/""'\r\n]+", "$1<user>");
        text = Regex.Replace(text, @"[\w.+-]+@[\w-]+\.[\w.-]+", "<email>");
        string machine = Environment.MachineName;
        if (machine.Length > 2) text = Regex.Replace(text, Regex.Escape(machine), "<pc>", RegexOptions.IgnoreCase);
        return text.Length > 16000 ? text[^16000..] : text;
    }

    /// <summary>Splits crash.log text into individual crashes (each starts with an ISO timestamp).</summary>
    public static List<string> SplitEntries(string log) =>
        Regex.Split(log, @"(?m)^(?=\d{4}-\d{2}-\d{2}T)").Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

    internal static bool TryParseDsn(string dsn, out Uri endpoint, out string key, out string project)
    {
        endpoint = null!; key = project = "";
        if (!Uri.TryCreate(dsn, UriKind.Absolute, out var u) || u.Scheme != "https" || string.IsNullOrEmpty(u.UserInfo)) return false;
        key = u.UserInfo.Split(':')[0];
        project = u.AbsolutePath.Trim('/').Split('/').Last();
        if (project.Length == 0) return false;
        endpoint = new Uri($"https://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}/api/{project}/envelope/");
        return true;
    }

    /// <summary>The Sentry envelope for one crash (header line, item header line, event JSON).</summary>
    public static string BuildEnvelope(string dsn, string report, string appVersion, string osVersion, DateTime utcNow)
    {
        string id = Guid.NewGuid().ToString("N");
        var first = report.Split('\n').FirstOrDefault(l => Regex.IsMatch(l, @"\w+(\.\w+)*Exception")) ?? report.Split('\n')[0];
        var m = Regex.Match(first, @"([\w.]+Exception):?\s*(.*)");
        string type = m.Success ? m.Groups[1].Value : "Crash", value = m.Success ? m.Groups[2].Value.Trim() : first.Trim();
        var evt = new Dictionary<string, object?>
        {
            ["event_id"] = id, ["timestamp"] = utcNow.ToString("O"), ["platform"] = "csharp", ["level"] = "fatal",
            ["release"] = "kissik@" + appVersion, ["environment"] = "production",
            ["contexts"] = new Dictionary<string, object> { ["os"] = new Dictionary<string, string> { ["name"] = "Windows", ["version"] = osVersion } },
            ["exception"] = new { values = new[] { new { type, value = Scrub(value) } } },
            ["extra"] = new Dictionary<string, string> { ["report"] = Scrub(report) },
        };
        var sb = new StringBuilder();
        sb.Append(JsonSerializer.Serialize(new { event_id = id, dsn, sent_at = utcNow.ToString("O") })).Append('\n');
        sb.Append("{\"type\":\"event\"}\n");
        sb.Append(JsonSerializer.Serialize(evt)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Sends one crash. Returns false when not set up or the network failed (the report stays local).</summary>
    public static async Task<bool> SendAsync(string report, string appVersion, HttpClient? http = null)
    {
        if (!TryParseDsn(Dsn, out var endpoint, out var key, out _)) return false;
        bool own = http is null; http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var body = new StringContent(BuildEnvelope(Dsn, report, appVersion, Environment.OSVersion.Version.ToString(), DateTime.UtcNow),
                                         Encoding.UTF8, "application/x-sentry-envelope");
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = body };
            req.Headers.TryAddWithoutValidation("X-Sentry-Auth", $"Sentry sentry_version=7, sentry_key={key}, sentry_client=kissik/{appVersion}");
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
        finally { if (own) http.Dispose(); }
    }
}
