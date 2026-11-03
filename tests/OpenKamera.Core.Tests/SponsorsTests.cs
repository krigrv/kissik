namespace OpenKamera.Core.Tests;

public class SponsorsTests
{
    private static readonly DateTime Now = new(2026, 11, 2);
    private static readonly SponsorPick Cam = new("cam", "Brand A", "A sharper 4K webcam", "b", "https://a.example/cam", "camera-upgrade");
    private static readonly SponsorPick Light = new("light", "Brand B", "A soft key light", "b", "https://b.example/light", "low-light");
    private static readonly SponsorPick Any = new("any", "Brand C", "Desk mic", "b", "https://c.example/mic", "any");

    [Fact]
    public void SituationPickBeatsGeneric()
    {
        var p = Sponsors.Pick([Any, Cam], new SponsorContext(720, false, true), new HashSet<string>(), Now);
        Assert.Equal("cam", p?.Id);
    }

    [Fact]
    public void ShowsNothingWhenNothingFits()
    {
        Assert.Null(Sponsors.Pick([Cam, Light], new SponsorContext(2160, false, true), new HashSet<string>(), Now));
        Assert.Null(Sponsors.Pick([], new SponsorContext(720, true, false), new HashSet<string>(), Now));
    }

    [Fact]
    public void RespectsDismissedAndExpired()
    {
        var old = Light with { Id = "old", Until = Now.AddDays(-1) };
        Assert.Null(Sponsors.Pick([Cam, old], new SponsorContext(720, true, true), new HashSet<string> { "cam" }, Now));
    }

    [Fact]
    public void ParseDropsBrokenAndNonHttpsEntries()
    {
        var json = """
            [{"id":"ok","brand":"B","title":"T","body":"x","url":"https://b.example","when":"any"},
             {"id":"bad","brand":"B","title":"T","body":"x","url":"http://insecure.example","when":"any"},
             {"id":"","brand":"B","title":"T","body":"x","url":"https://b.example","when":"any"}]
            """;
        Assert.Equal(["ok"], Sponsors.Parse(json).Select(p => p.Id));
        Assert.Empty(Sponsors.Parse("not json"));
    }
}
