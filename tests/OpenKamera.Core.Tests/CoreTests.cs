using OpenKamera.Core;

namespace OpenKamera.Core.Tests;

public class CoreTests
{
    [Fact]
    public void DriveMath_SplitsAroundCentre()
    {
        Assert.Equal((0.0, 40.0), DriveMath.Split(40));
        Assert.Equal((30.0, 0.0), DriveMath.Split(-30));
        Assert.Equal((0.0, 0.0), DriveMath.Split(0));
        Assert.Equal((0.0, 100.0), DriveMath.Split(500));
    }

    [Fact]
    public void DriveTarget_ClampsToDeviceRange()
    {
        var p = new CameraProperty("Zoom", 0, 100, 1, 10, 98, false, false);
        Assert.Equal(100, DriveMath.DriveTarget(p, 100));
        var q = new CameraProperty("Zoom", 0, 100, 1, 10, 2, false, false);
        Assert.Equal(0, DriveMath.DriveTarget(q, -100));
    }

    [Fact]
    public void Property_ClampsAndRoundsToStep()
    {
        var p = new CameraProperty("Brightness", 0, 255, 5, 128, 128, false, false);
        Assert.Equal(130, p.Clamp(131));
        Assert.Equal(255, p.Clamp(999));
        Assert.Equal(0, p.Clamp(-4));
    }

    [Fact]
    public void Scene_RoundTripsJson()
    {
        var scene = SceneDocument.Templates.First(s => s.Id == "lower-third");
        var copy = SceneDocument.FromJson(scene.ToJson());
        Assert.Equal("Lower Third", copy.Name);
        Assert.Equal(scene.Layers.Count, copy.Layers.Count);
        Assert.Equal("Your Name", copy.Layers.Single(l => l.Name == "Name").Text);
    }

    [Fact]
    public void Settings_RoundTripsAndKeepsDeviceValues()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var s = new AppSettings();
        s.ForDevice("cam1").Values["Brightness"] = 140;
        s.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Equal(140, loaded.ForDevice("cam1").Values["Brightness"]);
        File.Delete(path);
    }

    [Fact]
    public void ImageFilters_BrightnessScalesPixels()
    {
        byte[] px = [100, 100, 100, 255];
        ImageFilters.Apply(px, new ImageFilters.Adjustments(Brightness: 0.5));
        Assert.Equal(50, px[0]);
        Assert.Equal(255, px[3]); // alpha untouched
    }

    [Fact]
    public void ImageFilters_NeutralIsNoOp()
    {
        byte[] px = [10, 20, 30, 255];
        ImageFilters.Apply(px, ImageFilters.Adjustments.Neutral);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, px);
    }

    [Fact]
    public void PackageManifest_DeclaresWebcamAndMicrophoneCapabilities()
    {
        // Regression: without these, MediaCapture in the packaged app throws
        // "Access is denied" and the preview stays blank.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? manifest = null;
        for (var d = dir; d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "OpenKamera.App", "Package.appxmanifest");
            if (File.Exists(candidate)) { manifest = candidate; break; }
        }
        Assert.True(manifest is not null, "Package.appxmanifest not found above test output");
        string xml = File.ReadAllText(manifest!);
        Assert.Contains("DeviceCapability Name=\"webcam\"", xml);
        Assert.Contains("DeviceCapability Name=\"microphone\"", xml);
        // Regression: the appx schema requires every DeviceCapability after all
        // other Capability elements, or package registration fails (0xC00CE014).
        int firstDevice = xml.IndexOf("<DeviceCapability", StringComparison.Ordinal);
        int lastOther = Math.Max(xml.LastIndexOf(":Capability ", StringComparison.Ordinal),
                                 xml.LastIndexOf("<Capability ", StringComparison.Ordinal));
        Assert.True(lastOther < firstDevice, "DeviceCapability must come after all Capability elements");
    }

    [Fact]
    public void Profiles_DefaultIs720p30()
    {
        Assert.Equal(1280, OutputProfile.P720.Width);
        Assert.Equal(30, OutputProfile.P720.Fps);
        Assert.Equal(3840, OutputProfile.P2160.Width);
    }
}
