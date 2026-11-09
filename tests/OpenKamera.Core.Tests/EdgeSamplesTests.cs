using OpenKamera.Core;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>
/// Visual check of the cut-out edge on real pictures. Runs only when KISSIK_EDGE_SAMPLES names a folder of pictures
/// (for example the stock people in website/assets/person) and KISSIK_EDGE_OUT a folder to write into. For each
/// picture and quality it writes the person over a flat blue background (any old room left on the edge stands out
/// against it) and the cut-out itself, from the GPU path and the CPU path.
/// </summary>
[Collection("Timing")]
public class EdgeSamplesTests(ITestOutputHelper log)
{
    [Fact]
    public async Task WritesReplacedPicturesForEyeballing()
    {
        string? dir = Environment.GetEnvironmentVariable("KISSIK_EDGE_SAMPLES"), outDir = Environment.GetEnvironmentVariable("KISSIK_EDGE_OUT");
        if (dir is null || outDir is null || !Directory.Exists(dir)) { log.WriteLine("skipped: KISSIK_EDGE_SAMPLES / KISSIK_EDGE_OUT not set"); return; }
        string only = Environment.GetEnvironmentVariable("KISSIK_EDGE_ONLY") ?? "";
        Directory.CreateDirectory(outDir);
        foreach (string path in Directory.GetFiles(dir, "*.jpg"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (only.Length > 0 && !only.Split(',').Contains(name)) continue;
            var (px, w, h) = await BackgroundLibrary.DecodeAsync(path);
            var flat = new byte[px.Length];
            for (int i = 0; i < flat.Length; i += 4) { flat[i] = 200; flat[i + 1] = 80; flat[i + 2] = 20; flat[i + 3] = 255; }
            foreach (var q in new[] { SegmentationQuality.Fast, SegmentationQuality.High })
            {
                using var seg = new PersonSegmenter(q);
                var mask = seg.Segment(px, w, h);
                var replace = FrameRecipe.Passthrough with { Background = new BackgroundSettings(BackgroundMode.Replace, BackgroundId: "x", Quality: q) };
                var remove = FrameRecipe.Passthrough with { Background = new BackgroundSettings(BackgroundMode.Remove, Quality: q) };

                if (GpuEffects.Shared is { } gpu)
                {
                    if (gpu.Process((byte[])px.Clone(), w, h, replace, mask, flat) is { } g) await Save(Path.Combine(outDir, $"{name}-{q}-gpu.png"), g, w, h);
                    if (gpu.Process((byte[])px.Clone(), w, h, remove, mask, null) is { } a) await Save(Path.Combine(outDir, $"{name}-{q}-alpha.png"), AlphaAsGrey(a), w, h);
                }
                var cpu = (byte[])px.Clone();
                BackgroundEffects.Apply(cpu, w, h, MaskOps.Matte(mask, cpu, w, h), replace.Background, flat);
                await Save(Path.Combine(outDir, $"{name}-{q}-cpu.png"), cpu, w, h);
                log.WriteLine($"{name} {q}: written");
            }
        }
    }

    private static byte[] AlphaAsGrey(byte[] premultiplied)
    {
        var grey = new byte[premultiplied.Length];
        for (int i = 0; i < grey.Length; i += 4) { grey[i] = grey[i + 1] = grey[i + 2] = premultiplied[i + 3]; grey[i + 3] = 255; }
        return grey;
    }

    private static async Task Save(string path, byte[] px, int w, int h)
    {
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, px);
        await enc.FlushAsync();
    }
}
