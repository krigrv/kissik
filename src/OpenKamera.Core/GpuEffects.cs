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
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, [FeatureLevel.Level_11_0],
            out _device!, out _ctx!).CheckError();
        using (var dxgi = _device.QueryInterface<Vortice.DXGI.IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
            AdapterName = adapter.Description.Description;

        using var stream = typeof(GpuEffects).Assembly.GetManifestResourceStream("OpenKamera.Core.GpuEffects.hlsl")!;
        string source = new StreamReader(stream).ReadToEnd();
        foreach (var entry in new[] { "Upsample", "Retouch", "BlurDown", "BoxH", "BoxV", "Effects", "Sharpen", "Scene" })
        {
            var code = Compiler.Compile(source, entry, "GpuEffects.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
            _cs[entry] = _device.CreateComputeShader(code.Span);
        }
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
    private enum Sf { Fills = 1, Ellipse = 2, Camera = 4, Overlay = 8, Underlay = 16 }

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
            try { return Run(px, w, h, r, mask, replacement); }
            catch (Exception) { return null; }
        }
    }

    private byte[] Run(byte[] px, int w, int h, FrameRecipe r, PersonMask? mask, byte[]? replacement)
    {
        int n = w * h;
        var s = r.Background;
        var adj = r.Adjustments;
        var p = new Params { I0 = new(w, h, mask?.Width ?? 0, mask?.Height ?? 0) };

        Upload("frame", px, 4);
        string cur = "frame";
        string Next() => cur == "a" ? "b" : "a";

        bool useMask = mask is not null && (s.NeedsMask || r.BehindOverlay is not null);
        if (useMask)
        {
            Upload("maskLow", mask!.Alpha, 4);
            Ensure("alpha", n * 4, 4);
            SetParams(p);
            Run("Upsample", n, srv: [("maskLow", 6)], uav: [("alpha", 1)]);
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
            BackgroundMode.Replace when replacement is { } bg && bg.Length == px.Length => 2,
            BackgroundMode.Remove => 3,
            _ => 0,
        };
        if (mode == 1)
        {
            int bw = Math.Max(1, w / 4), bh = Math.Max(1, h / 4), q = bw * bh;
            int radius = 2 + (int)Math.Round(s.BlurStrength * 14);
            p.I1 = new(bw, bh, radius, 1);
            SetParams(p);
            Ensure("acc0", q * 16, 16); Ensure("acc1", q * 16, 16);
            Run("BlurDown", q, srv: [(cur, 0), ("alpha", 1)], uav: [("acc0", 2)]);
            for (int pass = 0; pass < 3; pass++)
            {
                Run("BoxH", q, srv: [("acc0", 7)], uav: [("acc1", 2)]);
                Run("BoxV", q, srv: [("acc1", 7)], uav: [("acc0", 2)]);
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
        bool behind = useMask && r.BehindOverlay is { } bo && bo.Length == px.Length;
        if (behind) { flags |= Fx.Behind; Upload("behind", r.BehindOverlay!, 4); }
        if (!adj.IsNeutral && adj.NeedsColour) flags |= ColourParams(adj, ref p);
        p.I2.X = (int)flags;

        if (mode != 0 || flags != 0)
        {
            SetParams(p);
            string o = Next(); Ensure(o, n * 4, 4);
            Run("Effects", n,
                srv: [(cur, 0), ("alpha", 1), (mode == 2 ? "replace" : cur, 2), (behind ? "behind" : cur, 3), ("tone", 4), ("lut", 5), (mode == 1 ? "acc0" : "alpha", 7)],
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
            SceneParams(scene, mode == 3 || SceneCompositor.HasTransparency(px), w, h, r.Overlay, ref p);
            SetParams(p);
            if (((Sf)p.I4.X & Sf.Overlay) != 0) Upload("aux", r.Overlay!, 4);
            string o = Next(); Ensure(o, n * 4, 4);
            Run("Scene", n, srv: [(cur, 0), ("aux", 2)], uav: [(o, 0)]);
            cur = o;
        }

        return Download(cur, n * 4);
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

    private static void SceneParams(SceneDocument scene, bool transparent, int w, int h, byte[]? overlay, ref Params p)
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
