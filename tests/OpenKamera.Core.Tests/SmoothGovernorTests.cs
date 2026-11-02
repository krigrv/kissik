namespace OpenKamera.Core.Tests;

public class SmoothGovernorTests
{
    [Fact]
    public void StepsDownOnlyAfterSustainedOverload()
    {
        var g = new SmoothGovernor(); var t = DateTime.UtcNow;
        Assert.False(g.Update(40, 30, t));
        Assert.False(g.Update(40, 30, t.AddSeconds(1)));
        Assert.True(g.Update(40, 30, t.AddSeconds(2.1)));
        Assert.Equal(1, g.Level);
    }

    [Fact]
    public void ABriefSpikeDoesNothing()
    {
        var g = new SmoothGovernor(); var t = DateTime.UtcNow;
        g.Update(40, 30, t); g.Update(20, 30, t.AddSeconds(1)); g.Update(40, 30, t.AddSeconds(2.5));
        Assert.Equal(0, g.Level);
    }

    [Fact]
    public void StepsBackUpWithHeadroomAndNeverPastLimits()
    {
        var g = new SmoothGovernor(); var t = DateTime.UtcNow;
        for (int i = 0; i < 20; i++) g.Update(60, 30, t.AddSeconds(i * 2.1));
        Assert.Equal(SmoothGovernor.MaxLevel, g.Level);
        var u = t.AddMinutes(5);
        for (int i = 0; i < 40; i++) g.Update(3, 30, u.AddSeconds(i * 10.1));
        Assert.Equal(0, g.Level);
    }
}
