using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKamera.Core;

namespace OpenKamera_App;

// Smaller settings: start with Windows, your own output profiles, phone screen dimming, forgetting the phone,
// and Ctrl+Alt+K to bring KISSIK to the front from any app.
public sealed partial class MainPage
{
    private IEnumerable<OutputProfile> AllProfiles => OutputProfile.All.Concat(_settings.CustomProfiles);

    private async void InitExtras()
    {
        PhoneDimToggle.IsChecked = _settings.PhoneDim;
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync("KissikStartup");
            StartupToggle.IsChecked = task.State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        catch { StartupToggle.IsEnabled = false; }
    }

    private async void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync("KissikStartup");
            if (StartupToggle.IsChecked)
            {
                var state = await task.RequestEnableAsync();
                StartupToggle.IsChecked = state is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
                Status(StartupToggle.IsChecked ? "KISSIK will start with Windows."
                    : "Windows didn't allow it. Turn KISSIK on in Settings > Apps > Startup.");
            }
            else { task.Disable(); Status("KISSIK won't start with Windows."); }
        }
        catch (Exception ex) { Status("Couldn't change start with Windows: " + ex.Message); }
    }

    private async void PhoneDimToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.PhoneDim = PhoneDimToggle.IsChecked; _settings.Save();
        await SendPhoneDimAsync();
        Status(_settings.PhoneDim ? "The phone's screen dims while it streams. Tap it to light it." : "The phone's screen stays on while it streams.");
    }

    /// <summary>Tells a connected phone whether to dim; also sent each time a phone connects.</summary>
    private async Task SendPhoneDimAsync()
    {
        try { if (_phone is { } p) await p.SendAsync(_settings.PhoneDim ? "dim:on" : "dim:off"); } catch { }
    }

    private async void ForgetPhone_Click(object sender, RoutedEventArgs e)
    {
        var res = await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Forget the paired phone?",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "The phone disconnects and can't reconnect by itself. To use a phone again, pick Phone (Wi-Fi) and scan the new code." },
            PrimaryButtonText = "Forget", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
        if (res != ContentDialogResult.Primary) return;
        if (DeviceBox.SelectedItem == PhoneDevice && _session is not null) await ConnectAsync(toggle: true);
        await StopPhoneAsync();
        PhoneServer.Unpair(PhoneStateDir);
        Status("Phone forgotten. Pick Phone (Wi-Fi) to pair a phone again.");
    }

    private void BringToFront()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        ShowWindow(hwnd, 9 /* SW_RESTORE */);
        App.MainWindow.Activate();
        SetForegroundWindow(hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);

    /// <summary>"New profile..." and, for profiles you made, "Remove ...", at the end of the profile menu.</summary>
    private void AddProfileTools()
    {
        ProfileMenu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "New profile…", Icon = new FontIcon { Glyph = "" } };
        add.Click += async (_, _) => await NewProfileAsync();
        ProfileMenu.Items.Add(add);
        foreach (var p in _settings.CustomProfiles.ToList())
        {
            var rm = new MenuFlyoutItem { Text = "Remove " + p.Name, Icon = new FontIcon { Glyph = "" } };
            rm.Click += async (_, _) =>
            {
                _settings.CustomProfiles.Remove(p); _settings.Save();
                if (_settings.ProfileName == p.Name) await ChangeProfileAsync(OutputProfile.P720);
                BuildProfileMenu();
                Status($"Profile {p.Name} removed.");
            };
            ProfileMenu.Items.Add(rm);
        }
    }

    private async Task NewProfileAsync()
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "My square video" };
        NumberBox Num(string header, double value) => new()
        {
            Header = header, Value = value, Minimum = 160, Maximum = 3840, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var w = Num("Width (pixels)", 1920);
        var h = Num("Height (pixels)", 1080);
        var fps = new ComboBox { Header = "Frame rate", ItemsSource = new[] { 15, 24, 25, 30, 50, 60 }, SelectedItem = 30 };
        var body = new StackPanel { Spacing = 10, MinWidth = 320 };
        body.Children.Add(name); body.Children.Add(w); body.Children.Add(h); body.Children.Add(fps);
        body.Children.Add(new TextBlock { Text = "Sizes are rounded to even numbers. A camera that can't reach this size is scaled up to it.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 });
        var res = await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "New output profile", Content = body,
            PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();
        if (res != ContentDialogResult.Primary) return;
        // What's typed counts even if the box hasn't committed it yet (a number box only commits when it loses focus).
        static double Read(NumberBox b, double fallback) =>
            double.TryParse(b.Text, out var typed) ? typed : double.IsNaN(b.Value) ? fallback : b.Value;
        int width = (int)Math.Clamp(Read(w, 1920), 160, 3840) / 2 * 2;
        int height = (int)Math.Clamp(Read(h, 1080), 160, 3840) / 2 * 2;
        string n = string.IsNullOrWhiteSpace(name.Text) ? $"Custom {width}x{height}" : name.Text.Trim();
        if (AllProfiles.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) n += " (2)";
        var profile = new OutputProfile(n, width, height, fps.SelectedItem is int f ? f : 30);
        _settings.CustomProfiles.Add(profile); _settings.Save();
        BuildProfileMenu();
        try { await ChangeProfileAsync(profile); } catch (Exception ex) { Status("Couldn't switch profile: " + ex.Message); return; }
        Status($"Profile {n} added: {profile.Details}.");
    }
}
