using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace OpenKamera.Core;

/// <summary>A recording or snapshot on disk.</summary>
public sealed record MediaItem(string Path, bool IsVideo, DateTime Created, long Bytes);

/// <summary>Lists recordings and snapshots, and trims videos (keeps quality and size, re-encodes once).</summary>
public static class RecordingTools
{
    public static List<MediaItem> List(params string[] folders)
    {
        var items = new List<MediaItem>();
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".mp4" or ".png" or ".jpg")) continue;
                var info = new FileInfo(f);
                items.Add(new MediaItem(f, ext == ".mp4", info.CreationTime, info.Length));
            }
        return items.OrderByDescending(i => i.Created).ToList();
    }

    /// <summary>A free output name next to the input: "clip (trimmed).mp4", "clip (trimmed 2).mp4"…</summary>
    public static string TrimmedName(string input)
    {
        string dir = System.IO.Path.GetDirectoryName(input)!, name = System.IO.Path.GetFileNameWithoutExtension(input);
        for (int i = 1; ; i++)
        {
            string p = System.IO.Path.Combine(dir, i == 1 ? $"{name} (trimmed).mp4" : $"{name} (trimmed {i}).mp4");
            if (!File.Exists(p)) return p;
        }
    }

    /// <summary>Keeps <paramref name="start"/>..<paramref name="end"/> of a video in a new file. Returns the new path.</summary>
    public static async Task<string> TrimAsync(string input, TimeSpan start, TimeSpan end)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(input));
        var length = clip.OriginalDuration;
        start = TimeSpan.FromTicks(Math.Clamp(start.Ticks, 0, length.Ticks));
        end = TimeSpan.FromTicks(Math.Clamp(end.Ticks, start.Ticks, length.Ticks));
        if (end - start < TimeSpan.FromMilliseconds(200)) throw new ArgumentException("The part to keep is too short.");
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = length - end;
        var comp = new MediaComposition();
        comp.Clips.Add(clip);
        string output = TrimmedName(input);
        var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(output)!);
        var file = await folder.CreateFileAsync(System.IO.Path.GetFileName(output), CreationCollisionOption.FailIfExists);
        var props = clip.GetVideoEncodingProperties();
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video!.Width = props.Width; profile.Video.Height = props.Height;
        if (props.FrameRate.Denominator > 0) { profile.Video.FrameRate.Numerator = props.FrameRate.Numerator; profile.Video.FrameRate.Denominator = props.FrameRate.Denominator; }
        profile.Video.Bitrate = CompositeRecorder.BitrateFor((int)props.Width, (int)props.Height, 30);
        var result = await comp.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile);
        if (result != TranscodeFailureReason.None) { try { await file.DeleteAsync(); } catch { } throw new InvalidOperationException("Trim failed: " + result); }
        return output;
    }
}
