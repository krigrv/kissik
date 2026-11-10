namespace OpenKamera.Core;

/// <summary>
/// A small note file of unusual moments (the camera going quiet, a slow frame, a recording starting and stopping),
/// kept on this PC so a problem that only shows up now and then can be traced afterwards. Nothing is sent anywhere.
/// The file is capped: when it grows past the limit the older half is dropped. Never throws.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(AppPaths.LocalData, "activity.log");

    public static void Note(string what)
    {
        try
        {
            lock (Gate)
            {
                string path = Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (new FileInfo(path) is { Exists: true, Length: > MaxBytes })
                {
                    string all = File.ReadAllText(path);
                    int cut = all.IndexOf('\n', all.Length / 2);
                    File.WriteAllText(path, cut >= 0 ? all[(cut + 1)..] : "");
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {what}\n");
            }
        }
        catch { /* notes are best effort */ }
    }
}
