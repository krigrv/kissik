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
foreach (var (w, h) in only is "decode" or "phone" ? [] : new[] { (1920, 1080), (3840, 2160) })
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
            // As the app runs it with a recording on: full-size bake for the outputs, the preview read back at 1080p.
            using var bg = new BackgroundPipeline();
            for (int i = 0; i < 5; i++) FrameProcessor.Process(nv12, blur, bg, bake, (1920, 1080));
            var sw = Stopwatch.StartNew(); int n = 0;
            while (sw.Elapsed.TotalSeconds < 3) { FrameProcessor.Process(nv12, blur, bg, bake, (1920, 1080)); n++; }
            Console.WriteLine($"{w}x{h} gpu nv12 blur+bake, preview at 1080p: {n / sw.Elapsed.TotalSeconds:F1} fps");
        }
        if (GpuRecorder.Available)
        {
            // Recording only, as the app runs it on the GPU encoder: the bake becomes a texture (no readback of it),
            // the preview comes back at 1080p, and a real recorder encodes to a temporary file.
            string file = Path.Combine(Path.GetTempPath(), $"kissik-bench-{w}x{h}.mp4");
            var rec = new GpuRecorder(w, h, 30);
            await rec.StartAsync(file);
            var texBake = bake with { Texture = true, Bytes = false };
            using var bg = new BackgroundPipeline();
            for (int i = 0; i < 5; i++) FrameProcessor.Process(nv12, blur, bg, texBake, (1920, 1080)).Baked?.Texture?.Dispose();
            var sw = Stopwatch.StartNew(); int n = 0;
            while (sw.Elapsed.TotalSeconds < 3)
            {
                var o = FrameProcessor.Process(nv12, blur, bg, texBake, (1920, 1080));
                if (o.Baked?.Texture is { } t) rec.PushTexture(t, sw.Elapsed);
                n++;
            }
            double fps = n / sw.Elapsed.TotalSeconds;
            await rec.StopAsync();
            await rec.DisposeAsync();
            Console.WriteLine($"{w}x{h} gpu nv12 blur+bake, recording from textures: {fps:F1} fps ({rec.FramesWritten} encoded, {rec.FramesDropped} dropped while the encoder was busy)");
            try { File.Delete(file); } catch { }
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

// Phone pictures (bench "phone"): the decoder's NV12 to a frame on the GPU, against the old CPU conversion.
if (only is "" or "phone" or "gpu" && GpuEffects.Shared is not null)
{
    FrameProcessor.UseGpu = true;
    foreach (var (pw, ph, rot) in new[] { (1080, 1920, 0), (1920, 1080, 90), (3840, 2160, 0), (3840, 2160, 90) })
    {
        int pitch = (pw + 63) & ~63; // decoder buffers are padded
        var nv12 = new byte[pitch * ph * 3 / 2];
        new Random(1).NextBytes(nv12);
        var bgra = new byte[pw * ph * 4];
        double Ms(Action a) { for (int i = 0; i < 5; i++) a(); var sw = Stopwatch.StartNew(); int n = 0; while (sw.Elapsed.TotalSeconds < 2) { a(); n++; } return sw.Elapsed.TotalMilliseconds / n; }
        double cpu = Ms(() => PhoneVideo.Nv12ToBgra(nv12, pitch, pw, ph, pitch * ph, rot, bgra, out _, out _));
        var packed = new byte[pw * ph * 3 / 2];
        int ow = 0, oh = 0;
        double hand = Ms(() => PhoneVideo.Nv12Rotate(nv12, pitch, pw, ph, pitch * ph, rot, packed, out ow, out oh));
        double gpu = Ms(() =>
        {
            PhoneVideo.Nv12Rotate(nv12, pitch, pw, ph, pitch * ph, rot, packed, out ow, out oh);
            using var bmp = SoftwareBitmap.CreateCopyFromBuffer(packed.AsBuffer(), BitmapPixelFormat.Nv12, ow, oh);
            YuvConvert.MarkBt709(bmp);
            FrameProcessor.Process(bmp, FrameRecipe.Passthrough, null, null, (1920, 1080));
        });
        Console.WriteLine($"phone {pw}x{ph} turned {rot}: CPU to BGRA {cpu:F1} ms; NV12 handoff {hand:F1} ms, whole frame on the GPU incl. preview readback {gpu:F1} ms");
    }
}

// Live phone decode (bench "decode <clip.h264>..."): packets paced at 60 per second as the phone sends them, decoded
// (hardware when there is one), then handed to the frame path on the decoder thread as the app does.
if (only == "decode")
{
    FrameProcessor.UseGpu = true;
    foreach (var clip in args.Skip(1))
        foreach (var (nv12, rot) in new[] { (false, 0), (true, 0), (false, 90), (true, 90) })
        {
            if (nv12 && !FrameProcessor.GpuActive) continue;
            using var dec = H264Decoder.Create();
            dec.DropStaleFrames = true; dec.OutputNv12 = nv12; dec.Rotation = rot;
            var session = new CaptureSession();
            await session.StartExternalAsync();
            using var bg = new BackgroundPipeline();
            var recipe = cases[0].Recipe; // blur, as a typical live setup
            int shown = 0; double firstMs = -1, lastMs = 0;
            var clock = Stopwatch.StartNew();
            session.FrameAvailable += bmp =>
            {
                FrameProcessor.Process(bmp, recipe, bg, null, (1920, 1080));
                double now = clock.Elapsed.TotalMilliseconds;
                if (firstMs < 0) firstMs = now; else shown++;
                lastMs = now;
            };
            dec.FrameDecoded += f => session.PushBgra(f);
            var packets = PhoneVideo.SplitAccessUnits(File.ReadAllBytes(clip)).ToList();
            var sw = Stopwatch.StartNew();
            long t = 0; int i = 0;
            foreach (var (data, key) in packets)
            {
                var due = TimeSpan.FromSeconds(i++ / 60.0);
                while (sw.Elapsed < due) Thread.Sleep(1);
                dec.Push(new PhoneVideoPacket(key, t += 16_667, data));
            }
            Thread.Sleep(300);
            double secs = (lastMs - firstMs) / 1000; // steady state: from the first picture to the last
            Console.WriteLine($"{Path.GetFileNameWithoutExtension(clip)} turned {rot} {(nv12 ? "NV12 to GPU" : "BGRA on CPU")}: {shown / secs:F1} shown/s of 60 ({(dec.Hardware ? "hardware" : "software")} decode), " +
                $"read+convert {dec.ConvertMs:F1} ms, frame path {dec.HandlerMs:F1} ms per picture");
            await session.StopAsync();
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
