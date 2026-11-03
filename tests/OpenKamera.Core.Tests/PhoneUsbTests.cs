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
