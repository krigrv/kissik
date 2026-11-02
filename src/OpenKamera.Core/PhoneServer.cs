using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace OpenKamera.Core;

/// <summary>Status the phone page reports alongside its frames.</summary>
public sealed record PhoneStatus(string? Device, int Width, int Height, double? Battery, bool? Charging, string? Facing,
    bool? Torch = null, double? ZoomMax = null, double? Zoom = null, int? MaxWidth = null, int? MaxHeight = null, double? MaxFps = null)
{
    /// <summary>The longest side the phone camera can deliver (either orientation), or null if it didn't say.</summary>
    /// Prefers the lens's video limit (videoMaxW/H): the still-photo size can be far larger than what it can stream.
    public int? MaxLongSide => VideoMaxWidth is { } vw && VideoMaxHeight is { } vh ? Math.Max(vw, vh)
        : MaxWidth is { } w && MaxHeight is { } h ? Math.Max(w, h) : null;
    public int? VideoMaxWidth { get; init; }
    public int? VideoMaxHeight { get; init; }
    /// <summary>Phone-side delay: sensor exposure start → encoded (ms), and time waiting to send (ms).</summary>
    public double? CapEncMs { get; init; }
    public double? QueueMs { get; init; }

    // Pro controls from the native app. Null/empty = the lens (or the browser page) doesn't have it, so hide it.
    public double? ZoomMin { get; init; }
    /// <summary>Clockwise degrees H.264 frames must be turned (JPEG frames arrive upright).</summary>
    public int Rotation { get; init; }
    public IReadOnlyList<PhoneLens> Lenses { get; init; } = [];
    /// <summary>Chosen lens id, or null when following front/back.</summary>
    public string? Lens { get; init; }
    public int? Ev { get; init; }
    public int? EvMin { get; init; }
    public int? EvMax { get; init; }
    public double? EvStep { get; init; }
    public bool Manual { get; init; }
    public int? Iso { get; init; }
    public int? IsoMin { get; init; }
    public int? IsoMax { get; init; }
    /// <summary>Exposure time in nanoseconds; null = automatic.</summary>
    public long? Shutter { get; init; }
    public long? ShutterMin { get; init; }
    public long? ShutterMax { get; init; }
    public bool FocusManual { get; init; }
    /// <summary>0 = far … 1 = nearest; null = autofocus.</summary>
    public double? Focus { get; init; }
    public IReadOnlyList<string> WbModes { get; init; } = [];
    public string? Wb { get; init; }
    public IReadOnlyList<int> FpsList { get; init; } = [];
    /// <summary>0 = automatic.</summary>
    public int? Fps { get; init; }
    public bool? Stab { get; init; }
    public bool? Hdr { get; init; }
    public IReadOnlyList<string> Codecs { get; init; } = [];
    public string? Codec { get; init; }

    public bool HasEv => EvMin is { } lo && EvMax is { } hi && hi > lo && EvStep is > 0;
    public bool HasIso => Manual && IsoMin is { } lo && IsoMax is { } hi && hi > lo;
    public bool HasShutter => Manual && ShutterMin is { } lo && ShutterMax is { } hi && hi > lo;
    public bool CanH264 => Codecs.Contains("h264");
}

/// <summary>One of the phone's lenses, e.g. ("2", "back", "Ultra-wide").</summary>
public sealed record PhoneLens(string Id, string? Facing, string Label);

/// <summary>
/// Phone as camera, no app needed: a tiny HTTPS + WebSocket server on the local network. The phone opens the pairing
/// link (from the QR code), allows its camera, and streams JPEG frames back. HTTPS is required because phone browsers
/// only allow the camera on secure pages; the certificate is self-made, so the phone asks once to trust it.
/// A random token in the link means only the phone that scanned the code can connect.
/// </summary>
public sealed class PhoneServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private X509Certificate2? _cert;
    private CancellationTokenSource? _cts;
    private WebSocket? _active;

    public string Token { get; }
    private readonly string? _stateDir;

    /// <param name="stateDir">Folder to keep the pairing token and certificate in, so a paired phone reconnects after
    /// KISSIK restarts without scanning again or re-accepting the certificate. Null = new pairing every time.</param>
    public PhoneServer(string? stateDir = null)
    {
        _stateDir = stateDir;
        string? saved = null;
        if (stateDir is not null)
        {
            try { saved = File.ReadAllText(Path.Combine(stateDir, "phone-token.txt")).Trim(); } catch { }
        }
        Token = saved is { Length: 32 } && saved.All(Uri.IsHexDigit) ? saved : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // 128-bit
        if (stateDir is not null && saved != Token)
            try { Directory.CreateDirectory(stateDir); File.WriteAllText(Path.Combine(stateDir, "phone-token.txt"), Token); } catch { }
    }

    /// <summary>Forgets the paired phone: next start makes a new link and certificate.</summary>
    public static void Unpair(string stateDir)
    {
        foreach (var f in new[] { "phone-token.txt", "phone-cert.pfx" })
            try { File.Delete(Path.Combine(stateDir, f)); } catch { }
    }
    public int Port { get; private set; }
    public bool PhoneConnected => _active is { State: WebSocketState.Open };
    public PhoneStatus? LastStatus { get; private set; }
    public long FramesReceived { get; private set; }

    /// <summary>What reached the server, for "why won't my phone connect?" answers.</summary>
    public DateTime? LastVisit { get; private set; }
    public string? LastVisitFrom { get; private set; }
    public int WrongLinkVisits { get; private set; }
    public int PageLoads { get; private set; }
    public int TlsFailures { get; private set; }

    /// <summary>A JPEG frame from the phone (raised on a background thread).</summary>
    public event Action<byte[]>? FrameReceived;
    public event Action<bool>? ConnectionChanged;
    /// <summary>Phone microphone audio, already 48 kHz stereo.</summary>
    public event Action<float[]>? AudioReceived;
    public event Action<PhoneStatus>? StatusChanged;
    /// <summary>Raw H.264 packets: 'V' | flags (bit0 keyframe) | uint64 LE pts µs | Annex-B NAL units.</summary>
    public event Action<byte[]>? VideoPacketReceived;

    /// <summary>The fixed port phones connect to, so one firewall rule keeps working.</summary>
    public const int DefaultPort = 47820;

    public void Start(int port = DefaultPort)
    {
        _cert = LoadOrCreateCertificate();
        _listener = new TcpListener(IPAddress.Any, port);
        try { _listener.Start(); }
        catch (SocketException) when (port != 0) { _listener = new TcpListener(IPAddress.Any, 0); _listener.Start(); } // port busy
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_listener, tls: true, _cts.Token);
        // Plain HTTP on this PC only, for a phone plugged in by USB (adb reverse makes the phone's "localhost" reach it).
        // Phone browsers allow the camera on http://localhost, so there's no certificate warning over USB.
        try
        {
            _loopback = new TcpListener(IPAddress.Loopback, Port + 1);
            _loopback.Start();
            UsbPort = Port + 1;
            _ = AcceptLoop(_loopback, tls: false, _cts.Token);
        }
        catch (SocketException) { _loopback = null; UsbPort = 0; }
    }

    private TcpListener? _loopback;
    /// <summary>Port of the USB (localhost-only) listener; 0 if it couldn't start.</summary>
    public int UsbPort { get; private set; }
    /// <summary>The link a USB-connected phone opens (after adb reverse of <see cref="UsbPort"/>).</summary>
    public string UsbLink => $"http://localhost:{UsbPort}/?t={Token}";

    /// <summary>Pairing links for every local network address (the QR uses the first).</summary>
    public IReadOnlyList<string> Links => LocalAddresses().Select(a => $"https://{a}:{Port}/?t={Token}").ToList();

    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            // Prefer adapters with a gateway (real Wi-Fi / Ethernet) over virtual switches.
            bool hasGateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    list.Insert(hasGateway ? 0 : list.Count, ua.Address.ToString());
        }
        return list.Distinct().ToList();
    }

    private const int MaxConnections = 16;
    private int _open;
    private readonly Dictionary<string, (int Count, DateTime Since)> _wrongTries = [];

    /// <summary>True when this address has guessed wrong links too often in the last minute (locked out for a minute).</summary>
    private bool LockedOut(string? ip, bool wrongNow)
    {
        if (ip is null) return false;
        lock (_wrongTries)
        {
            var now = DateTime.UtcNow;
            var (count, since) = _wrongTries.TryGetValue(ip, out var e) && now - e.Since < TimeSpan.FromMinutes(1) ? e : (0, now);
            if (wrongNow) count++;
            _wrongTries[ip] = (count, since);
            return count > 10;
        }
    }

    private async Task AcceptLoop(TcpListener listener, bool tls, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            // Flood guard: a phone needs one or two connections; refuse beyond a small cap.
            if (Interlocked.Increment(ref _open) > MaxConnections) { Interlocked.Decrement(ref _open); client.Dispose(); continue; }
            _ = Task.Run(async () => { try { await Serve(client, tls, ct); } finally { Interlocked.Decrement(ref _open); } }, ct);
        }
    }

    private async Task Serve(TcpClient client, bool tls, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        LastVisit = DateTime.Now;
        LastVisitFrom = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
        try
        {
            Stream ssl = client.GetStream();
            if (tls)
            {
                var secure = new SslStream(ssl, false);
                try { await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _cert }, ct); }
                catch { TlsFailures++; throw; } // usually the browser refusing our certificate (user tapped Back)
                ssl = secure;
            }
            var (method, target, headers) = await ReadRequest(ssl, ct);
            var (path, query) = SplitTarget(target);
            bool authorised = query.TryGetValue("t", out var t) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(t), Encoding.ASCII.GetBytes(Token));

            string? ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
            if (LockedOut(ip, wrongNow: !authorised)) return; // too many wrong guesses from this device: drop silently
            if (method != "GET") { await Respond(ssl, 405, "text/plain", "Method not allowed"u8.ToArray(), ct); return; }
            if (!authorised) { WrongLinkVisits++; await Respond(ssl, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("This link has expired. Scan the QR code in KISSIK again."), ct); return; }
            if (path == "/") { PageLoads++; await Respond(ssl, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PhonePage.Html), ct); return; }
            if (path == "/ws" && headers.TryGetValue("sec-websocket-key", out var key))
            {
                await Upgrade(ssl, key, ct);
                await RunSocket(WebSocket.CreateFromStream(ssl, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(10)), ct);
                return;
            }
            await Respond(ssl, 404, "text/plain", "Not found"u8.ToArray(), ct);
        }
        catch { /* a dropped or untrusted connection: the phone retries */ }
    }

    private async Task RunSocket(WebSocket ws, CancellationToken ct)
    {
        // One phone at a time: a new connection replaces the old (e.g. after the phone reloads the page).
        var old = Interlocked.Exchange(ref _active, ws);
        try { old?.Abort(); } catch { }
        ConnectionChanged?.Invoke(true);
        var buffer = new byte[256 * 1024];
        var frame = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                frame.Write(buffer, 0, r.Count);
                if (frame.Length > 8 * 1024 * 1024) { frame.SetLength(0); continue; } // refuse absurd frames
                if (!r.EndOfMessage) continue;
                var data = frame.ToArray(); frame.SetLength(0);
                if (r.MessageType == WebSocketMessageType.Binary && PhoneAudio.IsAudio(data)) AudioReceived?.Invoke(PhoneAudio.ToStereo48k(data));
                else if (r.MessageType == WebSocketMessageType.Binary)
                {
                    // 'V' = H.264 packet from the native app (only sent after codec:h264); everything else is a JPEG frame.
                    if (data.Length > 10 && data[0] == (byte)'V') VideoPacketReceived?.Invoke(data);
                    else { FramesReceived++; FrameReceived?.Invoke(data); }
                }
                else if (TryParseStatus(data) is { } s) { LastStatus = s; StatusChanged?.Invoke(s); }
            }
        }
        catch { }
        finally
        {
            if (Interlocked.CompareExchange(ref _active, null, ws) == ws) ConnectionChanged?.Invoke(false);
        }
    }

    /// <summary>Asks the phone to switch lens or quality ("front"/"back", or "q:720" etc.).</summary>
    public async Task SendAsync(string command)
    {
        if (_active is { State: WebSocketState.Open } ws)
            await ws.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    internal static PhoneStatus? TryParseStatus(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            double? D(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            bool? B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? I(string n) => D(n) is { } d ? (int)Math.Round(d) : null;
            long? L(string n) => D(n) is { } d ? (long)Math.Round(d) : null;
            IEnumerable<JsonElement> A(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
            var lenses = A("lenses").Where(l => l.ValueKind == JsonValueKind.Object).Select(l =>
            {
                string? Field(string n) => l.TryGetProperty(n, out var v) ? v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                } : null;
                return Field("id") is { } id ? new PhoneLens(id, Field("facing"), Field("label") ?? id) : null;
            }).OfType<PhoneLens>().ToList();
            int rot = ((I("rot") ?? 0) % 360 + 360) % 360;
            return new PhoneStatus(S("device"), (int)(D("w") ?? 0), (int)(D("h") ?? 0), D("battery"), B("charging"), S("facing"),
                B("torch"), D("zoomMax"), D("zoom"), (int?)D("maxW"), (int?)D("maxH"), D("maxFps"))
            {
                ZoomMin = D("zoomMin"),
                VideoMaxWidth = I("videoMaxW"), VideoMaxHeight = I("videoMaxH"),
                CapEncMs = D("capEncMs"), QueueMs = D("queueMs"),
                Rotation = rot % 90 == 0 ? rot : 0,
                Lenses = lenses,
                Lens = r.TryGetProperty("lens", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetRawText() : S("lens"),
                Ev = I("ev"), EvMin = I("evMin"), EvMax = I("evMax"), EvStep = D("evStep"),
                Manual = B("manual") == true,
                Iso = I("iso"), IsoMin = I("isoMin"), IsoMax = I("isoMax"),
                Shutter = L("shutter"), ShutterMin = L("shutterMin"), ShutterMax = L("shutterMax"),
                FocusManual = B("focusManual") == true,
                Focus = D("focus") is { } f ? Math.Clamp(f, 0, 1) : null,
                WbModes = A("wbModes").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList(),
                Wb = S("wb"),
                FpsList = A("fpsList").Where(e => e.ValueKind == JsonValueKind.Number).Select(e => (int)Math.Round(e.GetDouble())).Where(f => f > 0).Distinct().Order().ToList(),
                Fps = I("fps"),
                Stab = B("stab"), Hdr = B("hdr"),
                Codecs = A("codecs").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.ToLowerInvariant()).ToList(),
                Codec = S("codec")?.ToLowerInvariant(),
            };
        }
        catch { return null; }
    }

    /// <summary>Plain-language reasons the phone isn't streaming, most likely first.</summary>
    public IReadOnlyList<string> Diagnose(bool firewallRuleExists, string? networkCategory)
    {
        var tips = new List<string>();
        if (PhoneConnected) { tips.Add("Your phone is connected and streaming."); return tips; }
        if (LastVisit is null)
        {
            tips.Add("Your phone hasn't reached this PC at all.");
            if (!firewallRuleExists) tips.Add("Windows Firewall is blocking it. Press “Allow phones on this network”.");
            tips.Add("Check the phone is on the same Wi-Fi as this PC (not mobile data, not a guest network).");
            tips.Add("Some routers keep Wi-Fi devices apart (“client isolation” or “AP isolation”). Turn it off in the router, or connect the PC to your phone's hotspot instead.");
            if (networkCategory == "Public") tips.Add("This PC's network is set to Public. That's fine once the firewall rule is added, but Private works better at home.");
            if (LocalAddresses().Count > 1) tips.Add("This PC has several networks. If the QR link doesn't open, try the other links shown under the code.");
            return tips;
        }
        if (WrongLinkVisits > 0 && PageLoads == 0) tips.Add("Your phone opened an old link. Scan the QR code again.");
        if (TlsFailures > 0 && PageLoads == 0) tips.Add("The phone reached the PC but didn't accept the security warning. Open the link again and tap Advanced → Continue.");
        if (PageLoads > 0) tips.Add("The page opened on your phone. Tap Start camera and allow the camera. If it says “Camera blocked”, allow camera access for this site in the phone browser's settings.");
        if (tips.Count == 0) tips.Add($"Your phone reached this PC at {LastVisit:HH:mm:ss}. Reload the page on the phone.");
        return tips;
    }

    internal static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        int q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = new Dictionary<string, string>();
        if (q >= 0)
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int e = pair.IndexOf('=');
                if (e > 0) query[Uri.UnescapeDataString(pair[..e])] = Uri.UnescapeDataString(pair[(e + 1)..]);
            }
        return (path, query);
    }

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers)> ReadRequest(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder(); var one = new byte[1];
        while (sb.Length < 16 * 1024)
        {
            if (await s.ReadAsync(one, ct) == 0) break;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
        }
        var lines = sb.ToString().Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>();
        foreach (var l in lines.Skip(1))
        {
            int c = l.IndexOf(':');
            if (c > 0) headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim();
        }
        return (first.ElementAtOrDefault(0) ?? "", first.ElementAtOrDefault(1) ?? "/", headers);
    }

    /// <summary>The page is self-contained: nothing loads from elsewhere, nothing can frame it, the link never leaks as a referrer.</summary>
    internal const string SecurityHeaders =
        "Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; media-src 'self' blob: mediastream:; connect-src 'self' wss: ws:; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n" +
        "Referrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\n" +
        "Permissions-Policy: geolocation=(), payment=(), usb=()\r\n";

    private static async Task Respond(Stream s, int code, string type, byte[] body, CancellationToken ct)
    {
        string reason = code switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\n" +
                   SecurityHeaders + "Connection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }

    private static async Task Upgrade(Stream s, string key, CancellationToken ct)
    {
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var head = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await s.FlushAsync(ct);
    }

    private X509Certificate2 LoadOrCreateCertificate()
    {
        var addresses = LocalAddresses();
        string? path = _stateDir is null ? null : Path.Combine(_stateDir, "phone-cert.pfx");
        if (path is not null && File.Exists(path))
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                try { raw = System.Security.Cryptography.ProtectedData.Unprotect(raw, null, System.Security.Cryptography.DataProtectionScope.CurrentUser); }
                catch (CryptographicException) { /* written by an older version, unencrypted */ }
                var c = X509CertificateLoader.LoadPkcs12(raw, null, X509KeyStorageFlags.Exportable);
                // Reuse it while it's valid and still covers this PC's current addresses.
                var san = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                var ips = san?.EnumerateIPAddresses().Select(i => i.ToString()).ToHashSet() ?? [];
                if (c.NotAfter > DateTime.Now.AddDays(7) && addresses.All(ips.Contains)) return c;
                c.Dispose();
            }
            catch { }
        }
        var cert = CreateCertificate(addresses);
        // The private key never sits on disk in the clear: encrypted for this Windows user only (DPAPI).
        if (path is not null) try { File.WriteAllBytes(path, System.Security.Cryptography.ProtectedData.Protect(cert.Export(X509ContentType.Pfx), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); } catch { }
        return cert;
    }

    internal static X509Certificate2 CreateCertificate(IEnumerable<string> addresses)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=KISSIK on this PC", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        foreach (var a in addresses) if (IPAddress.TryParse(a, out var ip)) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Round-trip through PFX so SslStream on Windows can use the private key.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _loopback?.Stop(); } catch { }
        if (_active is { } ws) { try { ws.Abort(); } catch { } }
        _cert?.Dispose();
        await Task.CompletedTask;
    }
}
