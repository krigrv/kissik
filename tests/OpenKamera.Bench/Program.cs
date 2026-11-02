using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenKamera.Core;
using Windows.Graphics.Imaging;

// Frame-path benchmark: dotnet run -c Release --project tests/OpenKamera.Bench [cpu|gpu]
// Runs the full FrameProcessor (segmentation included) on a synthetic frame and prints fps.
string only = args.Length > 0 ? args[0] : "";
var cases = new (string Name, FrameRecipe Recipe)[]
{
    ("blur", FrameRecipe.Passthrough with { Background = new BackgroundSettings(BackgroundMode.Blur, 0.6) }),
    ("replace", FrameRecipe.Passthrough with { Background = new BackgroundSettings(BackgroundMode.Replace, BackgroundId: BackgroundLibrary.BuiltIn[0].Id) }),
    ("look+lut", FrameRecipe.Passthrough with { Adjustments = new ImageFilters.Adjustments(Contrast: 1.1, Saturation: 1.2, Sharpness: 0.5, Filter: FilterPreset.Cinematic) }),
};
foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
{
    using var bmp = Synthetic(w, h);
    foreach (var mode in new[] { "cpu", "gpu" })
    {
        if (only != "" && only != mode) continue;
        FrameProcessor.UseGpu = mode == "gpu";
        if (FrameProcessor.UseGpu && GpuEffects.Shared is null) { Console.WriteLine("gpu: unavailable"); continue; }
        foreach (var (name, recipe) in cases)
        {
            using var bg = new BackgroundPipeline();
            for (int i = 0; i < 5; i++) FrameProcessor.ProcessPixels(bmp, recipe, bg); // warm up
            var sw = Stopwatch.StartNew(); int n = 0;
            while (sw.Elapsed.TotalSeconds < 3) { FrameProcessor.ProcessPixels(bmp, recipe, bg); n++; }
            Console.WriteLine($"{w}x{h} {mode} {name}: {n / sw.Elapsed.TotalSeconds:F1} fps{(bg.LastError is { } e ? " (" + e + ")" : "")}");
        }
    }
}

static SoftwareBitmap Synthetic(int w, int h)
{
    var px = new byte[w * h * 4];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            // A skin-toned oval "person" on a textured room.
            float dx = (x - w / 2f) / (w * 0.18f), dy = (y - h * 0.55f) / (h * 0.4f);
            bool person = dx * dx + dy * dy < 1;
            px[i] = (byte)(person ? 120 : (x * 7 ^ y * 3) & 255);
            px[i + 1] = (byte)(person ? 150 : (x + y) & 255);
            px[i + 2] = (byte)(person ? 200 : (x * 3 + y * 5) & 255);
            px[i + 3] = 255;
        }
    return SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
}
