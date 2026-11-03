using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace OpenKamera.Core;

/// <summary>
/// Direct3D 11 compute version of the per-frame effects: mask upsampling, retouch, blur / replace / remove,
/// spotlight, "behind me" layers, colour and looks (tone, hue, filters, LUT), sharpening and the scene.
/// The frame goes up once and comes back once; every step in between stays on the GPU.
/// Mirrors the CPU code in <see cref="BackgroundEffects"/>, <see cref="ImageFilters"/> and <see cref="SceneCompositor"/>
/// (which remain the fallback) to within a level or two per channel.
/// </summary>
public sealed class GpuEffects : IDisposable
{
    private static readonly Lazy<GpuEffects?> _shared = new(TryCreate);
    /// <summary>The process-wide engine, or null when there is no Direct3D 11 GPU.</summary>
    public static GpuEffects? Shared => _shared.Value;

    public string AdapterName { get; }

    /// <summary>True when the device was made with video support, so a hardware encoder can take its textures.</summary>
    public bool VideoSupport { get; }

    /// <summary>The device frames live on (the recorder hands it to Media Foundation).</summary>
    internal ID3D11Device Device => _device;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly Dictionary<string, ID3D11ComputeShader> _cs = [];
    private readonly ID3D11Buffer _params;
    private readonly Dictionary<string, Slot> _slots = [];
    private readonly object _gate = new();

    private sealed class Slot(ID3D11Buffer buffer, ID3D11ShaderResourceView srv, ID3D11UnorderedAccessView? uav, int bytes) : IDisposable
    {
        public ID3D11Buffer Buffer = buffer; public ID3D11ShaderResourceView Srv = srv; public ID3D11UnorderedAccessView? Uav = uav; public int Bytes = bytes;
        public void Dispose() { Uav?.Dispose(); Srv.Dispose(); Buffer.Dispose(); }
    }

    private static GpuEffects? TryCreate()
    {
        try { return new GpuEffects(); }
        catch (Exception) { return null; }
    }

    private GpuEffects()
    {
        // Video support lets the hardware encoder share this device (recording straight from textures); some drivers
        // refuse it, and then the frame path runs without.
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.VideoSupport, [FeatureLevel.Level_11_0],
                out _device!, out _ctx!).Failure)
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, [FeatureLevel.Level_11_0],
                out _device!, out _ctx!).CheckError();
        else VideoSupport = true;
        // Media Foundation uses the immediate context from its own threads.
        using (var mt = _device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        using (var dxgi = _device.QueryInterface<Vortice.DXGI.IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
            AdapterName = adapter.Description.Description;

        using var stream = typeof(GpuEffects).Assembly.GetManifestResourceStream("OpenKamera.Core.GpuEffects.hlsl")!;
        string source = new StreamReader(stream).ReadToEnd();
        foreach (var entry in new[] { "Transform", "Fit", "Small", "Upsample", "GuideDown", "GuideBoxH", "GuideBoxV", "GuideAB", "GuideApply", "Retouch", "BlurDown", "BoxH", "BoxV", "Effects", "Sharpen", "Scene", "Nv12", "Yuy2", "Over", "Fade", "Caption", "PixBlock", "PixFill", "Slide", "Zoom", "Place", "ToTexture" })
        {
            var code = Compiler.Compile(source, entry, "GpuEffects.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
            _cs[entry] = _device.CreateComputeShader(code.Span);
        }
        // Exact match with the CPU's double-precision resize where the GPU has doubles (nearly all do).
        try
        {
            var doubles = _device.CheckFeatureSupport<FeatureDataDoubles>(Feature.Doubles);
            if (doubles.DoublePrecisionFloatShaderOps)
                _cs["FitDouble"] = _device.CreateComputeShader(Compiler.Compile(source, "FitDouble", "GpuEffects.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3).Span);
        }
        catch (Exception) { /* float fit only */ }
        _params = _device.CreateBuffer((uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Int4(int x, int y, int z, int w) { public int X = x, Y = y, Z = z, W = w; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Float4(float x, float y, float z, float w) { public float X = x, Y = y, Z = z, W = w; }

    // Must match the cbuffer in GpuEffects.hlsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Int4 I0, I1, I2, I3, I4;
        public Float4 F0, F1, F2, F3, F4, F5, F6, F7, F8, F9;
    }

    [Flags]
    private enum Fx { Temp = 1, Tint = 2, Hue = 4, Vib = 8, Sat = 16, Lut = 32, Colour = 64, Behind = 128, Spot = 256 }
    [Flags]
    private enum Sf { Fills = 1, Ellipse = 2, Camera = 4, Overlay = 8, Underlay = 16, Under = 32 }

    /// <summary>
    /// Runs every effect after framing and output fit. <paramref name="mask"/> is the model-resolution person mask
    /// (null when no effect needs one); <paramref name="replacement"/> is the Replace picture at frame size.
    /// Returns BGRA at the output size, or null if the GPU failed (the caller falls back to the CPU path).
    /// </summary>
    public byte[]? Process(byte[] px, int w, int h, FrameRecipe r, PersonMask? mask, byte[]? replacement)
    {
        int n = w * h;
        if (n == 0 || n > 65535 * 256 * 64) return null;
        lock (_gate)
        {
            try
            {
                Upload("frame", px, 4);
                return Download(Effects("frame", w, h, r, mask, replacement, SceneCompositor.HasTransparency(px)), n * 4);
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// The whole frame path from the camera's pixels: framing, output fit, the model's input for segmentation, then
    /// every effect. Only the model's small input comes back to the CPU before the finished frame does.
    /// Null if the GPU failed (the caller falls back to the CPU path).
    /// </summary>
    public (byte[] Pixels, int Width, int Height)? ProcessFrame(byte[] raw, int rw, int rh, FrameRecipe r, BackgroundPipeline? background) =>
        ProcessOutput(raw, rw, rh, r, background, null) is { } o ? (o.Pixels, o.Width, o.Height) : null;

    /// <summary>As <see cref="ProcessFrame"/>, plus the output bake (overlay, fade, captions, shutter) when asked for.</summary>
    public FrameOutput? ProcessOutput(byte[] raw, int rw, int rh, FrameRecipe r, BackgroundPipeline? background, OutputBake? bake,
        (int Width, int Height)? previewWithin = null)
    {
        if (rw * rh == 0) return null;
        lock (_gate)
        {
            try
            {
                Upload("raw", raw, 4);
                return Pipeline("raw", rw, rh, r, background, SceneCompositor.HasTransparency(raw), bake, previewWithin);
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// The frame path straight from the camera's NV12 or YUY2 bitmap: its bytes go up as they are (no CPU colour
    /// conversion, 1.5 or 2 bytes a pixel instead of 4) and become BGRA here. Null if the GPU failed.
    /// </summary>
    public FrameOutput? ProcessCamera(Windows.Graphics.Imaging.SoftwareBitmap yuv, FrameRecipe r, BackgroundPipeline? background, OutputBake? bake,
        (int Width, int Height)? previewWithin = null)
    {
        int w = yuv.PixelWidth, h = yuv.PixelHeight;
        if (w * h == 0 || w * h > 65535 * 256 * 64 || !YuvConvert.IsYuv(yuv)) return null;
        bool nv12 = yuv.BitmapPixelFormat == Windows.Graphics.Imaging.BitmapPixelFormat.Nv12;
        lock (_gate)
        {
            try
            {
                var (stride, uvOffset) = YuvConvert.Read(yuv, (ptr, bytes, stride, uv) => { UploadRaw("yuv", ptr, bytes); return (stride, uv); });
                var m = YuvConvert.IsBt709(yuv) ? YuvConvert.Bt709 : YuvConvert.Bt601;
                SetParams(new Params { I0 = new(w, h, uvOffset, stride), I1 = new(m.Rv, m.Gu, m.Gv, m.Bu) });
                Ensure("raw", w * h * 4, 4);
                Run(nv12 ? "Nv12" : "Yuy2", w * h, srv: [("yuv", 0)], uav: [("raw", 0)]);
                return Pipeline("raw", w, h, r, background, false, bake, previewWithin);
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>The camera's bytes converted to BGRA on the GPU and read back (tests compare it with <see cref="YuvConvert"/>).</summary>
    public byte[]? ConvertCamera(Windows.Graphics.Imaging.SoftwareBitmap yuv) =>
        ProcessCamera(yuv, FrameRecipe.Passthrough, null, null)?.Pixels;

    /// <summary>
    /// <paramref name="previewWithin"/>: the processed frame comes back shrunk to fit (OutputFit.Stretch maths) when it
    /// is larger, for a preview that does not need every pixel; the baked output keeps the full size.
    /// </summary>
    private FrameOutput? Pipeline(string input, int rw, int rh, FrameRecipe r, BackgroundPipeline? background, bool transparent, OutputBake? bake,
        (int Width, int Height)? previewWithin)
    {
        var (src, w, h) = Frame(input, rw, rh, r);
        if (w * h > 65535 * 256 * 64) return null;
        background?.NewFrame();
        var s = r.Background;
        bool wantsMask = background is not null && (s.NeedsMask || r.BehindOverlay is { } bo && bo.Length == w * h * 4);
        var mask = wantsMask ? background!.ModelMask((iw, ih) => Small(src, w, h, iw, ih), s) : null;
        byte[]? bg = s.NeedsMask ? background?.ReplacementFor(s, w, h) : null; // pictures or a looping video
        string cur = Effects(src, w, h, r, mask, bg, transparent);
        if (previewWithin is var (mw, mh) && Shrunk(w, h, mw, mh) is var (pw, ph) && (pw != w || ph != h))
        {
            Resample(cur, w, h, pw, ph, (double)pw / w, (double)ph / h, "preview");
            var preview = Download("preview", pw * ph * 4);
            return new FrameOutput(preview, pw, ph, bake is null ? null : Bake(cur, w, h, bake, null));
        }
        var pixels = Download(cur, w * h * 4);
        return new FrameOutput(pixels, w, h, bake is null ? null : Bake(cur, w, h, bake, pixels));
    }

    /// <summary>The size a w x h frame shrinks to so it fits within mw x mh (even sides; unchanged when it fits).</summary>
    public static (int Width, int Height) Shrunk(int w, int h, int mw, int mh)
    {
        if (w <= mw && h <= mh) return (w, h);
        double s = Math.Min((double)mw / w, (double)mh / h);
        return (Math.Max(2, (int)(w * s)) & ~1, Math.Max(2, (int)(h * s)) & ~1);
    }

    /// <summary>
    /// What the meeting camera, stream and recording get on top of the processed frame, as MainPage bakes it on the
    /// CPU: front overlay (SceneCompositor.BlendOver), scene cross-fade, burned-in captions (CaptionBurn.Blend) and the
    /// privacy shutter (PrivacyShutter.Apply). Returns <paramref name="pixels"/> itself when there is nothing to add.
    /// </summary>
    private BakedFrame Bake(string cur, int w, int h, OutputBake b, byte[]? pixels)
    {
        int n = w * h;
        string? k = null;
        var p = new Params { I0 = new(w, h, 0, 0) };
        void Pass(string kernel, string aux)
        {
            SetParams(p);
            string o = k == "k0" ? "k1" : "k0"; Ensure(o, n * 4, 4);
            Run(kernel, n, srv: [(k ?? cur, 0), (aux, 2)], uav: [(o, 0)]);
            k = o;
        }
        if (b.Front?.Invoke(w, h) is { } front && front.Length == n * 4) { UploadCached("front", front); Pass("Over", "front"); }
        // SceneTransitions.Apply, from the copy BeginFade took.
        void Transition(SceneTransition kind, double t)
        {
            double e = Math.Clamp(t, 0, 1); e = e * e * (3 - 2 * e); // smoothstep
            switch (kind)
            {
                case SceneTransition.Fade:
                    p.I1 = new((int)Math.Clamp(e * 256, 0, 256), 0, 0, 0);
                    Pass("Fade", "fadeFrom");
                    break;
                case SceneTransition.Slide:
                    int shift = (int)Math.Round(w * (1 - e));
                    if (shift <= 0) break;
                    p.I1 = new(shift, 0, 0, 0);
                    Pass("Slide", "fadeFrom");
                    break;
                case SceneTransition.Zoom:
                    double s = 1 + 0.25 * e;
                    float cx = w / 2f, cy = h / 2f;
                    var map = new uint[w + h];
                    for (int x = 0; x < w; x++) map[x] = (uint)(int)Math.Clamp(cx + (x - cx) / s, 0, w - 1);
                    for (int y = 0; y < h; y++) map[w + y] = (uint)(int)Math.Clamp(cy + (y - cy) / s, 0, h - 1);
                    Upload("map", map, 4);
                    p.I1 = new((int)Math.Clamp((1 - e) * 256, 0, 256), 0, 0, 0);
                    SetParams(p);
                    string o = k == "k0" ? "k1" : "k0"; Ensure(o, n * 4, 4);
                    Run("Zoom", n, srv: [(k ?? cur, 0), ("fadeFrom", 2), ("map", 8)], uav: [(o, 0)]);
                    k = o;
                    break;
            }
        }
        if (b.FadeT is double t && t < 1 && _fadeFrom == n) Transition(b.Transition, t);
        // The next scene change fades from this (captions and shutter excluded, as on the CPU).
        Copy(k ?? cur, "lastBaked", n * 4); _lastBaked = n;
        if (b.Caption is { } cap && b.CaptionWidth <= w && b.CaptionHeight <= h && cap.Length == b.CaptionWidth * b.CaptionHeight * 4)
        {
            UploadCached("caption", cap);
            p.I1 = new(b.CaptionWidth, b.CaptionHeight, (w - b.CaptionWidth) / 2, h - b.CaptionHeight - (int)(h * 0.06));
            Pass("Caption", "caption");
        }
        if (b.Shutter != ShutterMode.Off)
        {
            int block = Math.Max(8, w / 24), bx = (w + block - 1) / block, by = (h + block - 1) / block;
            p.I1 = new(block, bx, by, b.Shutter == ShutterMode.Away ? 1 : 0);
            SetParams(p);
            Ensure("blocks", bx * by * 4, 4);
            Run("PixBlock", bx * by, srv: [(k ?? cur, 0)], uav: [("blocks", 0)]);
            Pass("PixFill", "blocks");
        }
        // Only the meeting camera wants it: shrink to its size here, so less comes back (VirtualCameraChannel's maths).
        if (b.FitWithin is var (mw, mh) && (w > mw || h > mh))
        {
            var (nw, nh) = Shrunk(w, h, mw, mh);
            // The pause sign is drawn at the size that comes back, as the CPU draws it before the meeting camera shrinks it.
            Resample(k ?? cur, w, h, nw, nh, (double)nw / w, (double)nh / h, "fitout");
            var small = Download("fitout", nw * nh * 4);
            if (b.Shutter == ShutterMode.Away) PrivacyShutter.DrawPause(small, nw, nh);
            return new BakedFrame(small, nw, nh);
        }
        // The recorder can take a texture (the Away sign is drawn on the CPU, so that one comes back as bytes).
        GpuFrame? texture = b.Texture && VideoSupport && b.Shutter != ShutterMode.Away ? ToTexture(k ?? cur, w, h) : null;
        if (texture is not null && !b.Bytes) return new BakedFrame(null, w, h, texture);
        if (k is null) return new BakedFrame(pixels ?? Download(cur, n * 4), w, h, texture);
        var baked = Download(k, n * 4);
        if (b.Shutter == ShutterMode.Away) PrivacyShutter.DrawPause(baked, w, h);
        return new BakedFrame(baked, w, h, texture);
    }

    /// <summary>
    /// Copies a frame into a new B8G8R8A8 texture for the hardware encoder. A fresh texture per frame: the encoder
    /// holds samples for a while and frees each texture when it is done with it. Null if the GPU can't write that format.
    /// </summary>
    private GpuFrame? ToTexture(string src, int w, int h)
    {
        if (_textureFails) return null;
        try
        {
            var tex = _device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
                BindFlags.UnorderedAccess | BindFlags.ShaderResource | BindFlags.RenderTarget));
            using (var uav = _device.CreateUnorderedAccessView(tex, new UnorderedAccessViewDescription(tex, UnorderedAccessViewDimension.Texture2D)))
            {
                SetParams(new Params { I0 = new(w, h, 0, 0) });
                _ctx.CSSetShader(_cs["ToTexture"]);
                _ctx.CSSetConstantBuffer(0, _params);
                _ctx.CSSetShaderResource(0, Get(src).Srv);
                _ctx.CSSetUnorderedAccessView(3, uav);
                int groups = (w * h + 255) / 256, gx = Math.Min(groups, 65535), gy = (groups + gx - 1) / gx;
                _ctx.Dispatch((uint)gx, (uint)gy, 1);
                _ctx.CSSetShaderResource(0, null!);
                _ctx.CSSetUnorderedAccessView(3, null!);
            }
            return new GpuFrame(tex, w, h);
        }
        catch (Exception) { _textureFails = true; return null; }
    }

    private bool _textureFails;

    /// <summary>Reads a texture made by the frame path back as BGRA (tests).</summary>
    public byte[] ReadTexture(GpuFrame f)
    {
        lock (_gate)
        {
            using var staging = _device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)f.Width, (uint)f.Height, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _ctx.CopyResource(staging, f.Texture);
            var map = _ctx.Map(staging, 0, MapMode.Read);
            try
            {
                var px = new byte[f.Width * f.Height * 4];
                for (int y = 0; y < f.Height; y++) Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), px, y * f.Width * 4, f.Width * 4);
                return px;
            }
            finally { _ctx.Unmap(staging, 0); }
        }
    }

    /// <summary>Start a scene cross-fade from the last baked output frame (then pass <see cref="OutputBake.FadeT"/>).</summary>
    public void BeginFade()
    {
        lock (_gate)
        {
            if (_lastBaked == 0) return;
            try { Copy("lastBaked", "fadeFrom", _lastBaked * 4); _fadeFrom = _lastBaked; }
            catch (Exception) { _fadeFrom = 0; }
        }
    }

    private int _lastBaked, _fadeFrom; // pixel counts held in those buffers (0: none)

    /// <summary>FrameTransform.Apply then OutputFit.Cover, on the GPU. Returns the buffer holding the result.</summary>
    private (string Name, int W, int H) Frame(string src, int w, int h, FrameRecipe r)
    {
        var t = r.Transform;
        if (!t.IsIdentity)
        {
            int rot = ((t.Rotation % 360) + 360) % 360;
            if (rot % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.");
            bool swap = rot is 90 or 270;
            int ow = swap ? h : w, oh = swap ? w : h;
            double zoom = Math.Max(1.0, t.Zoom), cropW = w / zoom, cropH = h / zoom;
            double left = (w - cropW) / 2 * (1 + Math.Clamp(t.PanX, -1, 1)), top = (h - cropH) / 2 * (1 + Math.Clamp(t.PanY, -1, 1));
            // Same maths as FrameTransform.Apply, per output column and row: [sx(ox), sy(oy), sx(oy), sy(ox) for swapped axes].
            var map = new uint[(ow + oh) * 2];
            for (int o = 0; o < ow; o++) { var (sx, sy) = Source((o + 0.5) / ow, 0.5); map[o] = (uint)(swap ? sy : sx); }
            for (int o = 0; o < oh; o++) { var (sx, sy) = Source(0.5, (o + 0.5) / oh); map[ow + o] = (uint)sy; map[ow + oh + o] = (uint)sx; }
            (int, int) Source(double nx, double ny)
            {
                double u, v;
                switch (rot)
                {
                    case 90: u = ny; v = 1 - nx; break;
                    case 180: u = 1 - nx; v = 1 - ny; break;
                    case 270: u = 1 - ny; v = nx; break;
                    default: u = nx; v = ny; break;
                }
                if (t.Mirror) u = 1 - u;
                return (Math.Clamp((int)(left + u * cropW), 0, w - 1), Math.Clamp((int)(top + v * cropH), 0, h - 1));
            }
            Upload("map", map, 4);
            SetParams(new Params { I0 = new(ow, oh, 0, 0), I1 = new(w, h, swap ? 1 : 0, 0) });
            Ensure("framed", ow * oh * 4, 4);
            Run("Transform", ow * oh, srv: [(src, 0), ("map", 8)], uav: [("framed", 0)]);
            (src, w, h) = ("framed", ow, oh);
        }
        if (r.OutputWidth is int fw && r.OutputHeight is int fh && (fw != w || fh != h))
        {
            double scale = Math.Max((double)fw / w, (double)fh / h);
            Resample(src, w, h, fw, fh, scale, scale, "fitted");
            (src, w, h) = ("fitted", fw, fh);
        }
        return (src, w, h);
    }

    /// <summary>OutputFit's resample (Cover when the scales match, Stretch when not) into <paramref name="dst"/>.</summary>
    private void Resample(string src, int w, int h, int fw, int fh, double scaleX, double scaleY, string dst)
    {
        {
            double left = (w - fw / scaleX) / 2, top = (h - fh / scaleY) / 2;
            // Per column then per row: index, weight as double (two words); then the weights as float for the fallback.
            var map = new uint[(fw + fh) * 4];
            void Tap(int o, int n, int at, int fl, double pos, int size)
            {
                double f = Math.Clamp(pos, 0, size - 1), d = f - (int)f;
                ulong bits = BitConverter.DoubleToUInt64Bits(d);
                map[at + o] = (uint)(int)f; map[at + n + o] = (uint)bits; map[at + 2 * n + o] = (uint)(bits >> 32);
                map[fl + o] = BitConverter.SingleToUInt32Bits((float)d);
            }
            for (int o = 0; o < fw; o++) Tap(o, fw, 0, 3 * (fw + fh), left + (o + 0.5) / scaleX - 0.5, w);
            for (int o = 0; o < fh; o++) Tap(o, fh, 3 * fw, 3 * (fw + fh) + fw, top + (o + 0.5) / scaleY - 0.5, h);
            Upload("map", map, 4);
            SetParams(new Params { I0 = new(fw, fh, 0, 0), I1 = new(w, h, 0, 0) });
            Ensure(dst, fw * fh * 4, 4);
            Run(_cs.ContainsKey("FitDouble") ? "FitDouble" : "Fit", fw * fh, srv: [(src, 0), ("map", 8)], uav: [(dst, 0)]);
        }
    }

    /// <summary>The segmentation model's input: the frame area-averaged to iw x ih, as PersonSegmenter samples it.</summary>
    private byte[] Small(string src, int w, int h, int iw, int ih)
    {
        // Footprint starts worked out with PersonSegmenter's integer maths: iw + 1 column edges, then ih + 1 row edges.
        var map = new uint[iw + ih + 2];
        for (int x = 0; x <= iw; x++) map[x] = (uint)PersonSegmenter.Span(x, w, iw);
        for (int y = 0; y <= ih; y++) map[iw + 1 + y] = (uint)PersonSegmenter.Span(y, h, ih);
        Upload("map", map, 4);
        SetParams(new Params { I0 = new(w, h, 0, 0), I1 = new(iw, ih, 0, 0) });
        Ensure("small", iw * ih * 4, 4);
        Run("Small", iw * ih, srv: [(src, 0), ("map", 8)], uav: [("small", 0)]);
        return Download("small", iw * ih * 4);
    }

    private string Effects(string input, int w, int h, FrameRecipe r, PersonMask? mask, byte[]? replacement, bool transparent)
    {
        int n = w * h;
        var s = r.Background;
        var adj = r.Adjustments;
        var p = new Params { I0 = new(w, h, mask?.Width ?? 0, mask?.Height ?? 0) };

        string cur = input;
        string Next() => cur == "a" ? "b" : "a";

        bool useMask = mask is not null && (s.NeedsMask || r.BehindOverlay is not null);
        if (useMask)
        {
            Upload("maskLow", mask!.Alpha, 4);
            Ensure("alpha", n * 4, 4);
            SetParams(p);
            Run("Upsample", n, srv: [("maskLow", 6)], uav: [("alpha", 1)]);
            GuidedRefine(input, w, h, MaskOps.WideRadius, carve: true, ref p);
            GuidedRefine(input, w, h, MaskOps.FineRadius, carve: false, ref p);
        }
        else Ensure("alpha", 4, 4);

        if (useMask && s.Retouch > 0)
        {
            p.F0.Y = (float)s.Retouch;
            SetParams(p);
            string o = Next(); Ensure(o, n * 4, 4);
            Run("Retouch", n, srv: [(cur, 0), ("alpha", 1)], uav: [(o, 0)]);
            cur = o;
        }

        // Background mode, spotlight, behind-me and colour share one pass.
        int mode = !useMask || !s.NeedsMask ? 0 : s.Mode switch
        {
            BackgroundMode.Blur => 1,
            BackgroundMode.Replace when replacement is { } bg && bg.Length == n * 4 => 2,
            BackgroundMode.Remove => 3,
            _ => 0,
        };
        if (mode != 0)
        {
            // BackgroundEffects.MaskedBlur: the nearby room and person colours (to unmix the edge), and for Blur the
            // blurred room, all from one masked quarter-size downsample.
            int bw = Math.Max(1, w / 4), bh = Math.Max(1, h / 4), q = bw * bh;
            Ensure("acc0", q * 16, 16); Ensure("acc1", q * 16, 16);
            Ensure("room0", q * 16, 16); Ensure("room1", q * 16, 16);
            Ensure("pers0", q * 16, 16); Ensure("pers1", q * 16, 16);
            p.I1 = new(bw, bh, BackgroundEffects.NearRadius, mode);
            SetParams(p);
            Run("BlurDown", q, srv: [(cur, 0), ("alpha", 1)], uav: [("acc0", 2), ("pers0", 3)]);
            for (int pass = 0; pass < 3; pass++)
            {
                Run("BoxH", q, srv: [(pass == 0 ? "acc0" : "room0", 7)], uav: [("room1", 2)]);
                Run("BoxV", q, srv: [("room1", 7)], uav: [("room0", 2)]);
                Run("BoxH", q, srv: [("pers0", 7)], uav: [("pers1", 2)]);
                Run("BoxV", q, srv: [("pers1", 7)], uav: [("pers0", 2)]);
            }
            if (mode == 1)
            {
                p.I1 = new(bw, bh, 2 + (int)Math.Round(s.BlurStrength * 14), mode);
                SetParams(p);
                for (int pass = 0; pass < 3; pass++)
                {
                    Run("BoxH", q, srv: [("acc0", 7)], uav: [("acc1", 2)]);
                    Run("BoxV", q, srv: [("acc1", 7)], uav: [("acc0", 2)]);
                }
            }
        }
        else p.I1 = new(0, 0, 0, mode);
        if (mode == 2 && !ReferenceEquals(_replacementOnGpu, replacement))
        {
            // BackgroundLibrary caches its pictures, so the same array means the same picture: upload it once.
            Upload("replace", replacement!, 4);
            _replacementOnGpu = replacement;
        }

        Fx flags = 0;
        if (useMask && s.NeedsMask && s.Spotlight > 0) { flags |= Fx.Spot; p.F0.X = (float)s.Spotlight; }
        bool behind = useMask && r.BehindOverlay is { } bo && bo.Length == n * 4;
        if (behind) { flags |= Fx.Behind; Upload("behind", r.BehindOverlay!, 4); }
        if (!adj.IsNeutral && adj.NeedsColour) flags |= ColourParams(adj, ref p);
        p.I2.X = (int)flags;

        if (mode != 0 || flags != 0)
        {
            SetParams(p);
            string o = Next(); Ensure(o, n * 4, 4);
            Run("Effects", n,
                srv: mode == 0
                    ? [(cur, 0), ("alpha", 1), (cur, 2), (behind ? "behind" : cur, 3), ("tone", 4), ("lut", 5), ("alpha", 7)]
                    : [(cur, 0), ("alpha", 1), (mode == 2 ? "replace" : cur, 2), (behind ? "behind" : cur, 3), ("tone", 4), ("lut", 5),
                       (mode == 1 ? "acc0" : "alpha", 7), ("room0", 9), ("pers0", 10)],
                uav: [(o, 0)]);
            cur = o;
        }

        if (!adj.IsNeutral && adj.Sharpness > 0 && w >= 3 && h >= 3)
        {
            p.F0.W = (float)adj.Sharpness;
            SetParams(p);
            string o = Next(); Ensure(o, n * 4, 4);
            Run("Sharpen", n, srv: [(cur, 0)], uav: [(o, 0)]);
            cur = o;
        }

        if (r.Scene is { } scene)
        {
            // Only Remove makes the camera see-through (blur and replace leave it opaque).
            SceneParams(scene, mode == 3 || transparent, w, h, r.Overlay, r.UnderOverlay, ref p);
            SetParams(p);
            if (((Sf)p.I4.X & Sf.Overlay) != 0) Upload("aux", r.Overlay!, 4);
            bool under = ((Sf)p.I4.X & Sf.Under) != 0;
            if (under) UploadCached("under", r.UnderOverlay!);
            string o = Next(); Ensure(o, n * 4, 4);
            Run("Scene", n, srv: [(cur, 0), ("aux", 2), (under ? "under" : cur, 3)], uav: [(o, 0)]);
            cur = o;
        }

        // Other sources placed on top (a second camera), as SceneCompositor.PlaceSource.
        foreach (var src in r.Placed ?? [])
        {
            if (src.Pixels.Length != src.Width * src.Height * 4 || src.Width * src.Height == 0) continue;
            var c = src.Layer;
            int rx = (int)Math.Round(c.X * w), ry = (int)Math.Round(c.Y * h);
            int rw = Math.Max(1, (int)Math.Round(c.W * w)), rh = Math.Max(1, (int)Math.Round(c.H * h));
            double scale = Math.Max((double)rw / src.Width, (double)rh / src.Height);
            var q = new Params
            {
                I0 = new(w, h, 0, 0), I1 = new(src.Width, src.Height, 0, 0), I3 = new(rx, ry, rw, rh),
                I4 = new(c.Shape == "ellipse" ? (int)Sf.Ellipse : 0, 0, 0, 0),
                F4 = new(0, (float)Math.Clamp(c.Opacity, 0, 1), (float)(c.CornerRadius * h), 0),
                F9 = new((float)((src.Width - rw / scale) / 2), (float)((src.Height - rh / scale) / 2), (float)scale, 0),
            };
            SetParams(q);
            Upload("placed", src.Pixels, 4);
            string o = Next(); Ensure(o, n * 4, 4);
            Run("Place", n, srv: [(cur, 0), ("placed", 2)], uav: [(o, 0)]);
            cur = o;
        }

        return cur;
    }

    /// <summary>One MaskOps.GuidedRefine pass (colour guide) on the GPU, with the scale MaskOps.Matte uses.</summary>
    private void GuidedRefine(string frame, int w, int h, int radius, bool carve, ref Params p)
    {
        int n = w * h;
        int flags = p.I2.W;
        p.I2.W = carve ? 1 : 0;
        int scale = MaskOps.GuideScale(w), gw = Math.Max(1, w / scale), gh = Math.Max(1, h / scale), q = gw * gh;
        p.I3 = new(gw, gh, scale, radius);
        SetParams(p);
        string[] planes = ["guide0", "guide1", "guide2", "guide3"];
        foreach (var g in planes) Ensure(g, q * 16, 16);
        Ensure("guideT", q * 16, 16);
        Run("GuideDown", q, srv: [(frame, 0), ("alpha", 1)], uav: [("guide0", 2), ("guide1", 3), ("guide2", 4), ("guide3", 5)]);
        foreach (var g in planes) Box(g);
        Run("GuideAB", q, srv: [("guide0", 7), ("guide1", 9), ("guide2", 10), ("guide3", 11)], uav: [("guideT", 2)]);
        Run("GuideBoxH", q, srv: [("guideT", 7)], uav: [("guide0", 2)]);
        Run("GuideBoxV", q, srv: [("guide0", 7)], uav: [("guideT", 2)]);
        Run("GuideApply", n, srv: [(frame, 0), ("guideT", 7)], uav: [("alpha", 1)]);
        p.I3 = default; p.I2.W = flags;

        void Box(string g)
        {
            Run("GuideBoxH", q, srv: [(g, 7)], uav: [("guideT", 2)]);
            Run("GuideBoxV", q, srv: [("guideT", 7)], uav: [(g, 2)]);
        }
    }

    private Fx ColourParams(ImageFilters.Adjustments adj, ref Params p)
    {
        Fx f = Fx.Colour;
        var tone = ImageFilters.BuildToneCurve(adj);
        Upload("tone", tone, 4);
        if (adj.Temperature != 0) f |= Fx.Temp;
        if (adj.Tint != 0) f |= Fx.Tint;
        if (adj.Vibrance != 0) f |= Fx.Vib;
        p.F1 = new((float)adj.Temperature, (float)adj.Tint, (float)adj.Saturation, (float)adj.Vibrance);
        if (adj.Hue != 0)
        {
            f |= Fx.Hue;
            var m = ImageFilters.HueMatrix(adj.Hue);
            p.F2 = new(m[0], m[1], m[2], m[3]); p.F3 = new(m[4], m[5], m[6], m[7]); p.F4.X = m[8];
        }
        p.I2.Y = (int)adj.Filter;
        if (adj.Lut is { } lut && adj.LutStrength > 0)
        {
            f |= Fx.Lut;
            Upload("lut", lut.Data, 4);
            p.I2.Z = lut.Size;
            p.F0.Z = (float)Math.Clamp(adj.LutStrength, 0, 1);
            var (min, max) = lut.Domain;
            p.F7 = new(min[0], min[1], min[2], 0); p.F8 = new(max[0], max[1], max[2], 0);
        }
        return f;
    }

    private static void SceneParams(SceneDocument scene, bool transparent, int w, int h, byte[]? overlay, byte[]? under, ref Params p)
    {
        var top = ColorHex.Parse(scene.Background, (255, 16, 20, 24));
        var bottom = string.IsNullOrEmpty(scene.Background2) ? top : ColorHex.Parse(scene.Background2, top);
        p.F5 = new(top.B, top.G, top.R, 0); p.F6 = new(bottom.B, bottom.G, bottom.R, 0);
        var c = scene.Camera;
        Sf f = 0;
        if (SceneCompositor.CameraFillsCanvas(scene))
        {
            f |= Sf.Fills;
            if (transparent) f |= Sf.Underlay;
        }
        else
        {
            int rx = (int)Math.Round(c.X * w), ry = (int)Math.Round(c.Y * h);
            int rw = Math.Max(1, (int)Math.Round(c.W * w)), rh = Math.Max(1, (int)Math.Round(c.H * h));
            p.I3 = new(rx, ry, rw, rh);
            if (c.Visible) f |= Sf.Camera;
            if (under is not null && under.Length == w * h * 4) f |= Sf.Under;
            if (c.Shape == "ellipse") f |= Sf.Ellipse;
            p.F4.Y = (float)Math.Clamp(c.Opacity, 0, 1);
            p.F4.Z = (float)(c.CornerRadius * h);
            double scale = Math.Max((double)rw / w, (double)rh / h);
            p.F9 = new((float)((w - rw / scale) / 2), (float)((h - rh / scale) / 2), (float)scale, 0);
        }
        if (overlay is not null && overlay.Length == w * h * 4) f |= Sf.Overlay;
        p.I4 = new((int)f, 0, 0, 0);
    }

    private void SetParams(Params p)
    {
        var map = _ctx.Map(_params, MapMode.WriteDiscard);
        Marshal.StructureToPtr(p, map.DataPointer, false);
        _ctx.Unmap(_params, 0);
    }

    private void Run(string kernel, int count, (string Name, int Slot)[] srv, (string Name, int Slot)[] uav)
    {
        _ctx.CSSetShader(_cs[kernel]);
        _ctx.CSSetConstantBuffer(0, _params);
        foreach (var (name, slot) in srv) _ctx.CSSetShaderResource((uint)slot, Get(name).Srv);
        foreach (var (name, slot) in uav) _ctx.CSSetUnorderedAccessView((uint)slot, Get(name).Uav!);
        int groups = (count + 255) / 256, gx = Math.Min(groups, 65535), gy = (groups + gx - 1) / gx;
        _ctx.Dispatch((uint)gx, (uint)gy, 1);
        // Unbind so the next pass can read what this one wrote.
        foreach (var (_, slot) in srv) _ctx.CSSetShaderResource((uint)slot, null!);
        foreach (var (_, slot) in uav) _ctx.CSSetUnorderedAccessView((uint)slot, null!);
    }

    private Slot Get(string name) => _slots.TryGetValue(name, out var s) ? s : Ensure(name, 16, 4);

    /// <summary>A structured buffer of at least <paramref name="bytes"/>, reused across frames.</summary>
    private Slot Ensure(string name, int bytes, int stride)
    {
        if (_slots.TryGetValue(name, out var s) && s.Bytes >= bytes) return s;
        s?.Dispose();
        if (name == "replace") _replacementOnGpu = null;
        _onGpu.Remove(name);
        bytes = Math.Max(bytes, 16);
        var buf = _device.CreateBuffer(new BufferDescription((uint)bytes, BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, (uint)stride));
        s = new Slot(buf, _device.CreateShaderResourceView(buf), _device.CreateUnorderedAccessView(buf), bytes);
        _slots[name] = s;
        return s;
    }

    private void Upload<T>(string name, T[] data, int stride) where T : unmanaged
    {
        int bytes = data.Length * Marshal.SizeOf<T>();
        var s = Ensure(name, bytes, stride);
        unsafe
        {
            fixed (T* ptr = data)
                _ctx.UpdateSubresource(s.Buffer, 0, new Vortice.Mathematics.Box(0, 0, 0, bytes, 1, 1), (nint)ptr, (uint)bytes, 0);
        }
    }

    /// <summary>Uploads <paramref name="data"/> unless that same array is already there (callers cache their layers).</summary>
    private void UploadCached(string name, byte[] data)
    {
        if (_onGpu.TryGetValue(name, out var held) && ReferenceEquals(held, data)) return;
        Upload(name, data, 4);
        _onGpu[name] = data;
    }

    private void UploadRaw(string name, nint data, int bytes)
    {
        if (bytes % 4 != 0)
        {
            // Structured buffers take whole words: pad the tail.
            var padded = new byte[(bytes + 3) & ~3];
            Marshal.Copy(data, padded, 0, bytes);
            Upload(name, padded, 4);
            return;
        }
        var s = Ensure(name, bytes, 4);
        _ctx.UpdateSubresource(s.Buffer, 0, new Vortice.Mathematics.Box(0, 0, 0, bytes, 1, 1), data, (uint)bytes, 0);
    }

    private void Copy(string from, string to, int bytes)
    {
        var dst = Ensure(to, bytes, 4);
        _ctx.CopySubresourceRegion(dst.Buffer, 0, 0, 0, 0, Get(from).Buffer, 0, new Vortice.Mathematics.Box(0, 0, 0, bytes, 1, 1));
    }

    private readonly Dictionary<string, object> _onGpu = [];
    private byte[]? _replacementOnGpu;
    private ID3D11Buffer? _staging; private int _stagingBytes;

    private byte[] Download(string name, int bytes)
    {
        if (_staging is null || _stagingBytes < bytes)
        {
            _staging?.Dispose();
            _staging = _device.CreateBuffer(new BufferDescription((uint)bytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _stagingBytes = bytes;
        }
        _ctx.CopySubresourceRegion(_staging, 0, 0, 0, 0, Get(name).Buffer, 0, new Vortice.Mathematics.Box(0, 0, 0, bytes, 1, 1));
        var map = _ctx.Map(_staging, 0, MapMode.Read);
        try
        {
            var result = GC.AllocateUninitializedArray<byte>(bytes);
            Marshal.Copy(map.DataPointer, result, 0, bytes);
            return result;
        }
        finally { _ctx.Unmap(_staging, 0); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _slots.Values) s.Dispose();
            _slots.Clear();
            foreach (var c in _cs.Values) c.Dispose();
            _staging?.Dispose(); _params.Dispose(); _ctx.Dispose(); _device.Dispose();
        }
    }
}
