using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenKamera_App;

// Update check: asks GitHub for the newest release once at launch. When it is newer, the menu shows
// "Update available" and a status line says so; clicking opens the download page. Nothing installs by itself.
public sealed partial class MainPage
{
    private const string ReleasesApi = "https://api.github.com/repos/krigrv/kissik/releases/latest";
    private const string ReleasesPage = "https://github.com/krigrv/kissik/releases/latest";
    private string? _updateUrl;

    // Store installs update through the Store; GitHub releases only carry the Android app, so skip the check there.
    private static bool IsStoreInstall()
    {
        try
        {
            var pkg = Windows.ApplicationModel.Package.Current;
            return pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store
                || pkg.Id.Name.Equals("OGLABSPVTLIMITED.KISSIK", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsGitHubPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static Version CurrentVersion()
    {
        try { var v = Windows.ApplicationModel.Package.Current.Id.Version; return new Version(v.Major, v.Minor, v.Build); }
        catch { return new Version(1, 0, 0); }
    }

    private async Task CheckForUpdateAsync(bool quiet)
    {
        if (IsStoreInstall()) { if (!quiet) Status("The Microsoft Store keeps KISSIK up to date."); return; }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KISSIK", CurrentVersion().ToString()));
            var json = JsonNode.Parse(await http.GetStringAsync(ReleasesApi));
            string tag = (json?["tag_name"]?.ToString() ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest)) { if (!quiet) Status("Couldn't read the latest version."); return; }
            if (latest > CurrentVersion())
            {
                string? page = json?["html_url"]?.ToString();
                _updateUrl = IsGitHubPage(page) ? page : ReleasesPage;
                UpdateItem.Text = $"Update available: {latest}";
                ((FontIcon)UpdateItem.Icon).Glyph = "";
                Status($"KISSIK {latest} is available. Open the menu and choose Update available.");
            }
            else if (!quiet) Status($"You have the latest version ({CurrentVersion()}).");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { if (!quiet) Status($"You have the latest version ({CurrentVersion()})."); }
        catch (HttpRequestException) { if (!quiet) Status("Couldn't reach the update server. Check your internet connection and try again."); }
        catch (Exception) { if (!quiet) Status("Couldn't check for updates right now. Try again later."); }
    }

    private async void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        if (IsStoreInstall()) { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-windows-store://pdp/?ProductId=9MZC9QRSWK72")); return; }
        if (_updateUrl is { } url && IsGitHubPage(url)) await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        else { Status("Checking for updates…"); await CheckForUpdateAsync(quiet: false); }
    }
}
