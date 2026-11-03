using System.Net.Security;
using System.Net.WebSockets;
using System.Text;

namespace OpenKamera.Core.Tests;

public class PhoneServerTests
{
    private static HttpClient Insecure() => new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });

    [Fact]
    public async Task ServesThePageOnlyWithTheToken()
    {
        await using var s = new PhoneServer(); s.Start(0);
        using var http = Insecure();
        var ok = await http.GetAsync($"https://localhost:{s.Port}/?t={s.Token}");
        Assert.Equal(200, (int)ok.StatusCode);
        Assert.Contains("Start camera", await ok.Content.ReadAsStringAsync());
        var bad = await http.GetAsync($"https://localhost:{s.Port}/?t=wrong");
        Assert.Equal(403, (int)bad.StatusCode);
    }

    [Fact]
    public async Task FramesAndStatusArriveOverTheSocket()
    {
        await using var s = new PhoneServer(); s.Start(0);
        var got = new TaskCompletionSource<byte[]>(); var status = new TaskCompletionSource<PhoneStatus>();
        s.FrameReceived += f => got.TrySetResult(f);
        s.StatusChanged += st => status.TrySetResult(st);
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await ws.ConnectAsync(new Uri($"wss://localhost:{s.Port}/ws?t={s.Token}"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"w\":1280,\"h\":720,\"battery\":0.5,\"facing\":\"back\"}"), WebSocketMessageType.Text, true, default);
        var jpeg = new byte[300_000]; jpeg[0] = 0xFF; jpeg[1] = 0xD8;
        await ws.SendAsync(jpeg, WebSocketMessageType.Binary, true, default);
        var frame = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(jpeg.Length, frame.Length);
        var st = await status.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1280, st.Width); Assert.Equal(0.5, st.Battery); Assert.True(s.PhoneConnected);
    }

    [Fact]
    public async Task SocketWithoutTokenIsRefused()
    {
        await using var s = new PhoneServer(); s.Start(0);
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri($"wss://localhost:{s.Port}/ws?t=nope"), CancellationToken.None));
    }

    [Fact]
    public void SplitTarget_ReadsQuery()
    {
        var (p, q) = PhoneServer.SplitTarget("/ws?t=abc%20d&x=1");
        Assert.Equal("/ws", p); Assert.Equal("abc d", q["t"]);
    }

    [Fact]
    public async Task Pairing_SurvivesARestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "pair-" + Guid.NewGuid().ToString("N")[..8]);
        string token; string thumb;
        await using (var a = new PhoneServer(dir)) { a.Start(0); token = a.Token; thumb = await Thumbprint(a.Port); }
        await using (var b = new PhoneServer(dir)) { b.Start(0); Assert.Equal(token, b.Token); Assert.Equal(thumb, await Thumbprint(b.Port)); }
        PhoneServer.Unpair(dir);
        await using var c = new PhoneServer(dir); Assert.NotEqual(token, c.Token);
    }

    private static async Task<string> Thumbprint(int port)
    {
        using var tcp = new System.Net.Sockets.TcpClient("localhost", port);
        string? t = null;
        using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => { t = cert!.GetCertHashString(); return true; });
        await ssl.AuthenticateAsClientAsync("localhost");
        return t!;
    }
}

public class PhoneDiagnoseTests
{
    [Fact]
    public async Task NeverReached_PointsAtFirewallAndWifi()
    {
        await using var s = new PhoneServer(); s.Start(0);
        var tips = s.Diagnose(firewallRuleExists: false, "Public");
        Assert.Contains(tips, t => t.Contains("hasn't reached"));
        Assert.Contains(tips, t => t.Contains("Firewall"));
        Assert.Contains(tips, t => t.Contains("isolation"));
    }

    [Fact]
    public async Task OldLink_SaysScanAgain()
    {
        await using var s = new PhoneServer(); s.Start(0);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        await http.GetAsync($"https://localhost:{s.Port}/?t=old");
        Assert.Contains(s.Diagnose(true, "Private"), t => t.Contains("old link"));
    }
}
