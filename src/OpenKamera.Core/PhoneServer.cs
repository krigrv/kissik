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
public sealed record PhoneStatus(string? Device, int Width, int Height, double? Battery, bool? Charging, string? Facing);

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
        Token = saved is { Length: 16 } && saved.All(Uri.IsHexDigit) ? saved : Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
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
        _ = AcceptLoop(_cts.Token);
    }

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

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        LastVisit = DateTime.Now;
        LastVisitFrom = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
        try
        {
            var ssl = new SslStream(client.GetStream(), false);
            try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _cert }, ct); }
            catch { TlsFailures++; throw; } // usually the browser refusing our certificate (user tapped Back)
            var (method, target, headers) = await ReadRequest(ssl, ct);
            var (path, query) = SplitTarget(target);
            bool authorised = query.TryGetValue("t", out var t) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(t), Encoding.ASCII.GetBytes(Token));

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
                else if (r.MessageType == WebSocketMessageType.Binary) { FramesReceived++; FrameReceived?.Invoke(data); }
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
            return new PhoneStatus(S("device"), (int)(D("w") ?? 0), (int)(D("h") ?? 0), D("battery"), B("charging"), S("facing"));
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

    private static async Task Respond(Stream s, int code, string type, byte[] body, CancellationToken ct)
    {
        string reason = code switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
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
                var c = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
                // Reuse it while it's valid and still covers this PC's current addresses.
                var san = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                var ips = san?.EnumerateIPAddresses().Select(i => i.ToString()).ToHashSet() ?? [];
                if (c.NotAfter > DateTime.Now.AddDays(7) && addresses.All(ips.Contains)) return c;
                c.Dispose();
            }
            catch { }
        }
        var cert = CreateCertificate(addresses);
        if (path is not null) try { File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx)); } catch { }
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
        if (_active is { } ws) { try { ws.Abort(); } catch { } }
        _cert?.Dispose();
        await Task.CompletedTask;
    }
}
