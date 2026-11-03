using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace OpenKamera.Core;

public sealed record BackgroundInfo(string Id, string Name, string Category, string? FilePath = null);

/// <summary>
/// Replacement backgrounds: procedurally generated (our own, no third-party art) plus images the user imports.
/// Rendered at frame size and cached.
/// </summary>
public static class BackgroundLibrary
{
    public static string UserFolder => Path.Combine(
        AppPaths.Data, "Backgrounds");

    public static IReadOnlyList<BackgroundInfo> BuiltIn { get; } =
    [
        new("studio-grey", "Studio Grey", "Studio"),
        new("soft-daylight", "Soft Daylight", "Studio"),
        new("midnight-bokeh", "Midnight Bokeh", "Abstract"),
        new("ember-bokeh", "Ember Bokeh", "Abstract"),
        new("ocean-deep", "Ocean Deep", "Abstract"),
        new("sunset-haze", "Sunset Haze", "Abstract"),
        new("aurora", "Aurora", "Abstract"),
        new("paper-white", "Paper White", "Studio"),
    ];

    public static IReadOnlyList<BackgroundInfo> All()
    {
        var list = new List<BackgroundInfo>(BuiltIn);
        if (Directory.Exists(UserFolder))
            foreach (var f in Directory.EnumerateFiles(UserFolder).Where(f => ImageExtensions.Concat(VideoExtensions).Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
                list.Add(new("user:" + Path.GetFileName(f), Path.GetFileNameWithoutExtension(f), IsVideo(f) ? "Video" : "Imported", f));
        return list;
    }

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    /// <summary>Looping video backgrounds, decoded by Windows (H.264/HEVC .mp4 and .mov).</summary>
    public static readonly string[] VideoExtensions = [".mp4", ".mov"];
    public static bool IsVideo(string idOrPath) => VideoExtensions.Any(e => idOrPath.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    public static string? FilePathOf(string id) => id.StartsWith("user:", StringComparison.Ordinal) ? Path.Combine(UserFolder, id[5..]) : null;

    private static readonly ConcurrentDictionary<(string, int, int), byte[]> Cache = new();

    /// <summary>BGRA pixels for the background at exactly w×h (cover-fit for images), or null if unknown.</summary>
    public static byte[]? Render(string id, int w, int h)
    {
        if (IsVideo(id)) return null; // videos are played by VideoBackground, frame by frame
        if (Cache.TryGetValue((id, w, h), out var hit)) return hit;
        byte[]? px = id.StartsWith("user:", StringComparison.Ordinal)
            ? LoadImage(Path.Combine(UserFolder, id[5..]), w, h)
            : Generate(id, w, h);
        if (px is not null) { if (Cache.Count > 24) Cache.Clear(); Cache[(id, w, h)] = px; }
        return px;
    }

    /// <summary>Copies an image into the user library and returns its id.</summary>
    public static string Import(string sourcePath)
    {
        Directory.CreateDirectory(UserFolder);
        string name = Path.GetFileName(sourcePath);
        File.Copy(sourcePath, Path.Combine(UserFolder, name), overwrite: true);
        foreach (var k in Cache.Keys.Where(k => k.Item1 == "user:" + name)) Cache.TryRemove(k, out _);
        return "user:" + name;
    }

    internal static byte[]? Generate(string id, int w, int h)
    {
        var px = new byte[w * h * 4];
        // Stable seed (string.GetHashCode is randomised per process) so a background looks the same every launch.
        var rng = new Random(id.Aggregate(17, (h, c) => unchecked(h * 31 + c)) & 0x7fffffff);
        switch (id)
        {
            case "studio-grey": Radial(px, w, h, (88, 92, 98), (34, 36, 40)); break;
            case "soft-daylight": Radial(px, w, h, (246, 240, 228), (196, 204, 214)); break;
            case "paper-white": Radial(px, w, h, (250, 250, 248), (226, 226, 224)); break;
            case "midnight-bokeh": Linear(px, w, h, (14, 20, 46), (4, 6, 16)); Bokeh(px, w, h, rng, [(90, 140, 255), (255, 200, 120), (160, 110, 255)]); break;
            case "ember-bokeh": Linear(px, w, h, (40, 14, 8), (10, 4, 4)); Bokeh(px, w, h, rng, [(255, 150, 60), (255, 90, 40), (255, 210, 120)]); break;
            case "ocean-deep": Linear(px, w, h, (12, 90, 120), (4, 20, 40)); Waves(px, w, h, (60, 190, 210)); break;
            case "sunset-haze": Linear(px, w, h, (250, 170, 110), (90, 50, 110)); break;
            case "aurora": Linear(px, w, h, (8, 16, 30), (6, 30, 40)); Waves(px, w, h, (60, 240, 160)); Waves(px, w, h, (120, 90, 255), 1.7f); break;
            default: return null;
        }
        return px;
    }

    private static void Put(byte[] px, int i, float r, float g, float b)
    { px[i] = (byte)Math.Clamp(b, 0, 255); px[i + 1] = (byte)Math.Clamp(g, 0, 255); px[i + 2] = (byte)Math.Clamp(r, 0, 255); px[i + 3] = 255; }

    private static void Linear(byte[] px, int w, int h, (int R, int G, int B) top, (int R, int G, int B) bottom)
    {
        for (int y = 0; y < h; y++)
        {
            float t = (float)y / Math.Max(1, h - 1);
            for (int x = 0; x < w; x++)
                Put(px, (y * w + x) * 4, top.R + (bottom.R - top.R) * t, top.G + (bottom.G - top.G) * t, top.B + (bottom.B - top.B) * t);
        }
    }

    private static void Radial(byte[] px, int w, int h, (int R, int G, int B) centre, (int R, int G, int B) edge)
    {
        float cx = w * 0.5f, cy = h * 0.42f, maxD = MathF.Sqrt(cx * cx + cy * cy);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float t = Math.Min(1, MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxD);
                t = t * t;
                Put(px, (y * w + x) * 4, centre.R + (edge.R - centre.R) * t, centre.G + (edge.G - centre.G) * t, centre.B + (edge.B - centre.B) * t);
            }
        });
    }

    private static void Bokeh(byte[] px, int w, int h, Random rng, (int R, int G, int B)[] colours)
    {
        int count = 26;
        for (int n = 0; n < count; n++)
        {
            var c = colours[rng.Next(colours.Length)];
            float cx = (float)rng.NextDouble() * w, cy = (float)rng.NextDouble() * h;
            float rad = (0.03f + (float)rng.NextDouble() * 0.07f) * Math.Max(w, h);
            float strength = 0.25f + (float)rng.NextDouble() * 0.35f;
            int x0 = Math.Max(0, (int)(cx - rad)), x1 = Math.Min(w - 1, (int)(cx + rad));
            int y0 = Math.Max(0, (int)(cy - rad)), y1 = Math.Min(h - 1, (int)(cy + rad));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / rad;
                    if (d >= 1) continue;
                    float a = strength * (1 - d * d * d * d); // soft disc, brighter rim falloff
                    int i = (y * w + x) * 4;
                    Put(px, i, px[i + 2] + (c.R - px[i + 2]) * a, px[i + 1] + (c.G - px[i + 1]) * a, px[i] + (c.B - px[i]) * a);
                }
        }
    }

    private static void Waves(byte[] px, int w, int h, (int R, int G, int B) c, float freq = 1f)
    {
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / w, v = (float)y / h;
                float band = MathF.Sin(u * 6.28f * freq + MathF.Sin(v * 3.1f) * 2f) * 0.5f + 0.5f;
                float a = MathF.Pow(band, 6) * (1 - v) * 0.55f;
                int i = (y * w + x) * 4;
                Put(px, i, px[i + 2] + (c.R - px[i + 2]) * a, px[i + 1] + (c.G - px[i + 1]) * a, px[i] + (c.B - px[i]) * a);
            }
        });
    }

    private static byte[]? LoadImage(string path, int w, int h)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return LoadImageAsync(path, w, h).GetAwaiter().GetResult();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Decodes an image file to BGRA at its native size.</summary>
    public static async Task<(byte[] Pixels, int Width, int Height)> DecodeAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return (data.DetachPixelData(), (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
    }

    private static async Task<byte[]> LoadImageAsync(string path, int w, int h)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // Cover-fit: scale so the image fills w×h, centre-crop the overflow.
        double scale = Math.Max((double)w / decoder.PixelWidth, (double)h / decoder.PixelHeight);
        uint sw = (uint)Math.Ceiling(decoder.PixelWidth * scale), sh = (uint)Math.Ceiling(decoder.PixelHeight * scale);
        var transform = new BitmapTransform
        {
            ScaledWidth = sw, ScaledHeight = sh, InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (sw - (uint)w) / 2, Y = (sh - (uint)h) / 2, Width = (uint)w, Height = (uint)h },
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return data.DetachPixelData();
    }
}
