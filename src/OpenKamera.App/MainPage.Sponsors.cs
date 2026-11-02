using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Sponsored picks: one small, clearly labelled card at the bottom of the Camera panel. Never drawn into the video,
// never a popup, never sound. The feed is downloaded once a day; the choice is made on this PC; nothing is sent.
public sealed partial class MainPage
{
    private IReadOnlyList<SponsorPick> _sponsorFeed = [];
    private SponsorPick? _sponsorShown;
    private static string SponsorCache => Path.Combine(AppPaths.Data, "sponsors.json");

    private async Task InitSponsorsAsync()
    {
        SponsorToggle.IsChecked = _settings.SponsoredPicks;
        try
        {
            if (File.Exists(SponsorCache)) _sponsorFeed = Sponsors.Parse(await File.ReadAllTextAsync(SponsorCache));
            bool stale = !File.Exists(SponsorCache) || DateTime.UtcNow - File.GetLastWriteTimeUtc(SponsorCache) > TimeSpan.FromDays(1);
            if (stale && _settings.SponsoredPicks)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                string json = await http.GetStringAsync(Sponsors.FeedUrl); // a plain GET: no cookies, ids or device details
                _sponsorFeed = Sponsors.Parse(json);
                Directory.CreateDirectory(Path.GetDirectoryName(SponsorCache)!);
                await File.WriteAllTextAsync(SponsorCache, json);
            }
        }
        catch { /* offline or no feed: simply show nothing */ }
        RefreshSponsor();
    }

    /// <summary>Re-picks for the current setup; called when the camera or picture changes.</summary>
    private void RefreshSponsor()
    {
        if (!_settings.SponsoredPicks) { SponsorCard.Visibility = Visibility.Collapsed; return; }
        int height = _session?.Mode?.Height ?? 0;
        var ctx = new SponsorContext(height, LowLight: false, HasMic: _audio.Tracks.Count > 0);
        var pick = Sponsors.Pick(_sponsorFeed, ctx, _settings.DismissedSponsors.ToHashSet(), DateTime.UtcNow);
        _sponsorShown = pick;
        SponsorCard.Visibility = pick is null ? Visibility.Collapsed : Visibility.Visible;
        if (pick is null) return;
        SponsorTitle.Text = pick.Title;
        SponsorBody.Text = pick.Body;
        SponsorBrand.Text = "Sponsored · " + pick.Brand;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SponsorCard, $"Sponsored by {pick.Brand}: {pick.Title}");
    }

    private async void SponsorOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_sponsorShown is { } p) await Windows.System.Launcher.LaunchUriAsync(new Uri(p.Url));
    }

    private void SponsorDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (_sponsorShown is not { } p) return;
        _settings.DismissedSponsors.Add(p.Id); _settings.Save();
        RefreshSponsor();
        Status("Hidden. You can turn sponsored picks off in Settings and help.");
    }

    private async void SponsorToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.SponsoredPicks = SponsorToggle.IsChecked; _settings.Save();
        if (_settings.SponsoredPicks) await InitSponsorsAsync(); else RefreshSponsor();
        Status(_settings.SponsoredPicks ? "Sponsored picks on." : "Sponsored picks off.");
    }
}
