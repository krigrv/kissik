namespace OpenKamera.Core.Tests;

public class LookPresetTests
{
    [Fact]
    public void CaptureAndApply_RoundTripsTheLook()
    {
        var s = new AppSettings { Filter = FilterPreset.None, Mirror = true, LutName = "teal" };
        s.Enhance["EnhBrightness"] = 130;
        s.Background = new BackgroundSettings(BackgroundMode.Blur, GreenScreen: true);
        var p = LookPreset.Capture("Studio", s);
        s.Enhance["EnhBrightness"] = 90; s.Mirror = false; s.LutName = null; s.Background = BackgroundSettings.Off;
        p.ApplyTo(s);
        Assert.Equal(130, s.Enhance["EnhBrightness"]);
        Assert.True(s.Mirror); Assert.Equal("teal", s.LutName);
        Assert.True(s.Background.GreenScreen);
    }

    [Fact]
    public void Upsert_ReplacesSameNameAndSorts()
    {
        var list = new List<LookPreset>();
        var s = new AppSettings();
        LookPreset.Upsert(list, LookPreset.Capture("Zoom", s));
        LookPreset.Upsert(list, LookPreset.Capture("Away", s));
        LookPreset.Upsert(list, LookPreset.Capture("zoom", s));
        Assert.Equal(["Away", "zoom"], list.Select(p => p.Name));
    }

    [Fact]
    public void Presets_SurviveSaveAndLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ok-{Guid.NewGuid():N}.json");
        var s = new AppSettings();
        s.Presets.Add(LookPreset.Capture("Night", s));
        s.Save(path);
        Assert.Equal("Night", AppSettings.Load(path).Presets.Single().Name);
        File.Delete(path);
    }
}
