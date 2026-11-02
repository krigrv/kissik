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

    private float[]? _lastAlpha; private int _lastW, _lastH;
    private float[]? _reuse; private int _reuseW, _reuseH; private long _frameNo; private bool _hadError;

    /// <summary>The person cut-out (0..1 per pixel) of the frame just processed, or a fresh one if none was needed.</summary>
    public float[] PersonMask(byte[] px, int w, int h, BackgroundSettings s)
    {
        lock (_gate)
        {
            if (_lastAlpha is { } a && _lastW == w && _lastH == h) return a;
            if (!_segmenters.TryGetValue(s.Quality, out var seg)) _segmenters[s.Quality] = seg = new PersonSegmenter(s.Quality);
            var alpha = s.GreenScreen ? ChromaKey.Mask((byte[])px.Clone(), w, h, s.KeyColor, s.Similarity, s.Smoothness, 0)
                                      : MaskOps.Upsample(seg.Segment(px, w, h), w, h);
            (_lastAlpha, _lastW, _lastH) = (alpha, w, h);
            return alpha;
        }
    }

    /// <summary>Call once per frame before processing, so <see cref="PersonMask"/> never reuses an old frame's cut-out.</summary>
    public void NewFrame() { lock (_gate) { _lastAlpha = null; _lastMask = null; } }

    private PersonMask? _lastMask, _reuseMask;

    /// <summary>
    /// GPU path: this frame's cut-out at model resolution (the GPU upsamples it), with the same every-other-frame
    /// reuse as <see cref="Apply"/>. Null on error. Not for green screen, which keys at full size on the CPU.
    /// </summary>
    public PersonMask? ModelMask(byte[] px, int w, int h, BackgroundSettings s)
    {
        lock (_gate)
        {
            if (_lastMask is { } done) return done;
            var sw = Stopwatch.StartNew();
            try
            {
                PersonMask mask;
                if (_reuseMask is { } prev && (++_frameNo & 1) == 1 && !_hadError) mask = prev;
                else
                {
                    if (!_segmenters.TryGetValue(s.Quality, out var seg))
                        _segmenters[s.Quality] = seg = new PersonSegmenter(s.Quality);
                    mask = _reuseMask = seg.Segment(px, w, h);
                }
                LastError = null; _hadError = false;
                return _lastMask = mask;
            }
            catch (Exception ex) { LastError = ex.Message; _hadError = true; _reuseMask = null; return null; }
            finally { LastMs = sw.Elapsed.TotalMilliseconds; }
        }
    }

    public void Apply(byte[] px, int w, int h, BackgroundSettings s)
    {
        if (!s.NeedsMask) return;
        lock (_gate)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                float[] alpha;
                if (s.GreenScreen)
                    alpha = ChromaKey.Mask(px, w, h, s.KeyColor, s.Similarity, s.Smoothness, s.Spill);
                else if (_reuse is { } prev && _reuseW == w && _reuseH == h && (++_frameNo & 1) == 1 && !_hadError)
                    alpha = prev; // the cut-out barely moves in 1/30 s: reuse it every other frame (halves the AI cost)
                else
                {
                    if (!_segmenters.TryGetValue(s.Quality, out var seg))
                        _segmenters[s.Quality] = seg = new PersonSegmenter(s.Quality);
                    alpha = MaskOps.Upsample(seg.Segment(px, w, h), w, h);
                    (_reuse, _reuseW, _reuseH) = (alpha, w, h);
                }
                (_lastAlpha, _lastW, _lastH) = (alpha, w, h);
                byte[]? bg = s.Mode == BackgroundMode.Replace && s.BackgroundId is { } id ? BackgroundLibrary.Render(id, w, h) : null;
                BackgroundEffects.Apply(px, w, h, alpha, s, bg);
                LastError = null; _hadError = false;
            }
            catch (Exception ex) { LastError = ex.Message; _hadError = true; _reuse = null; }
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
