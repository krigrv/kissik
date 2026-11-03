namespace OpenKamera.Core.Tests;

public class DeviceUsageTests
{
    [Theory]
    [InlineData(@"C:#Program Files#Google#Chrome#Application#chrome.exe", "Google Chrome")]
    [InlineData(@"C:#Users#me#AppData#Roaming#Zoom#bin#Zoom.exe", "Zoom")]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", "WhatsApp Desktop")]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Windows Camera")]
    [InlineData("6FC3C948-FF7F-4AE5-A9BF-759539B5E779_1z32rh13vfry6", "KISSIK")]
    public void FriendlyName_ReadsBothKeyStyles(string key, string expected) => Assert.Equal(expected, DeviceUsage.FriendlyName(key));

    [Fact]
    public void Interpret_StartedWithoutStopIsInUse()
    {
        Assert.True(DeviceUsage.Interpret(134358642184074490, 0).InUse);
        Assert.False(DeviceUsage.Interpret(134358642184074490, 134358642210506170).InUse);
        Assert.Null(DeviceUsage.Interpret(0, 0).LastUsed);
    }

    [Fact]
    public void Read_DoesNotThrowOnThisMachine() => Assert.NotNull(DeviceUsage.Read(PrivacyDevice.Camera));
}
