using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OpenKamera.Core;

/// <summary>Fast = MediaPipe selfie (256²); High = MODNet portrait matting (short edge 512 → 288 for speed).</summary>
/// <summary>
/// Fast and High look at each frame on its own. Video (Robust Video Matting, GPL-3.0) remembers the frames before,
/// so the edge holds steady, and it was trained to cut people out of moving video rather than single photos.
/// </summary>
public enum SegmentationQuality { Fast, High, Video }

/// <summary>A person mask: one 0..1 alpha per pixel at the model's resolution.</summary>
public sealed record PersonMask(float[] Alpha, int Width, int Height);

/// <summary>
/// Runs a person-segmentation ONNX model on BGRA frames. GPU (DirectML) when available, CPU otherwise.
/// Not thread-safe: call <see cref="Segment"/> from one thread at a time.
/// </summary>
public sealed class PersonSegmenter : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inW, _inH;
    private readonly float _scale, _offset; // input = pixel/255 * scale + offset
    private float[]? _previous;             // temporal smoothing state

    public SegmentationQuality Quality { get; }
    /// <summary>The model's input size; frames are area-averaged to this before inference.</summary>
    public int InputWidth => _inW;
    public int InputHeight => _inH;
    public bool UsesGpu { get; }

    /// <summary>0 = no smoothing; higher keeps more of the previous mask (reduces edge flicker).</summary>
    public float Smoothing { get; set; } = 0.35f;

    /// <summary>Removes stray blobs (chairs, lamps) that are much smaller than the main person.</summary>
    public bool KeepMainSubjects { get; set; } = true;
    /// <summary>A separate region survives if it is at least this share of the largest one (a second person does).</summary>
    public float MinRegionShare { get; set; } = 0.35f;

    public static string ModelPath(SegmentationQuality q) => Path.Combine(AppContext.BaseDirectory, "Models", q switch
    {
        SegmentationQuality.Fast => "selfie_segmentation_fp16.onnx",
        SegmentationQuality.Video => "rvm_mobilenetv3_fp32.onnx",
        _ => "modnet_fp16.onnx",
    });

    // Video model only: what it remembers from the frames before (four feature maps), fed back in each time.
    private readonly bool _recurrent;
    private (float[] Data, int[] Shape)[]? _memory;
    private static readonly string[] MemoryIn = ["r1i", "r2i", "r3i", "r4i"];
    /// <summary>The video model works on a shrunken copy inside and sharpens the result back up; this is that shrink.</summary>
    private readonly float _downsample = 1f;

    public PersonSegmenter(SegmentationQuality quality, bool preferGpu = true)
    {
        Quality = quality;
        (_inW, _inH, _scale, _offset) = quality switch
        {
            SegmentationQuality.Fast => (256, 256, 1f, 0f),
            SegmentationQuality.Video => (VideoWidth, VideoHeight, 1f, 0f), // plain 0..1 RGB
            _ => (512, 288, 2f, -1f), // MODNet: (x - 0.5) / 0.5, sides multiple of 32
        };
        _recurrent = quality == SegmentationQuality.Video;
        if (_recurrent) { _downsample = VideoDownsample; Smoothing = 0; } // it does its own smoothing over time
        string path = ModelPath(quality);
        if (!File.Exists(path)) throw new FileNotFoundException("Segmentation model missing.", path);

        if (preferGpu)
        {
            try
            {
                var gpu = new SessionOptions();
                gpu.AppendExecutionProvider_DML(0);
                _session = new InferenceSession(path, gpu);
                UsesGpu = true;
            }
            catch (Exception) { /* no DirectML device: fall back to CPU */ }
        }
        _session ??= new InferenceSession(path, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <summary>Segments a BGRA frame. The returned mask is at model resolution; use <see cref="MaskOps.Upsample"/>.</summary>
    public PersonMask Segment(byte[] bgra, int width, int height)
    {
        var input = new DenseTensor<float>([1, 3, _inH, _inW]);
        var buf = input.Buffer.Span;
        int plane = _inW * _inH;

        // Average each model pixel's footprint in the frame: nearest sampling passed webcam noise and aliasing
        // straight to the model, which made the edge shimmer from frame to frame.
        var r = new byte[plane]; var g = new byte[plane]; var b = new byte[plane];
        Parallel.For(0, _inH, y =>
        {
            int ya = Span(y, height, _inH), yb = Math.Max(ya + 1, Span(y + 1, height, _inH));
            for (int x = 0; x < _inW; x++)
            {
                int xa = Span(x, width, _inW), xb = Math.Max(xa + 1, Span(x + 1, width, _inW));
                int sb = 0, sg = 0, sr = 0, n = (yb - ya) * (xb - xa);
                for (int sy = ya; sy < yb; sy++)
                    for (int si = (sy * width + xa) * 4, end = si + (xb - xa) * 4; si < end; si += 4) { sb += bgra[si]; sg += bgra[si + 1]; sr += bgra[si + 2]; }
                int di = y * _inW + x;
                r[di] = (byte)((sr + n / 2) / n); g[di] = (byte)((sg + n / 2) / n); b[di] = (byte)((sb + n / 2) / n);
            }
        });

        // Models are trained on well-lit photos; stretch dark or flat frames so they can see the person.
        var curve = AutoLevels ? ExposureCurve(r, g, b) : null;
        float k = _scale / 255f;
        for (int p = 0; p < plane; p++)
        {
            buf[p] = (curve is null ? r[p] : curve[r[p]]) * k + _offset;
            buf[plane + p] = (curve is null ? g[p] : curve[g[p]]) * k + _offset;
            buf[2 * plane + p] = (curve is null ? b[p] : curve[b[p]]) * k + _offset;
        }

        var alpha = new float[plane];
        if (_recurrent) RunVideoModel(input, alpha);
        else
        {
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, input)]);
            var outTensor = results.First().AsTensor<float>();
            int i = 0;
            foreach (var v in outTensor) { if (i >= plane) break; alpha[i++] = Math.Clamp(v, 0f, 1f); }
        }

        if (KeepMainSubjects) MaskOps.DropSmallRegions(alpha, _inW, _inH, MinRegionShare);

        // Motion-aware smoothing: hold the edge steady where little changed (no shimmer), follow at once where you moved (no lag trail).
        if (_previous is { } prev && prev.Length == plane && Smoothing > 0)
            for (int j = 0; j < plane; j++)
            {
                float d = Math.Abs(alpha[j] - prev[j]);
                // Hold strongly through noise-sized changes, let go smoothly as the change grows (no step to ripple on).
                float t = Math.Clamp((d - 0.04f) / 0.26f, 0, 1);
                float keep = Math.Min(0.85f, Smoothing * 2.4f) * (1 - t * t * (3 - 2 * t));
                alpha[j] = alpha[j] * (1 - keep) + prev[j] * keep;
            }
        _previous = alpha;
        return new PersonMask(alpha, _inW, _inH);
    }

    /// <summary>First frame row/column covered by model pixel <paramref name="i"/> of <paramref name="n"/> (integer, so the GPU agrees).</summary>
    internal static int Span(int i, int size, int n) => (int)((long)i * size / n);

    /// <summary>Normalise exposure of the model's input (not the video) so dark rooms still segment.</summary>
    public bool AutoLevels { get; set; } = true;

    /// <summary>
    /// 256-entry curve mapping the frame's 1st–99th luminance percentiles to the full range, with a gamma
    /// lift when the frame is dark. Returns null when the frame is already well exposed.
    /// </summary>
    internal static byte[]? ExposureCurve(byte[] r, byte[] g, byte[] b)
    {
        var hist = new int[256];
        for (int i = 0; i < r.Length; i++) hist[(r[i] * 77 + g[i] * 150 + b[i] * 29) >> 8]++;
        int n = r.Length, lo = 0, hi = 255, acc = 0;
        for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= n / 100) { lo = v; break; } }
        acc = 0;
        for (int v = 255; v >= 0; v--) { acc += hist[v]; if (acc >= n / 100) { hi = v; break; } }
        double mean = 0;
        for (int v = 0; v < 256; v++) mean += v * (double)hist[v];
        mean /= n;
        if (hi - lo > 180 && mean > 70) return null; // already well exposed
        hi = Math.Max(hi, lo + 16);
        // Lift midtones so the mean lands near 0.45 after stretching.
        double stretchedMean = Math.Clamp((mean - lo) / (hi - lo), 0.02, 0.98);
        double gamma = Math.Clamp(Math.Log(0.45) / Math.Log(stretchedMean), 0.35, 1.0);
        var curve = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            double t = Math.Clamp((v - lo) / (double)(hi - lo), 0, 1);
            curve[v] = (byte)Math.Round(Math.Pow(t, gamma) * 255);
        }
        return curve;
    }

    /// <summary>Input size and inner shrink for the video model (overridable while tuning: KISSIK_VIDEO_MATTE=width,height,shrink).</summary>
    internal static int VideoWidth = 512, VideoHeight = 288;
    internal static float VideoDownsample = 1f;

    static PersonSegmenter()
    {
        var parts = Environment.GetEnvironmentVariable("KISSIK_VIDEO_MATTE")?.Split(',');
        if (parts is { Length: 3 } && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
            && float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out float d))
            (VideoWidth, VideoHeight, VideoDownsample) = (w, h, d);
    }

    /// <summary>One step of the video model: the frame plus what it remembers go in; the cut-out and new memory come out.</summary>
    private void RunVideoModel(DenseTensor<float> input, float[] alpha)
    {
        var feeds = new List<NamedOnnxValue>(6) { NamedOnnxValue.CreateFromTensor("src", input) };
        for (int m = 0; m < 4; m++)
        {
            // Nothing remembered yet: a single zero stands for "start fresh".
            var (data, shape) = _memory is { } mem ? mem[m] : (new float[1], new[] { 1, 1, 1, 1 });
            feeds.Add(NamedOnnxValue.CreateFromTensor(MemoryIn[m], new DenseTensor<float>(data, shape)));
        }
        feeds.Add(NamedOnnxValue.CreateFromTensor("downsample_ratio", new DenseTensor<float>(new[] { _downsample }, new[] { 1 })));
        using var results = _session.Run(feeds);
        var memory = new (float[] Data, int[] Shape)[4];
        foreach (var r in results)
        {
            if (r.Name == "pha")
            {
                var t = r.AsTensor<float>();
                int i = 0;
                foreach (var v in t) { if (i >= alpha.Length) break; alpha[i++] = Math.Clamp(v, 0f, 1f); }
            }
            else if (r.Name.Length == 3 && r.Name[0] == 'r' && r.Name[2] == 'o')
            {
                var t = r.AsTensor<float>();
                memory[r.Name[1] - '1'] = (t.ToArray(), t.Dimensions.ToArray());
            }
        }
        if (memory.All(m => m.Data is not null)) _memory = memory;
    }

    /// <summary>Forget the previous mask (call after a camera switch or big cut).</summary>
    public void Reset() { _previous = null; _memory = null; }

    public void Dispose() => _session.Dispose();
}

public static class MaskOps
{
    /// <summary>The full-size person matte for a frame: the model mask upsampled, then snapped to the picture's edges.</summary>
    public static float[] Matte(PersonMask m, byte[] bgra, int w, int h)
    {
        // A wide soft ramp keeps faint hair in play; the colour guide then decides what is person. A wide pass first
        // reaches room the model swallowed (gaps between locs, between arm and body), then a fine pass sets the edge.
        var alpha = Upsample(m, w, h, 0.1f, 0.9f);
        int scale = GuideScale(w);
        GuidedRefine(alpha, bgra, w, h, scale, WideRadius, carve: true);
        GuidedRefine(alpha, bgra, w, h, scale, FineRadius);
        return alpha;
    }

    /// <summary>Guide window radii (on the 1/scale grid) of the two refine passes.</summary>
    public const int WideRadius = 24, FineRadius = 8;

    /// <summary>The guide's downscale for a frame width: half size at 1080p, so single strands still register.</summary>
    public static int GuideScale(int w) => Math.Max(2, w / 960);

    /// <summary>
    /// Labels 4-connected regions of the mask (alpha &gt; 0.15) and clears every region smaller than
    /// <paramref name="minShare"/> × the largest region.
    /// </summary>
    public static void DropSmallRegions(float[] alpha, int w, int h, float minShare)
    {
        var label = new int[alpha.Length];
        var sizes = new List<int> { 0 };
        var stack = new Stack<int>();
        for (int start = 0; start < alpha.Length; start++)
        {
            if (label[start] != 0 || alpha[start] <= 0.15f) continue;
            int id = sizes.Count, size = 0;
            label[start] = id; stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop(); size++;
                int x = p % w, y = p / w;
                if (x > 0) Visit(p - 1);
                if (x < w - 1) Visit(p + 1);
                if (y > 0) Visit(p - w);
                if (y < h - 1) Visit(p + w);
            }
            sizes.Add(size);

            void Visit(int q)
            {
                if (label[q] == 0 && alpha[q] > 0.15f) { label[q] = id; stack.Push(q); }
            }
        }
        if (sizes.Count <= 2) return; // zero or one region
        int largest = sizes.Max();
        int keepFrom = (int)(largest * minShare);
        for (int i = 0; i < alpha.Length; i++)
            if (label[i] != 0 && sizes[label[i]] < keepFrom) alpha[i] = 0;
    }

    /// <summary>
    /// Fast colour guided filter (He et al.): snaps the upsampled mask to the real edges in the full-resolution
    /// picture, so hair and shoulders follow the image instead of the blocky model grid. Guided by colour, not just
    /// brightness, so a room with the hair's brightness but another colour does not stick to the outline. Works at
    /// 1/scale size, then applies the per-pixel linear model at full size: on the edge band, or (carve) on the solid inside, so room the
    /// model swallowed whole (gaps between locs, between arm and body) is still caught where its colour says room.
    /// </summary>
    /// <param name="carve">Only lower the solid inside (alpha 1) where the colour says room, leaving the edge band to a
    /// later fine pass. Otherwise refine the edge band and leave solid areas alone.</param>
    public static void GuidedRefine(float[] alpha, byte[] bgra, int w, int h, int scale = 4, int radius = 6, float eps = 0.001f, bool carve = false)
    {
        int sw = Math.Max(1, w / scale), sh = Math.Max(1, h / scale), n = sw * sh;
        // Guide planes: r, g, b, p, then r·p, g·p, b·p, then rr, rg, rb, gg, gb, bb.
        var ch = new float[13][];
        for (int c = 0; c < 13; c++) ch[c] = new float[n];
        Parallel.For(0, sh, y =>
        {
            int sy = Math.Min(h - 1, y * scale + scale / 2);
            for (int x = 0; x < sw; x++)
            {
                int sx = Math.Min(w - 1, x * scale + scale / 2), i = (sy * w + sx) * 4, k = y * sw + x;
                float r = bgra[i + 2] / 255f, g = bgra[i + 1] / 255f, b = bgra[i] / 255f, q = alpha[sy * w + sx];
                ch[0][k] = r; ch[1][k] = g; ch[2][k] = b; ch[3][k] = q;
                ch[4][k] = r * q; ch[5][k] = g * q; ch[6][k] = b * q;
                ch[7][k] = r * r; ch[8][k] = r * g; ch[9][k] = r * b; ch[10][k] = g * g; ch[11][k] = g * b; ch[12][k] = b * b;
            }
        });
        var m = new float[13][];
        Parallel.For(0, 13, c => m[c] = Box(ch[c], sw, sh, radius));
        var ab = new float[4][];
        for (int c = 0; c < 4; c++) ab[c] = new float[n];
        Parallel.For(0, n, k =>
        {
            var (ar, ag, ab_, bb) = Solve(m[0][k], m[1][k], m[2][k], m[3][k], m[4][k], m[5][k], m[6][k],
                m[7][k], m[8][k], m[9][k], m[10][k], m[11][k], m[12][k], eps);
            ab[0][k] = ar; ab[1][k] = ag; ab[2][k] = ab_; ab[3][k] = bb;
        });
        var mab = new float[4][];
        Parallel.For(0, 4, c => mab[c] = Box(ab[c], sw, sh, radius));
        float fx = (float)sw / w, fy = (float)sh / h;
        Parallel.For(0, h, y =>
        {
            float gy = Math.Clamp((y + 0.5f) * fy - 0.5f, 0, sh - 1); int y0 = (int)gy, y1 = Math.Min(y0 + 1, sh - 1); float dy = gy - y0;
            for (int x = 0; x < w; x++)
            {
                int k = y * w + x;
                float o = alpha[k];
                if (carve ? o < 0.999f : o <= 0.001f || o >= 0.999f) continue;
                float gx = Math.Clamp((x + 0.5f) * fx - 0.5f, 0, sw - 1); int x0 = (int)gx, x1 = Math.Min(x0 + 1, sw - 1); float dx = gx - x0;
                int i = k * 4;
                float v = Lerp2(mab[0], sw, x0, x1, y0, y1, dx, dy) * (bgra[i + 2] / 255f)
                        + Lerp2(mab[1], sw, x0, x1, y0, y1, dx, dy) * (bgra[i + 1] / 255f)
                        + Lerp2(mab[2], sw, x0, x1, y0, y1, dx, dy) * (bgra[i] / 255f)
                        + Lerp2(mab[3], sw, x0, x1, y0, y1, dx, dy);
                // Carve: only clear room comes out (under 0.4), so a colour merely close to the room (a guitar) stays.
                // Edge band: firmed up a little (smoothstep 0.05..0.95).
                v = carve ? Math.Clamp((v - 0.1f) / 0.3f, 0, 1) : Math.Clamp((v - 0.05f) / 0.9f, 0, 1);
                alpha[k] = v * v * (3 - 2 * v);
            }
        });
    }

    /// <summary>
    /// Per-window colour model alpha ≈ a·(r, g, b) + b from the window means: a = (Σ + eps·I)⁻¹ · cov(I, p).
    /// The GPU's GuideAB does the same sums in the same order.
    /// </summary>
    internal static (float Ar, float Ag, float Ab, float B) Solve(float mr, float mg, float mb, float mp,
        float mrp, float mgp, float mbp, float rr, float rg, float rb, float gg, float gb, float bb, float eps)
    {
        float crr = rr - mr * mr + eps, crg = rg - mr * mg, crb = rb - mr * mb;
        float cgg = gg - mg * mg + eps, cgb = gb - mg * mb, cbb = bb - mb * mb + eps;
        float pr = mrp - mr * mp, pg = mgp - mg * mp, pb = mbp - mb * mp;
        // Inverse of the symmetric 3×3 via cofactors.
        float i00 = cgg * cbb - cgb * cgb, i01 = crb * cgb - crg * cbb, i02 = crg * cgb - crb * cgg;
        float i11 = crr * cbb - crb * crb, i12 = crb * crg - crr * cgb, i22 = crr * cgg - crg * crg;
        float det = crr * i00 + crg * i01 + crb * i02;
        float ar = (i00 * pr + i01 * pg + i02 * pb) / det;
        float ag = (i01 * pr + i11 * pg + i12 * pb) / det;
        float ab = (i02 * pr + i12 * pg + i22 * pb) / det;
        return (ar, ag, ab, mp - ar * mr - ag * mg - ab * mb);
    }

    private static float Lerp2(float[] g, int gw, int x0, int x1, int y0, int y1, float dx, float dy)
    {
        float t = g[y0 * gw + x0] + (g[y0 * gw + x1] - g[y0 * gw + x0]) * dx;
        float u = g[y1 * gw + x0] + (g[y1 * gw + x1] - g[y1 * gw + x0]) * dx;
        return t + (u - t) * dy;
    }

    /// <summary>Mean over a (2r+1) square window via a summed-area table (edges average the part inside the image).</summary>
    private static float[] Box(float[] src, int w, int h, int r)
    {
        var sat = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double row = 0;
            for (int x = 0; x < w; x++) { row += src[y * w + x]; sat[(y + 1) * (w + 1) + x + 1] = sat[y * (w + 1) + x + 1] + row; }
        }
        var dst = new float[w * h];
        Parallel.For(0, h, y =>
        {
            int ya = Math.Max(0, y - r), yb = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Max(0, x - r), xb = Math.Min(w, x + r + 1);
                double s = sat[yb * (w + 1) + xb] - sat[ya * (w + 1) + xb] - sat[yb * (w + 1) + xa] + sat[ya * (w + 1) + xa];
                dst[y * w + x] = (float)(s / ((yb - ya) * (xb - xa)));
            }
        });
        return dst;
    }

    /// <summary>Bilinear upsample of a model mask to frame size, with a soft edge ramp.</summary>
    public static float[] Upsample(PersonMask m, int width, int height, float edgeLow = 0.25f, float edgeHigh = 0.75f)
    {
        var dst = new float[width * height];
        float sxScale = (float)m.Width / width, syScale = (float)m.Height / height;
        float span = Math.Max(1e-3f, edgeHigh - edgeLow);
        Parallel.For(0, height, y =>
        {
            float fy = Math.Clamp((y + 0.5f) * syScale - 0.5f, 0, m.Height - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, m.Height - 1); float dy = fy - y0;
            for (int x = 0; x < width; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * sxScale - 0.5f, 0, m.Width - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, m.Width - 1); float dx = fx - x0;
                float a = m.Alpha[y0 * m.Width + x0] * (1 - dx) + m.Alpha[y0 * m.Width + x1] * dx;
                float b = m.Alpha[y1 * m.Width + x0] * (1 - dx) + m.Alpha[y1 * m.Width + x1] * dx;
                float v = a * (1 - dy) + b * dy;
                v = Math.Clamp((v - edgeLow) / span, 0, 1);
                dst[y * width + x] = v * v * (3 - 2 * v); // smoothstep
            }
        });
        return dst;
    }
}
