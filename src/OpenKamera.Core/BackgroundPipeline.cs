using System.Diagnostics;

namespace OpenKamera.Core;

/// <summary>Owns the segmenters (created on first use) and applies background effects to frames.</summary>
public sealed class BackgroundPipeline : IDisposable
{
    private readonly Dictionary<SegmentationQuality, PersonSegmenter> _segmenters = [];
    private readonly object _gate = new();

    /// <summary>Milliseconds spent in segmentation + compositing for the last frame.</summary>
    public double LastMs { get; private set; }
    public string? LastError { get; private set; }
    public bool UsesGpu => _segmenters.Values.Any(s => s.UsesGpu);

    public void Apply(byte[] px, int w, int h, BackgroundSettings s)
    {
        if (!s.NeedsMask) return;
        lock (_gate)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!_segmenters.TryGetValue(s.Quality, out var seg))
                    _segmenters[s.Quality] = seg = new PersonSegmenter(s.Quality);
                var mask = seg.Segment(px, w, h);
                var alpha = MaskOps.Upsample(mask, w, h);
                byte[]? bg = s.Mode == BackgroundMode.Replace && s.BackgroundId is { } id ? BackgroundLibrary.Render(id, w, h) : null;
                BackgroundEffects.Apply(px, w, h, alpha, s, bg);
                LastError = null;
            }
            catch (Exception ex) { LastError = ex.Message; }
            LastMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _segmenters.Values) s.Dispose();
            _segmenters.Clear();
        }
    }
}
