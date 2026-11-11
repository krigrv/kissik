using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>
/// Edge quality of the person cut-out on real stock photos (curly and loose hair), with the real High-quality model.
/// Set KISSIK_EDGE_OUT to a folder to also save the Blur / Replace results and head crops for a visual check.
/// </summary>
[Collection("Timing")]
public class EdgeQualityTests(ITestOutputHelper log)
{
    private const int W = 1920, H = 1080;

    [Theory]
    [InlineData("creative")]
    [InlineData("streaming")]
    [InlineData("social")]
    public async Task BlurLeavesNoSharpRoomRoundThePerson(string name)
    {
        var (px, alpha) = await Cut(name);
        var blurred = (byte[])px.Clone();
        BackgroundEffects.Apply(blurred, W, H, alpha, new BackgroundSettings(BackgroundMode.Blur, 0.6));
        double halo = HaloRatio(px, blurred, alpha);
        log.WriteLine($"{name}: sharp detail left in the ring round the person = {halo:P1} of the original");
        Save(name, "blur", blurred, alpha);
        Assert.True(halo < Limits.Halo, $"halo {halo:P1}");
    }

    [Theory]
    [InlineData("creative")]
    [InlineData("streaming")]
    [InlineData("social")]
    public async Task ReplaceCarriesLittleOfTheOldRoom(string name)
    {
        var (px, alpha) = await Cut(name);
        var result = (byte[])px.Clone();
        BackgroundEffects.Apply(result, W, H, alpha, new BackgroundSettings(BackgroundMode.Replace, BackgroundId: "x"), Flat(Green));
        double bleed = Bleed(px, result, alpha);
        log.WriteLine($"{name}: old-room colour left in the edge band = {bleed:F2} levels");
        Save(name, "replace", result, alpha);
        Assert.True(bleed < Limits.Bleed, $"bleed {bleed:F2}");
    }

    /// <summary>
    /// The beige room seen through the gaps between locs (left of the head) and along the shoulder must not survive
    /// a Replace with Studio Grey. Counts pixels the result left as the old room: unchanged from the photo, close to
    /// the nearby room colour and clearly not the new background.
    /// </summary>
    [Theory]
    [InlineData("creative", SegmentationQuality.Fast)]
    [InlineData("streaming", SegmentationQuality.Fast)]
    [InlineData("social", SegmentationQuality.Fast)]
    [InlineData("creative", SegmentationQuality.High)]
    [InlineData("streaming", SegmentationQuality.High)]
    [InlineData("social", SegmentationQuality.High)]
    public async Task NoRoomShowsBetweenStrandsOnReplace(string name, SegmentationQuality q)
    {
        var (px, alpha) = await Cut(name, q);
        var grey = BackgroundLibrary.Render("studio-grey", W, H)!;
        var result = (byte[])px.Clone();
        BackgroundEffects.Apply(result, W, H, alpha, new BackgroundSettings(BackgroundMode.Replace, BackgroundId: "studio-grey"), grey);
        var (left, total) = RoomLeft(px, result, grey, alpha);
        log.WriteLine($"{name} ({q}): {left} old-room pixels left round the person ({(double)left / total:P2} of the edge zone)");
        Save(name, $"grey-{q}", result, alpha);
        Assert.True((double)left / total < Limits.RoomLeft, $"{left} of {total}");
    }

    /// <summary>Old-room pixels in the zone within 40 px of the person, and the zone's size.</summary>
    internal static (int Left, int Total) RoomLeft(byte[] orig, byte[] result, byte[] bg, float[] alpha)
    {
        const int r = 40, step = 8;
        // The room colour: per 8×8 cell, the mean of the photo where the cut-out says room (alpha < 0.02) within r.
        int cw = W / step + 1, ch = H / step + 1;
        var room = new float[cw * ch * 3]; var has = new bool[cw * ch];
        Parallel.For(0, ch, cy =>
        {
            for (int cx = 0; cx < cw; cx++)
            {
                double s0 = 0, s1 = 0, s2 = 0; int n = 0;
                for (int y = Math.Max(0, cy * step - r); y < Math.Min(H, cy * step + r); y += 3)
                    for (int x = Math.Max(0, cx * step - r); x < Math.Min(W, cx * step + r); x += 3)
                    {
                        int k = y * W + x;
                        if (alpha[k] >= 0.02f) continue;
                        s0 += orig[k * 4]; s1 += orig[k * 4 + 1]; s2 += orig[k * 4 + 2]; n++;
                    }
                int c = cy * cw + cx;
                if (n < 20) continue;
                has[c] = true; room[c * 3] = (float)(s0 / n); room[c * 3 + 1] = (float)(s1 / n); room[c * 3 + 2] = (float)(s2 / n);
            }
        });
        var ring = Ring(alpha, r);
        int left = 0, total = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int k = y * W + x, c = (y / step) * cw + x / step;
                if (!(ring[k] || alpha[k] is > 0.02f and < 0.999f) || !has[c]) continue;
                total++;
                int i = k * 4;
                double dRoom = 0, dBg = 0;
                for (int j = 0; j < 3; j++) { dRoom += Math.Abs(result[i + j] - room[c * 3 + j]); dBg += Math.Abs(result[i + j] - bg[i + j]); }
                if (dRoom < 30 && dBg > 60) left++;
            }
        return (left, Math.Max(1, total));
    }

    [Fact]
    public async Task EdgeHoldsStillOnAStillPicture()
    {
        var px = await Load("creative");
        using var seg = new PersonSegmenter(SegmentationQuality.High, preferGpu: false);
        var rng = new Random(3);
        float[]? prev = null; double sum = 0; long n = 0;
        for (int f = 0; f < 6; f++)
        {
            var noisy = (byte[])px.Clone(); // sensor noise, as a webcam adds even when nothing moves
            for (int i = 0; i < noisy.Length; i++) if ((i & 3) != 3) noisy[i] = (byte)Math.Clamp(noisy[i] + rng.Next(-6, 7), 0, 255);
            var alpha = MaskOps.Matte(seg.Segment(noisy, W, H), noisy, W, H);
            if (prev is not null)
                for (int i = 0; i < alpha.Length; i++)
                    if (alpha[i] is > 0.02f and < 0.98f || prev[i] is > 0.02f and < 0.98f) { sum += Math.Abs(alpha[i] - prev[i]); n++; }
            prev = alpha;
        }
        double flicker = sum / Math.Max(1, n);
        log.WriteLine($"mean edge change between frames: {flicker:F4} over {n / 5} edge pixels per frame");
        Assert.True(flicker < Limits.Flicker, $"flicker {flicker:F4}");
    }

    [Trait("Category", "Hardware")] // speed depends on a real GPU; cloud machines only have a software one
    [Fact]
    public async Task MatteAndBlurFitTheFrameBudget()
    {
        var px = await Load("social");
        using var seg = new PersonSegmenter(SegmentationQuality.High, preferGpu: false);
        var mask = seg.Segment(px, W, H);
        double best = double.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var f = (byte[])px.Clone();
            var sw = Stopwatch.StartNew();
            var a = MaskOps.Matte(mask, f, W, H);
            BackgroundEffects.Apply(f, W, H, a, new BackgroundSettings(BackgroundMode.Blur, 0.6));
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        log.WriteLine($"CPU matte + blur at 1080p: {best:F1} ms");
        Assert.True(best < 80, $"{best:F1} ms"); // the CPU fallback; the GPU path is the 60 fps one

        if (GpuEffects.Shared is not { } gpu) return;
        var r = FrameRecipe.Passthrough with { Background = new BackgroundSettings(BackgroundMode.Blur, 0.6) };
        gpu.Process((byte[])px.Clone(), W, H, r, mask, null);
        double gbest = double.MaxValue;
        for (int i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            gpu.Process((byte[])px.Clone(), W, H, r, mask, null);
            gbest = Math.Min(gbest, sw.Elapsed.TotalMilliseconds);
        }
        log.WriteLine($"GPU matte + blur at 1080p incl. upload and read-back: {gbest:F1} ms");
        Assert.True(gbest < 16, $"{gbest:F1} ms");
    }

    /// <summary>
    /// The person from <paramref name="name"/> put in front of a sharp, busy room (bookshelf-like blocks and stripes),
    /// using the photo's own cut-out as the true edge: the hard case for halos, as a real webcam room is in focus.
    /// </summary>
    internal static async Task<(byte[] Px, float[] Truth)> BusyRoom(string name)
    {
        var (px, truth) = await Cut(name);
        var room = new byte[px.Length];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int cell = (x / 37) * 131 + (y / 53) * 71, i = (y * W + x) * 4;
                int stripe = ((x + y / 3) / 6) % 2 * 60;
                room[i] = (byte)((cell * 37 + stripe) % 256); room[i + 1] = (byte)((cell * 91 + stripe * 2) % 256); room[i + 2] = (byte)((cell * 53 + 255 - stripe) % 256);
            }
        for (int i = 0, k = 0; k < truth.Length; i += 4, k++)
        {
            float a = truth[k];
            for (int c = 0; c < 3; c++) px[i + c] = (byte)(px[i + c] * a + room[i + c] * (1 - a) + 0.5f);
        }
        return (px, truth);
    }

    [Theory]
    [InlineData("creative")]
    [InlineData("streaming")]
    [InlineData("social")]
    public async Task BusyRoomStaysOutOfTheOutline(string name)
    {
        var (px, truth) = await BusyRoom(name);
        using var seg = new PersonSegmenter(SegmentationQuality.High, preferGpu: false);
        var alpha = MaskOps.Matte(seg.Segment(px, W, H), px, W, H);
        var blurred = (byte[])px.Clone();
        BackgroundEffects.Apply(blurred, W, H, alpha, new BackgroundSettings(BackgroundMode.Blur, 0.6));
        double err = 0; long n = 0;
        for (int i = 0; i < truth.Length; i++)
            if (truth[i] is > 0.01f and < 0.99f || alpha[i] is > 0.01f and < 0.99f) { err += Math.Abs(truth[i] - alpha[i]); n++; }
        err /= Math.Max(1, n);
        double halo = HaloRatio(px, blurred, truth);
        log.WriteLine($"{name} in a busy room: sharp room left round the person = {halo:P1}, mean edge error {err:F3}");
        Save(name, "busy", blurred, truth);
        Assert.True(halo < Limits.BusyHalo, $"halo {halo:P1}");
        Assert.True(err < Limits.EdgeError, $"edge error {err:F3}");
    }

    private static class Limits
    {
        // Measured on this change (old pipeline in brackets): halo 26-46% (37-49%), bleed 0.8-2.4 (2.3-11.7),
        // flicker 0.12 (0.34), busy-room halo 4-15% (4-20%), busy-room edge error 0.13-0.34 (0.29-0.41).
        public const double Halo = 0.5, Bleed = 4, Flicker = 0.18, BusyHalo = 0.18, EdgeError = 0.37,
            RoomLeft = 0.012; // 0.02-0.86% now (the rest is mostly a chair the model counts as person); the Fast model let 0.3-1% through before
    }

    private static readonly byte[] Green = [64, 177, 0]; // B, G, R

    /// <summary>
    /// High-frequency detail (|pixel − 3×3 mean| on luma) in the 24 px ring just outside the person, after blurring,
    /// as a share of the same in the original. A clean blur leaves almost none; a halo of untouched room leaves a lot.
    /// </summary>
    internal static double HaloRatio(byte[] orig, byte[] outPx, float[] alpha)
    {
        var ring = Ring(alpha, 24);
        double o = 0, b = 0;
        for (int y = 1; y < H - 1; y++)
            for (int x = 1; x < W - 1; x++)
            {
                if (!ring[y * W + x]) continue;
                o += Detail(orig, x, y); b += Detail(outPx, x, y);
            }
        return b / Math.Max(1, o);
    }

    /// <summary>Pixels outside the person (alpha &lt; 0.5) within <paramref name="r"/> px of it.</summary>
    private static bool[] Ring(float[] alpha, int r)
    {
        var inside = new int[(W + 1) * (H + 1)];
        for (int y = 0; y < H; y++)
        {
            int row = 0;
            for (int x = 0; x < W; x++) { row += alpha[y * W + x] >= 0.5f ? 1 : 0; inside[(y + 1) * (W + 1) + x + 1] = inside[y * (W + 1) + x + 1] + row; }
        }
        var ring = new bool[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (alpha[y * W + x] >= 0.5f) continue;
                int ya = Math.Max(0, y - r), yb = Math.Min(H, y + r + 1), xa = Math.Max(0, x - r), xb = Math.Min(W, x + r + 1);
                ring[y * W + x] = inside[yb * (W + 1) + xb] - inside[ya * (W + 1) + xb] - inside[yb * (W + 1) + xa] + inside[ya * (W + 1) + xa] > 0;
            }
        return ring;
    }

    private static float Detail(byte[] px, int x, int y)
    {
        float Lum(int xx, int yy) { int i = (yy * W + xx) * 4; return px[i] * 0.114f + px[i + 1] * 0.587f + px[i + 2] * 0.299f; }
        float s = 0;
        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) s += Lum(x + dx, y + dy);
        return Math.Abs(Lum(x, y) - s / 9);
    }

    /// <summary>
    /// Old-room colour left in the soft edge band of a green replacement. An edge pixel of the photo is
    /// a·person + (1 − a)·room; a clean result swaps that room part for green. The room colour is sampled from the
    /// nearby photo where the cut-out says "room" (a &lt; 0.05), and the residual is measured along room − green, in levels.
    /// </summary>
    internal static double Bleed(byte[] orig, byte[] result, float[] alpha)
    {
        const int r = 12;
        double sum = 0; long n = 0;
        for (int y = r; y < H - r; y += 2)
            for (int x = r; x < W - r; x += 2)
            {
                float a = alpha[y * W + x];
                if (a is <= 0.1f or >= 0.9f) continue;
                double bw = 0, r0 = 0, r1 = 0, r2 = 0;
                for (int dy = -r; dy <= r; dy += 3)
                    for (int dx = -r; dx <= r; dx += 3)
                    {
                        int k = (y + dy) * W + x + dx;
                        if (alpha[k] >= 0.05f) continue;
                        bw++; r0 += orig[k * 4]; r1 += orig[k * 4 + 1]; r2 += orig[k * 4 + 2];
                    }
                if (bw < 3) continue;
                double[] room = [r0 / bw, r1 / bw, r2 / bw];
                int i = (y * W + x) * 4;
                double dot = 0, len = 0;
                for (int c = 0; c < 3; c++)
                {
                    double dir = room[c] - Green[c];
                    double clean = orig[i + c] + (1 - a) * (Green[c] - room[c]);
                    dot += (result[i + c] - clean) * dir; len += dir * dir;
                }
                if (len < 400) continue; // room too close to green to tell apart
                sum += Math.Max(0, dot / Math.Sqrt(len)); n++;
            }
        return sum / Math.Max(1, n);
    }

    private static byte[] Flat(byte[] bgr)
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = bgr[0]; px[i + 1] = bgr[1]; px[i + 2] = bgr[2]; px[i + 3] = 255; }
        return px;
    }

    private static readonly Dictionary<string, (byte[], float[])> _cuts = [];

    /// <summary>The photo at 1080p and its cut-out (on the CPU, refined as the pipeline does).</summary>
    private static async Task<(byte[] Px, float[] Alpha)> Cut(string name, SegmentationQuality q = SegmentationQuality.High)
    {
        string key = $"{name}/{q}";
        lock (_cuts) if (_cuts.TryGetValue(key, out var c)) return ((byte[])c.Item1.Clone(), c.Item2);
        var px = await Load(name);
        using var seg = new PersonSegmenter(q, preferGpu: false);
        var alpha = MaskOps.Matte(seg.Segment(px, W, H), px, W, H);
        lock (_cuts) _cuts[key] = (px, alpha);
        return ((byte[])px.Clone(), alpha);
    }

    // Fixed copies of the website's stock people. The limits below are tuned to these exact photos,
    // so the website can change its pictures without moving the tests.
    private const string EdgePhotos = "tests/OpenKamera.Core.Tests/Assets/edge";

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, EdgePhotos))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    /// <summary>Cover-fits a stock photo to 1920×1080 BGRA.</summary>
    private static async Task<byte[]> Load(string name)
    {
        string path = Path.Combine(Repo(), EdgePhotos, name + ".jpg");
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var dec = await BitmapDecoder.CreateAsync(stream);
        double scale = Math.Max((double)W / dec.PixelWidth, (double)H / dec.PixelHeight);
        uint sw = (uint)Math.Ceiling(dec.PixelWidth * scale), sh = (uint)Math.Ceiling(dec.PixelHeight * scale);
        var t = new BitmapTransform
        {
            ScaledWidth = sw, ScaledHeight = sh, InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (sw - W) / 2, Y = (sh - H) / 2, Width = W, Height = H },
        };
        var data = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, t,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return data.DetachPixelData();
    }

    /// <summary>Saves the whole frame and a 2× crop of the top of the head when KISSIK_EDGE_OUT is set.</summary>
    private static void Save(string name, string mode, byte[] px, float[] alpha)
    {
        if (Environment.GetEnvironmentVariable("KISSIK_EDGE_OUT") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        WritePng(Path.Combine(dir, $"{name}-{mode}.png"), px, W, H);
        int top = 0, cx = W / 2; // top of the head: the first row with a solid run of person
        for (int y = 0; y < H && top == 0; y++)
        {
            int run = 0;
            for (int x = 0; x < W; x++) { run = alpha[y * W + x] > 0.5f ? run + 1 : 0; if (run > 40) { top = y; cx = x - 20; break; } }
        }
        const int cw = 480, ch = 270;
        int x0 = Math.Clamp(cx - cw / 2, 0, W - cw), y0 = Math.Clamp(top - 60, 0, H - ch);
        var crop = new byte[cw * 2 * ch * 2 * 4];
        for (int y = 0; y < ch * 2; y++)
            for (int x = 0; x < cw * 2; x++)
                Array.Copy(px, ((y0 + y / 2) * W + x0 + x / 2) * 4, crop, (y * cw * 2 + x) * 4, 4);
        WritePng(Path.Combine(dir, $"{name}-{mode}-hair.png"), crop, cw * 2, ch * 2);
    }

    private static void WritePng(string path, byte[] px, int w, int h)
    {
        var copy = (byte[])px.Clone();
        for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
        using var ms = new InMemoryRandomAccessStream();
        var enc = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms).AsTask().GetAwaiter().GetResult();
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, copy);
        enc.FlushAsync().AsTask().GetAwaiter().GetResult();
        var bytes = new byte[ms.Size];
        ms.Seek(0);
        ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
        File.WriteAllBytes(path, bytes);
    }
}
