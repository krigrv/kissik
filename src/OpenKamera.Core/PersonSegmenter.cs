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

        if (_previous is { } prev && prev.Length == plane && Smoothing > 0)
            for (int j = 0; j < plane; j++) alpha[j] = alpha[j] * (1 - Smoothing) + prev[j] * Smoothing;
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
