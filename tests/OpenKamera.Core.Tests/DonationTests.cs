namespace OpenKamera.Core.Tests;

public class DonationTests
{
    private static string RepoFile(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine([d.FullName, .. parts]);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }

    [Fact]
    public void ShippedConfig_HasValidPayPalAndUpi()
    {
        var d = DonationInfo.Load(RepoFile("src", "OpenKamera.App", "donate.json"));
        Assert.True(d.HasPayPal);
        Assert.True(d.HasUpi);
        Assert.StartsWith("upi://pay?pa=kri.grv@okaxis&pn=Krishna%20Gaurav&cu=INR", d.UpiUri);
    }

    [Theory]
    [InlineData("kri.grv@okaxis", true)]
    [InlineData("name.surname@okhdfcbank", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("bad@psp1", false)]
    [InlineData("", false)]
    public void UpiId_Validation(string id, bool ok) => Assert.Equal(ok, DonationInfo.IsValidUpiId(id));

    [Fact]
    public void QrPng_IsAPng()
    {
        var png = new DonationInfo { UpiId = "kri.grv@okaxis", UpiName = "Krishna Gaurav" }.UpiQrPng();
        Assert.True(png.Length > 200);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    [Fact]
    public void MissingOrBrokenConfig_IsUnconfigured()
    {
        Assert.False(DonationInfo.Load(Path.Combine(Path.GetTempPath(), "nope-donate.json")).IsConfigured);
        string bad = Path.Combine(Path.GetTempPath(), "bad-donate.json");
        File.WriteAllText(bad, "{ not json");
        Assert.False(DonationInfo.Load(bad).IsConfigured);
        Assert.False(new DonationInfo { PayPalUrl = "http://paypal.me/x" }.HasPayPal); // https only
    }
}
