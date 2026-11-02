using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenKamera_App;

// Update check: asks GitHub for the newest release once at launch. When it is newer, the menu shows
// "Update available" and a status line says so; clicking opens the download page. Nothing installs by itself.
public sealed partial class MainPage
{
    private const string ReleasesApi = "https://api.github.com/repos/krigrv/OpenKamera/releases/latest";
    private const string ReleasesPage = "https://github.com/krigrv/OpenKamera/releases/latest";
    private string? _updateUrl;

    private static Version CurrentVersion()
    {
        try { var v = Windows.ApplicationModel.Package.Current.Id.Version; return new Version(v.Major, v.Minor, v.Build); }
        catch { return new Version(1, 0, 0); }
    }

    private async Task CheckForUpdateAsync(bool quiet)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KISSIK", CurrentVersion().ToString()));
            var json = JsonNode.Parse(await http.GetStringAsync(ReleasesApi));
            string tag = (json?["tag_name"]?.ToString() ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest)) { if (!quiet) Status("Couldn't read the latest version."); return; }
            if (latest > CurrentVersion())
            {
                _updateUrl = json?["html_url"]?.ToString() ?? ReleasesPage;
                UpdateItem.Text = $"Update available: {latest}";
                ((FontIcon)UpdateItem.Icon).Glyph = "";
                Status($"KISSIK {latest} is available. Open the menu and choose Update available.");
            }
            else if (!quiet) Status($"You have the latest version ({CurrentVersion()}).");
        }
        catch (Exception ex) { if (!quiet) Status("Couldn't check for updates: " + ex.Message); }
    }

    private async void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        if (_updateUrl is { } url) await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        else { Status("Checking for updates…"); await CheckForUpdateAsync(quiet: false); }
    }
}
