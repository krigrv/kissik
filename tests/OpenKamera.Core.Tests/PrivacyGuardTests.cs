namespace OpenKamera.Core.Tests;

public class PrivacyGuardTests
{
    [Fact]
    public void HidesWhenSomeoneJoinsAndShowsAgainLater()
    {
        var g = new PrivacyGuard(); var t = DateTime.UtcNow;
        Assert.False(g.Update(2, t));
        Assert.True(g.Update(2, t.AddSeconds(1)));
        Assert.True(g.Hiding);
        Assert.False(g.Update(1, t.AddSeconds(2)));
        Assert.True(g.Update(1, t.AddSeconds(5.1)));
        Assert.False(g.Hiding);
    }

    [Fact]
    public void AOneFrameGlitchDoesNothing()
    {
        var g = new PrivacyGuard(); var t = DateTime.UtcNow;
        g.Update(2, t); g.Update(1, t.AddSeconds(0.3)); g.Update(2, t.AddSeconds(0.9));
        Assert.False(g.Hiding);
    }
}
