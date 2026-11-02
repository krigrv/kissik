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
    ("blur+framing", FrameRecipe.Passthrough with { Background = new BackgroundSettings(BackgroundMode.Blur, 0.6), Transform = new FrameTransform(Mirror: true, Zoom: 1.5) }),
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
        if (!FrameProcessor.UseGpu) continue;
        // The app's path: NV12 straight from the camera, plus the outputs' bake (front overlay, captions, shutter).
        using var nv12 = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Nv12);
        var front = Layer(w, h, 40); var caption = Layer(w / 2, h / 10, 200);
        var bake = new OutputBake((_, _) => front, null, caption, w / 2, h / 10);
        var blur = cases[0].Recipe;
        foreach (var (name, input, b) in new[] { ("nv12 blur", nv12, (OutputBake?)null), ("bgra blur+bake", bmp, bake), ("nv12 blur+bake", nv12, bake), ("nv12 blur+bake+shutter", nv12, bake with { Shutter = ShutterMode.Blur }), ("nv12 blur+bake, meeting camera size", nv12, bake with { FitWithin = (1920, 1080) }) })
        {
            using var bg = new BackgroundPipeline();
            for (int i = 0; i < 5; i++) FrameProcessor.Process(input, blur, bg, b);
            var sw = Stopwatch.StartNew(); int n = 0;
            while (sw.Elapsed.TotalSeconds < 3) { FrameProcessor.Process(input, blur, bg, b); n++; }
            Console.WriteLine($"{w}x{h} gpu {name}: {n / sw.Elapsed.TotalSeconds:F1} fps");
        }
        {
            // What the same bake costs on the CPU (MainPage's fallback), per frame, on top of the frame path.
            var frame = new byte[w * h * 4];
            bmp.CopyToBuffer(frame.AsBuffer());
            var sw = Stopwatch.StartNew(); int n = 0;
            while (sw.Elapsed.TotalSeconds < 3)
            {
                var x = (byte[])frame.Clone(); SceneCompositor.BlendOver(x, front);
                var c = (byte[])x.Clone(); CaptionBurn.Blend(c, w, h, caption, w / 2, h / 10);
                PrivacyShutter.Apply(c, w, h, ShutterMode.Blur); n++;
            }
            Console.WriteLine($"{w}x{h} cpu bake+shutter alone: {sw.Elapsed.TotalMilliseconds / n:F1} ms per frame");
            sw.Restart(); n = 0;
            while (sw.Elapsed.TotalSeconds < 2) { OutputFit.Stretch(frame, w, h, Math.Min(w, 1920), Math.Min(h, 1080)); n++; }
            Console.WriteLine($"{w}x{h} cpu shrink to meeting camera size: {sw.Elapsed.TotalMilliseconds / n:F1} ms per frame");
        }
    }
}

// A premultiplied layer: a translucent band every few rows.
static byte[] Layer(int w, int h, int every)
{
    var px = new byte[w * h * 4];
    for (int y = 0; y < h; y += every) for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; px[i] = px[i + 1] = px[i + 2] = 100; px[i + 3] = 128; }
    return px;
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
