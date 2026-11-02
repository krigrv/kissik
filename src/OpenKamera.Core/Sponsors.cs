using System.Text.Json;

namespace OpenKamera.Core;

/// <summary>
/// One sponsored pick: a product suggestion a brand pays to have shown when it fits the user's setup.
/// <paramref name="When"/> is the situation it fits: "camera-upgrade" (camera below 1080p), "low-light",
/// "no-mic" or "any".
/// </summary>
public sealed record SponsorPick(string Id, string Brand, string Title, string Body, string Url, string When,
    DateTime? Until = null);

/// <summary>What KISSIK knows about the setup, worked out on this PC. Nothing here leaves the device.</summary>
public sealed record SponsorContext(int CameraHeight, bool LowLight, bool HasMic);

/// <summary>
/// Sponsored picks: subtle, clearly labelled, never drawn into the video. The feed is a small public JSON list that
/// is downloaded and cached; choosing a pick happens on this PC, so no personal data is ever sent.
/// </summary>
public static class Sponsors
{
    public const string FeedUrl = "https://raw.githubusercontent.com/krigrv/OpenKamera/main/website/sponsors.json";

    public static IReadOnlyList<SponsorPick> Parse(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<SponsorPick>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return list?.Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Title)
                                    && Uri.TryCreate(p.Url, UriKind.Absolute, out var u) && u.Scheme == "https").ToList() ?? [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// The best pick for this setup, or null. Situation-specific picks win over "any"; dismissed and expired picks are
    /// skipped. Among equals the choice rotates by day so one brand doesn't hold the slot forever.
    /// </summary>
    public static SponsorPick? Pick(IReadOnlyList<SponsorPick> feed, SponsorContext ctx, ISet<string> dismissed, DateTime now)
    {
        bool Fits(SponsorPick p) => p.When switch
        {
            "camera-upgrade" => ctx.CameraHeight > 0 && ctx.CameraHeight < 1080,
            "low-light" => ctx.LowLight,
            "no-mic" => !ctx.HasMic,
            "any" => true,
            _ => false,
        };
        var live = feed.Where(p => !dismissed.Contains(p.Id) && (p.Until is null || p.Until > now) && Fits(p)).ToList();
        var specific = live.Where(p => p.When != "any").ToList();
        var pool = specific.Count > 0 ? specific : live;
        if (pool.Count == 0) return null;
        return pool.OrderBy(p => p.Id, StringComparer.Ordinal).ElementAt(now.DayOfYear % pool.Count);
    }
}
