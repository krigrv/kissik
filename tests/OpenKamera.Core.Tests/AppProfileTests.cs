namespace OpenKamera.Core.Tests;

public class AppProfileTests
{
    [Fact]
    public void FiresOnceWhenALinkedAppStartsUsingTheCamera()
    {
        var w = new AppProfileWatcher();
        w.Links["Zoom"] = "Client calls";
        Assert.Null(w.Update([]));
        Assert.Equal(("Zoom", "Client calls"), w.Update(["Zoom"]));
        Assert.Null(w.Update(["Zoom"]));          // still open: no repeat
        Assert.Null(w.Update([]));
        Assert.NotNull(w.Update(["zoom"]));       // reopened (case-insensitive)
    }

    [Fact]
    public void UnlinkedAppsAreIgnored()
    {
        var w = new AppProfileWatcher();
        w.Links["Microsoft Teams"] = "Work";
        Assert.Null(w.Update(["Google Chrome"]));
    }
}
