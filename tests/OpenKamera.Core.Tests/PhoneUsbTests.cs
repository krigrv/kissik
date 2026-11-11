namespace OpenKamera.Core.Tests;

public class PhoneUsbTests
{
    [Fact]
    public void ParsesAdbDevices()
    {
        var d = PhoneUsb.ParseDevices("List of devices attached\nR58N123\tdevice\nemulator-5554\tunauthorized\n\n");
        Assert.Equal(2, d.Count);
        Assert.Equal(("R58N123", "device"), d[0]);
        Assert.Equal("unauthorized", d[1].State);
    }

    [Fact]
    public void NoDevicesParsesEmpty() => Assert.Empty(PhoneUsb.ParseDevices("List of devices attached\n\n"));

    [Fact]
    public async Task UsbListener_ProvesItKnowsTheTokenWithoutSendingIt()
    {
        await using var s = new PhoneServer(); s.Start(0);
        using var http = new HttpClient();
        const string nonce = "00112233445566778899aabbccddeeff";
        var answer = await http.GetStringAsync($"http://localhost:{s.UsbPort}/proof?n={nonce}");
        Assert.Equal(PhoneServer.UsbProof(s.Token, nonce), answer);
        Assert.DoesNotContain(s.Token, answer);
        // Same vector as the Android app's usbProof: HMAC-SHA256(key "abc", "kissik-usb:00").
        Assert.Equal("d3736fef11c353687f38539202d475fcf0f13ca6876ebf0b5b7511bb69e9fe09", PhoneServer.UsbProof("abc", "00"));
        var bad = await http.GetAsync($"http://localhost:{s.UsbPort}/proof?n=not-hex");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task UsbListener_ServesThePageOverPlainHttpOnLocalhost()
    {
        await using var s = new PhoneServer(); s.Start(0);
        Assert.True(s.UsbPort > 0);
        using var http = new HttpClient();
        var page = await http.GetStringAsync(s.UsbLink);
        Assert.Contains("Start camera", page);
        Assert.StartsWith("http://localhost:", s.UsbLink);
    }
}
