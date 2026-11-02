using System.Net.Http.Headers;

namespace OpenKamera.Core.Tests;

public class SecurityTests
{
    [Theory]
    [InlineData("rtmp://live.example.com/app/key", true)]
    [InlineData("rtmps://live.example.com:443/app/key", true)]
    [InlineData("srt://203.0.113.5:9000", true)]
    [InlineData(@"C:\Users\me\Videos\test.flv", true)]
    [InlineData("rtmp://x/app\" -y C:\\evil.exe \"", false)]
    [InlineData("rtmp://x/app\nkey", false)]
    [InlineData("http://not-a-stream.example", false)]
    [InlineData("", false)]
    public void StreamDestinationsCantSmuggleOptions(string url, bool ok) => Assert.Equal(ok, LiveStream.IsSafeDestination(url));

    [Fact]
    public void BuildArgsRefusesUnsafeDestinations() =>
        Assert.Throws<ArgumentException>(() => LiveStream.BuildArgs(1280, 720, 30, null, ["rtmp://x/a\" -i evil"]));

    [Fact]
    public async Task ControlApiRejectsForeignHostNames()
    {
        var api = new ControlApi([new ControlTool("ping", "p", ControlApi.Schema(), _ => Task.FromResult("pong"))],
            Path.Combine(Path.GetTempPath(), "OpenKameraTests", $"tok-{Guid.NewGuid():N}.txt"));
        api.Start(0);
        await using var _a = api;
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, api.BaseUrl + "/tools");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.Token);
        req.Headers.Host = "attacker.example"; // what a DNS-rebinding page would send
        Assert.Equal(403, (int)(await http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public void PhonePageForbidsFramingAndReferrers()
    {
        Assert.Contains("frame-ancestors 'none'", PhoneServer.SecurityHeaders);
        Assert.Contains("Referrer-Policy: no-referrer", PhoneServer.SecurityHeaders);
        Assert.Contains("X-Content-Type-Options: nosniff", PhoneServer.SecurityHeaders);
    }
}
