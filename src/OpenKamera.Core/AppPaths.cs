namespace OpenKamera.Core;

/// <summary>
/// Where KISSIK keeps its files: settings and libraries in AppData\KISSIK, logs in LocalAppData\KISSIK, pictures and
/// videos in Pictures\KISSIK and Videos\KISSIK. Earlier versions used "OpenKamera"; <see cref="MigrateOldFolders"/>
/// moves those over once.
/// </summary>
public static class AppPaths
{
    public const string FolderName = "KISSIK";
    private const string OldFolderName = "OpenKamera";

    public static string Data => In(Environment.SpecialFolder.ApplicationData);
    public static string LocalData => In(Environment.SpecialFolder.LocalApplicationData);
    public static string Pictures => In(Environment.SpecialFolder.MyPictures);
    public static string Videos => In(Environment.SpecialFolder.MyVideos);

    private static string In(Environment.SpecialFolder root) => Path.Combine(Environment.GetFolderPath(root), FolderName);

    /// <summary>
    /// Moves each old OpenKamera folder to its KISSIK name when the new one doesn't exist yet. When both exist the old
    /// one is left alone (nothing is merged or overwritten). Never throws.
    /// </summary>
    public static void MigrateOldFolders()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos })
            try { MigrateIn(Environment.GetFolderPath(root)); } catch { /* a folder in use stays where it is */ }
    }

    /// <summary>A path inside an old OpenKamera folder, pointed at the same place in the KISSIK folder once that was moved.</summary>
    public static string Remap(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        foreach (var root in new[] { Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos })
        {
            string parent = Environment.GetFolderPath(root);
            if (string.IsNullOrEmpty(parent)) continue;
            string old = Path.Combine(parent, OldFolderName);
            if ((path.Equals(old, StringComparison.OrdinalIgnoreCase) || path.StartsWith(old + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                && !Directory.Exists(old))
                return Path.Combine(parent, FolderName) + path[old.Length..];
        }
        return path;
    }

    /// <summary>The move itself, for one parent folder (tests use a temporary one).</summary>
    public static bool MigrateIn(string parent)
    {
        if (string.IsNullOrEmpty(parent)) return false;
        string from = Path.Combine(parent, OldFolderName), to = Path.Combine(parent, FolderName);
        // Windows paths ignore case: "Kissik" and "KISSIK" are the same folder.
        if (!Directory.Exists(from) || Directory.Exists(to)) return false;
        Directory.Move(from, to);
        return true;
    }
}
