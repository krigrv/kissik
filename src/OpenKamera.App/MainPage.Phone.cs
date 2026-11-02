using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;

namespace OpenKamera_App;

// Phone as camera: pick "Phone" as the source, scan the QR code, tap Start on the phone. No app to install.
public sealed partial class MainPage
{
    private static readonly CameraDeviceInfo PhoneDevice = new("openkamera:phone", "Phone (Wi-Fi)");
    private PhoneServer? _phone;
    private static string PhoneStateDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenKamera", "Phone");
    private const string PhoneMicId = "openkamera:phone-mic";
    private AudioTrack? _phoneMicTrack;
    private int _phoneLimitShown;
    private ContentDialog? _phoneDialog;

    /// <summary>Phone picked as the source: make sure the server runs, and show the QR code unless the phone is already here.</summary>
    private async Task StartPhoneAsync()
    {
        var server = EnsurePhoneServer();
        if (!server.PhoneConnected) _ = ShowPhoneQrAsync(server);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Once a phone has been paired, keep listening for it whenever KISSIK runs: opening the phone page then
    /// switches the source to the phone by itself, with no rescan.
    /// </summary>
    private void ResumePairedPhone()
    {
        if (File.Exists(Path.Combine(PhoneStateDir, "phone-token.txt")))
            try { EnsurePhoneServer(); } catch { /* port busy: pairing works again from the Phone source */ }
    }

    private PhoneServer EnsurePhoneServer()
    {
        if (_phone is { } running) return running;
        var server = new PhoneServer(PhoneStateDir);
        server.Start();
        _phone = server;
        // Frames go to whatever session is current, but only a phone session accepts them.
        server.FrameReceived += jpeg => { if (_session is { } s) _ = s.PushJpegAsync(jpeg); };
        server.AudioReceived += samples =>
        {
            // The phone mic joins the mix as its own track (with meter, volume and mute) the first time it sends sound.
            if (!_audio.IsRunning) return;
            if (_phoneMicTrack is null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_phoneMicTrack is not null || !_audio.IsRunning) return;
                    _phoneMicTrack = _audio.AddPushTrack(PhoneMicId, "Phone microphone");
                    BuildMicTracks();
                    Status("Phone microphone added to your audio mix.");
                });
                return;
            }
            _audio.PushAudio(PhoneMicId, samples);
        };
        server.ConnectionChanged += connected => DispatcherQueue.TryEnqueue(() =>
        {
            if (!connected && _phoneMicTrack is not null) { _audio.RemovePushTrack(PhoneMicId); _phoneMicTrack = null; BuildMicTracks(); }
            if (connected) { _phoneDialog?.Hide(); _ = SwitchToPhoneAsync(); }
            else if (DeviceBox.SelectedItem == PhoneDevice) Status("Phone disconnected. It reconnects by itself when the phone page is open again.");
        });
        server.StatusChanged += st => DispatcherQueue.TryEnqueue(() =>
        {
            string battery = st.Battery is { } b ? $" · battery {b:P0}{(st.Charging == true ? " charging" : "")}" : "";
            HudText.Text = $"Phone {st.Facing} camera {st.Width}×{st.Height}{battery}";
            if (st.Width > 0) SourceInfo.Text = $"{st.Width}×{st.Height} · phone";
            if (st.Height > 0 && st.Height < _profile.Height && _phoneLimitShown != st.Height)
            {
                _phoneLimitShown = st.Height;
                Status($"Your phone is sending {st.Height}p. Pick 1080p on the phone page for a sharper picture; if it stays at {st.Height}p, that's the most this phone's browser offers.");
            }
            if (st.Battery is < 0.15 && st.Charging != true) Status("Your phone's battery is low. Plug it in to keep streaming.");
        });
        return server;
    }

    /// <summary>The phone page connected: make the phone the source (it's what the user just reached for).</summary>
    private async Task SwitchToPhoneAsync()
    {
        if (DeviceBox.SelectedItem == PhoneDevice && _session is not null) { Status("Phone connected. Its camera is live in KISSIK."); return; }
        if (_recorder is not null) { Status("Phone is ready. Stop recording, then pick Phone (Wi-Fi) to switch to it."); return; }
        if (_session is not null) await ConnectAsync(toggle: true); // disconnect the current camera
        DeviceBox.SelectedItem = PhoneDevice;
        await ConnectAsync();
        Status("Phone connected. Its camera is live in KISSIK.");
    }

    private async Task StopPhoneAsync()
    {
        _phoneDialog?.Hide();
        if (_phone is { } p) { _phone = null; await p.DisposeAsync(); }
    }

    private async Task ShowPhoneQrAsync(PhoneServer server)
    {
        var links = server.Links;
        if (links.Count == 0) { Status("No network found. Connect this PC and your phone to the same Wi-Fi."); return; }
        string link = links[0];

        var qr = new BitmapImage();
        using (var gen = new QRCoder.QRCodeGenerator())
        using (var data = gen.CreateQrCode(link, QRCoder.QRCodeGenerator.ECCLevel.M))
        using (var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream())
        {
            await ms.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(new QRCoder.PngByteQRCode(data).GetGraphic(8)));
            ms.Seek(0);
            await qr.SetSourceAsync(ms);
        }
        var image = new Image { Source = qr, Width = 240, Height = 240 };
        AutomationProperties.SetName(image, "QR code to connect your phone");

        var steps = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        steps.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "1. Put your phone on the same Wi-Fi as this PC.\n2. Scan this code with the phone's camera.\n3. The phone warns that the connection isn't private: tap Advanced → Continue (it's your own PC).\n4. Tap Start camera and allow the camera." });
        var linkBox = new TextBox { Text = link, IsReadOnly = true, FontSize = 12 };
        AutomationProperties.SetName(linkBox, "Link to open on your phone");

        var body = new StackPanel { Spacing = 14, MaxWidth = 380 };
        body.Children.Add(new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), CornerRadius = new CornerRadius(14), Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, Child = image });
        body.Children.Add(steps);
        body.Children.Add(linkBox);
        if (!PhoneFirewall.RuleExists(server.Port))
        {
            // Without this, Windows Firewall silently blocks the phone (especially on networks marked Public).
            var note = new TextBlock { Text = "Windows Firewall will block your phone until you allow it once. This opens one port, for devices on your own network only.", FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
            var allow = new Button { Content = "Allow phones on this network", HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            AutomationProperties.SetName(allow, "Allow phones on this network (asks for admin permission)");
            allow.Click += async (_, _) =>
            {
                allow.IsEnabled = false;
                bool ok = await PhoneFirewall.AllowAsync(server.Port);
                note.Text = ok ? "Done. Your phone can connect now." : "Not allowed. Approve the Windows prompt to let your phone connect.";
                allow.Visibility = ok ? Visibility.Collapsed : Visibility.Visible; allow.IsEnabled = !ok;
            };
            body.Children.Add(note);
            body.Children.Add(allow);
        }

        if (server.Links.Count > 1)
        {
            var others = new TextBox { Text = string.Join(Environment.NewLine, server.Links.Skip(1)), IsReadOnly = true, FontSize = 12, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(others, "Other links, for this PC's other networks");
            body.Children.Add(new TextBlock { Text = "This PC is on more than one network. If the code doesn't open, try one of these on your phone:", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(others);
        }

        // Android over USB: no Wi-Fi, no certificate warning.
        if (server.UsbPort > 0)
        {
            var usbNote = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(usbNote, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            var usb = new Button { Content = "Use a USB cable instead (Android)", HorizontalAlignment = HorizontalAlignment.Stretch };
            usb.Click += async (_, _) =>
            {
                usb.IsEnabled = false;
                usbNote.Text = "Looking for your phone on USB…"; usbNote.Visibility = Visibility.Visible;
                var r = await PhoneUsb.ConnectAsync(server.UsbPort, server.UsbLink);
                usbNote.Text = r.Message;
                usb.IsEnabled = true;
            };
            body.Children.Add(usb);
            body.Children.Add(usbNote);
        }

        // "It's not connecting": plain-language reasons, based on what actually reached this PC.
        var tips = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(tips, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var help = new Button { Content = "It's not connecting", HorizontalAlignment = HorizontalAlignment.Stretch };
        help.Click += (_, _) =>
        {
            tips.Text = string.Join("\n\n", server.Diagnose(PhoneFirewall.RuleExists(server.Port), null).Select(t => "• " + t));
            tips.Visibility = Visibility.Visible;
        };
        body.Children.Add(help);
        body.Children.Add(tips);

        _phoneDialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Use your phone as a camera",
            Content = new ScrollViewer { Content = body, MaxHeight = 560 },
            CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close,
        };
        Status("Scan the QR code with your phone to connect it.");
        try { await _phoneDialog.ShowAsync(); } catch { /* another dialog is open */ }
        _phoneDialog = null;
    }

    private async void PhoneFlip_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is { } p) await p.SendAsync(p.LastStatus?.Facing == "front" ? "back" : "front");
    }
}
