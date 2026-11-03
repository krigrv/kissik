using OpenKamera.Core;

namespace OpenKamera.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void MovesTheOldFolderOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "kissik-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OpenKamera", "Scenes"));
            File.WriteAllText(Path.Combine(root, "OpenKamera", "settings.json"), "{}");
            Assert.True(AppPaths.MigrateIn(root));
            Assert.True(File.Exists(Path.Combine(root, "KISSIK", "settings.json")));
            Assert.True(Directory.Exists(Path.Combine(root, "KISSIK", "Scenes")));
            Assert.False(Directory.Exists(Path.Combine(root, "OpenKamera")));
            Assert.False(AppPaths.MigrateIn(root)); // nothing left to move
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LeavesBothAloneWhenTheNewFolderExists()
    {
        string root = Path.Combine(Path.GetTempPath(), "kissik-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OpenKamera"));
            File.WriteAllText(Path.Combine(root, "OpenKamera", "a.txt"), "old");
            Directory.CreateDirectory(Path.Combine(root, "KISSIK"));
            Assert.False(AppPaths.MigrateIn(root));
            Assert.True(File.Exists(Path.Combine(root, "OpenKamera", "a.txt")));
        }
        finally { Directory.Delete(root, true); }
    }
}
