using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OpenKamera.Core;

/// <summary>Fast = MediaPipe selfie (256²); High = MODNet portrait matting (short edge 512 → 288 for speed).</summary>
public enum SegmentationQuality { Fast, High }

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
    /// <summary>The model's input size; frames are sampled (nearest) to this before inference.</summary>
    public int InputWidth => _inW;
    public int InputHeight => _inH;
    public bool UsesGpu { get; }

    /// <summary>0 = no smoothing; higher keeps more of the previous mask (reduces edge flicker).</summary>
    public float Smoothing { get; set; } = 0.35f;

    /// <summary>Removes stray blobs (chairs, lamps) that are much smaller than the main person.</summary>
    public bool KeepMainSubjects { get; set; } = true;
    /// <summary>A separate region survives if it is at least this share of the largest one (a second person does).</summary>
    public float MinRegionShare { get; set; } = 0.35f;

    public static string ModelPath(SegmentationQuality q) => Path.Combine(AppContext.BaseDirectory, "Models",
        q == SegmentationQuality.Fast ? "selfie_segmentation_fp16.onnx" : "modnet_fp16.onnx");

    public PersonSegmenter(SegmentationQuality quality, bool preferGpu = true)
    {
        Quality = quality;
        (_inW, _inH, _scale, _offset) = quality == SegmentationQuality.Fast
            ? (256, 256, 1f, 0f)
            : (512, 288, 2f, -1f); // MODNet: (x - 0.5) / 0.5, sides multiple of 32
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

        // Sample at model resolution (nearest is enough here and much faster than bilinear).
        var r = new byte[plane]; var g = new byte[plane]; var b = new byte[plane];
        for (int y = 0; y < _inH; y++)
        {
            int sy = Math.Min(height - 1, (int)((y + 0.5f) * height / _inH));
            for (int x = 0; x < _inW; x++)
            {
                int sx = Math.Min(width - 1, (int)((x + 0.5f) * width / _inW));
                int si = (sy * width + sx) * 4, di = y * _inW + x;
                r[di] = bgra[si + 2]; g[di] = bgra[si + 1]; b[di] = bgra[si];
            }
        }

        // Models are trained on well-lit photos; stretch dark or flat frames so they can see the person.
        var curve = AutoLevels ? ExposureCurve(r, g, b) : null;
        float k = _scale / 255f;
        for (int p = 0; p < plane; p++)
        {
            buf[p] = (curve is null ? r[p] : curve[r[p]]) * k + _offset;
            buf[plane + p] = (curve is null ? g[p] : curve[g[p]]) * k + _offset;
            buf[2 * plane + p] = (curve is null ? b[p] : curve[b[p]]) * k + _offset;
        }

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, input)]);
        var outTensor = results.First().AsTensor<float>();
        var alpha = new float[plane];
        int i = 0;
        foreach (var v in outTensor) { if (i >= plane) break; alpha[i++] = Math.Clamp(v, 0f, 1f); }

        if (KeepMainSubjects) MaskOps.DropSmallRegions(alpha, _inW, _inH, MinRegionShare);

        // Motion-aware smoothing: hold the edge steady where little changed (no shimmer), follow at once where you moved (no lag trail).
        if (_previous is { } prev && prev.Length == plane && Smoothing > 0)
            for (int j = 0; j < plane; j++)
            {
                float d = Math.Abs(alpha[j] - prev[j]);
                float keep = d < 0.12f ? Math.Min(0.8f, Smoothing * 2f) : d < 0.35f ? Smoothing * 0.5f : 0f;
                alpha[j] = alpha[j] * (1 - keep) + prev[j] * keep;
            }
        _previous = alpha;
        return new PersonMask(alpha, _inW, _inH);
    }

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

    /// <summary>Forget the previous mask (call after a camera switch or big cut).</summary>
    public void Reset() => _previous = null;

    public void Dispose() => _session.Dispose();
}

public static class MaskOps
{
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
    /// Fast guided filter (He et al.): snaps the upsampled mask to the real edges in the full-resolution picture,
    /// so hair and shoulders follow the image instead of the blocky model grid. Works at 1/scale size, then applies
    /// the per-pixel linear model at full size, only on edge pixels.
    /// </summary>
    public static void GuidedRefine(float[] alpha, byte[] bgra, int w, int h, int scale = 4, int radius = 6, float eps = 0.002f)
    {
        int sw = Math.Max(1, w / scale), sh = Math.Max(1, h / scale), n = sw * sh;
        var I = new float[n]; var p = new float[n];
        Parallel.For(0, sh, y =>
        {
            int sy = Math.Min(h - 1, y * scale + scale / 2);
            for (int x = 0; x < sw; x++)
            {
                int sx = Math.Min(w - 1, x * scale + scale / 2), i = (sy * w + sx) * 4;
                I[y * sw + x] = (bgra[i] * 0.114f + bgra[i + 1] * 0.587f + bgra[i + 2] * 0.299f) / 255f;
                p[y * sw + x] = alpha[sy * w + sx];
            }
        });
        var Ip = new float[n]; var II = new float[n];
        for (int i = 0; i < n; i++) { Ip[i] = I[i] * p[i]; II[i] = I[i] * I[i]; }
        var mI = Box(I, sw, sh, radius); var mp = Box(p, sw, sh, radius); var mIp = Box(Ip, sw, sh, radius); var mII = Box(II, sw, sh, radius);
        var a = new float[n]; var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float cov = mIp[i] - mI[i] * mp[i], vr = mII[i] - mI[i] * mI[i];
            a[i] = cov / (vr + eps); b[i] = mp[i] - a[i] * mI[i];
        }
        var ma = Box(a, sw, sh, radius); var mb = Box(b, sw, sh, radius);
        float fx = (float)sw / w, fy = (float)sh / h;
        Parallel.For(0, h, y =>
        {
            float gy = Math.Clamp((y + 0.5f) * fy - 0.5f, 0, sh - 1); int y0 = (int)gy, y1 = Math.Min(y0 + 1, sh - 1); float dy = gy - y0;
            for (int x = 0; x < w; x++)
            {
                int k = y * w + x;
                float o = alpha[k];
                if (o <= 0.001f || o >= 0.999f) continue; // solid inside or outside: nothing to refine
                float gx = Math.Clamp((x + 0.5f) * fx - 0.5f, 0, sw - 1); int x0 = (int)gx, x1 = Math.Min(x0 + 1, sw - 1); float dx = gx - x0;
                float A = Lerp2(ma, sw, x0, x1, y0, y1, dx, dy), B = Lerp2(mb, sw, x0, x1, y0, y1, dx, dy);
                int i = k * 4;
                float lum = (bgra[i] * 0.114f + bgra[i + 1] * 0.587f + bgra[i + 2] * 0.299f) / 255f;
                float v = Math.Clamp((A * lum + B - 0.15f) / 0.7f, 0, 1); // crisp the soft band (smoothstep 0.15..0.85)
                alpha[k] = v * v * (3 - 2 * v);
            }
        });
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
