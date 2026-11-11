namespace OpenKamera.Core.Tests;

public class CameraCatalogNameTests
{
    [Theory]
    [InlineData("OPPO Find X9 Ultra (Windows Virtual Camera)", "OPPO Find X9 Ultra · via Windows", true)]
    [InlineData("Pixel 8 (Windows Virtual Camera)", "Pixel 8 · via Windows", true)]
    [InlineData("Logitech C920", "Logitech C920", false)]
    public void WindowsLinkedCamerasAreLabelled(string raw, string shown, bool viaWindows)
    {
        Assert.Equal(shown, CameraCatalog.FriendlyName(raw));
        Assert.Equal(viaWindows, CameraCatalog.IsWindowsVirtual(CameraCatalog.FriendlyName(raw)));
    }

    [Theory]
    [InlineData("KISSIK", true)]
    [InlineData("KISSIK (Windows Virtual Camera)", true)]
    [InlineData("4K Veoαcam", false)]
    public void OwnCameraIsNeverAnInput(string name, bool own) => Assert.Equal(own, CameraCatalog.IsOwnCamera(name));
}
