using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OpenKamera.Core;

/// <summary>
/// A decoded picture: upright BGRA (already turned by the phone's rotation). <see cref="Bgra"/> is reused for the next
/// picture once the event returns (it may be longer than Width × Height × 4): copy what you keep.
/// </summary>
public sealed record DecodedFrame(byte[] Bgra, int Width, int Height, long TimestampUs);

/// <summary>
/// H.264 to BGRA with the Windows Media Foundation H.264 decoder (built into Windows, hardware-accelerated through
/// D3D11 when the GPU offers it, otherwise the same decoder in software). Packets go in on any thread; decoding runs
/// on its own thread so the network loop never waits. A broken stream raises <see cref="Error"/>: ask for a keyframe.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    public event Action<DecodedFrame>? FrameDecoded;
    /// <summary>Decoding hit a problem (missing reference, corrupt packet). Frames resume at the next keyframe.</summary>
    public event Action<string>? Error;

    /// <summary>True when the GPU decodes (D3D11 video acceleration); false for the software path.</summary>
    public bool Hardware { get; private set; }
    public long FramesDecoded { get; private set; }

    /// <summary>Clockwise degrees to turn decoded pictures; set from the phone status.</summary>
    public int Rotation { get; set; }

    private nint _mft, _d3dDevice, _dxgiManager;
    private int _width, _height, _pitchHint, _cropW, _cropH;
    private bool _providesSamples;
    private int _outputBufferSize;
    private bool _waitKey = true;
    private int _skipped;
    /// <summary>Decoded pictures not shown because a newer one was already waiting (catch-up after a slow moment).</summary>
    public int Skipped => Volatile.Read(ref _skipped);
    /// <summary>Live mode: show only the newest picture when packets back up (off for file playback and tests).</summary>
    public bool DropStaleFrames { get; set; }
    private readonly BlockingCollection<PhoneVideoPacket> _queue = new(new ConcurrentQueue<PhoneVideoPacket>(), 120);
    private Thread? _thread;
    private volatile bool _disposed;

    private H264Decoder() { }

    /// <summary>Creates and opens the decoder; throws when Media Foundation or its H.264 decoder isn't there.</summary>
    public static H264Decoder Create(bool allowHardware = true)
    {
        var d = new H264Decoder();
        try { d.Open(allowHardware); }
        catch { d.Release(); throw; }
        d._thread = new Thread(d.Run) { IsBackground = true, Name = "H.264 decoder" };
        d._thread.Start();
        return d;
    }

    /// <summary>Queues a packet. Returns false (and drops everything) when decoding fell far behind: ask for a keyframe.</summary>
    public bool Push(PhoneVideoPacket packet)
    {
        if (_disposed) return false;
        if (_queue.TryAdd(packet)) return true;
        while (_queue.TryTake(out _)) { }
        _waitKey = true;
        return false;
    }

    private void Run()
    {
        try
        {
            foreach (var p in _queue.GetConsumingEnumerable())
            {
                if (_disposed) break;
                if (_waitKey && !p.Keyframe) continue; // nothing to decode against until a keyframe
                _waitKey = false;
                try { Decode(p); }
                catch (Exception ex)
                {
                    _waitKey = true;
                    try { Call(Vt(_mft, 23), _mft, MFT_MESSAGE_COMMAND_FLUSH, (nuint)0); } catch { }
                    Error?.Invoke(ex.Message);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // ───── Media Foundation setup ─────

    private void Open(bool allowHardware)
    {
        Check(MFStartup(MF_VERSION, 0), "MFStartup");
        _started = true;
        var clsid = CLSID_MSH264DecoderMFT; var iid = IID_IMFTransform;
        Check(CoCreateInstance(ref clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out _mft), "H.264 decoder");

        // Low latency: output each frame as soon as it's decodable instead of holding a reorder queue.
        if (Call(Vt(_mft, 8), _mft, out nint attrs) >= 0 && attrs != 0)
        {
            var low = MF_LOW_LATENCY;
            Call(Vt(attrs, 21), attrs, &low, 1u);
            Marshal.Release(attrs);
        }
        if (allowHardware) Hardware = TrySetD3D();

        nint inType = NewType(MFMediaType_Video, MFVideoFormat_H264);
        try { Check(Call(Vt(_mft, 15), _mft, 0u, inType, 0u), "SetInputType"); }
        finally { Marshal.Release(inType); }
        PickOutputType();
    }

    private bool _started;

    private bool TrySetD3D()
    {
        try
        {
            // BGRA support | video support.
            if (D3D11CreateDevice(0, 1, 0, 0x20 | 0x800, 0, 0, 7, out _d3dDevice, out _, out nint ctx) < 0) return false;
            if (ctx != 0) Marshal.Release(ctx);
            var iidMt = IID_ID3D10Multithread;
            if (Marshal.QueryInterface(_d3dDevice, in iidMt, out nint mt) >= 0) { Call(Vt(mt, 5), mt, 1); Marshal.Release(mt); }
            if (MFCreateDXGIDeviceManager(out uint token, out _dxgiManager) < 0) return false;
            if (Call(Vt(_dxgiManager, 7), _dxgiManager, _d3dDevice, token) < 0) return false;
            if (Call(Vt(_mft, 23), _mft, MFT_MESSAGE_SET_D3D_MANAGER, (nuint)_dxgiManager) < 0)
            {
                Marshal.Release(_dxgiManager); _dxgiManager = 0;
                return false;
            }
            return true;
        }
        catch { return false; }
    }

    private void PickOutputType()
    {
        for (uint i = 0; ; i++)
        {
            if (Call(Vt(_mft, 14), _mft, 0u, i, out nint t) < 0) throw new InvalidOperationException("The H.264 decoder offers no NV12 output.");
            try
            {
                Guid sub; Guid kSub = MFMT_SUBTYPE, kSize = MFMT_FRAME_SIZE, kAperture = MFMT_MINIMUM_DISPLAY_APERTURE, kStride = MFMT_DEFAULT_STRIDE;
                if (Call(Vt(t, 10), t, &kSub, &sub) >= 0 && sub == MFVideoFormat_NV12)
                {
                    Check(Call(Vt(_mft, 16), _mft, 0u, t, 0u), "SetOutputType");
                    ulong size;
                    if (Call(Vt(t, 8), t, &kSize, &size) >= 0) { _width = (int)(size >> 32); _height = (int)(size & 0xFFFFFFFF); }
                    _cropW = _width; _cropH = _height;
                    // The coded size is padded to 16 lines; the aperture says how much of it is picture.
                    MFVideoArea area; uint got;
                    if (Call(Vt(t, 15), t, &kAperture, &area, (uint)sizeof(MFVideoArea), &got) >= 0 && area.Width > 0 && area.Height > 0)
                    { _cropW = area.Width; _cropH = area.Height; }
                    uint stride;
                    _pitchHint = Call(Vt(t, 7), t, &kStride, &stride) >= 0 ? (int)stride : _width;
                    MFT_OUTPUT_STREAM_INFO info;
                    if (Call(Vt(_mft, 7), _mft, 0u, &info) >= 0)
                    {
                        _providesSamples = (info.dwFlags & (0x100 | 0x200)) != 0;
                        _outputBufferSize = (int)info.cbSize;
                    }
                    return;
                }
            }
            finally { Marshal.Release(t); }
        }
    }

    // ───── Decoding ─────

    private void Decode(PhoneVideoPacket p)
    {
        nint sample = 0, buffer = 0;
        try
        {
            int len = p.AnnexB.Length;
            Check(MFCreateMemoryBuffer((uint)len, out buffer), "MFCreateMemoryBuffer");
            byte* dst; uint max, cur;
            Check(Call(Vt(buffer, 3), buffer, &dst, &max, &cur), "Lock");
            p.AnnexB.Span.CopyTo(new Span<byte>(dst, len));
            Call(Vt(buffer, 4), buffer);
            Call(Vt(buffer, 6), buffer, (uint)len);
            Check(MFCreateSample(out sample), "MFCreateSample");
            Call(Vt(sample, 42), sample, buffer);
            Call(Vt(sample, 36), sample, p.TimestampUs * 10);
            if (p.Keyframe) { var clean = MFSampleExtension_CleanPoint; Call(Vt(sample, 21), sample, &clean, 1u); }

            int hr = Call(Vt(_mft, 24), _mft, 0u, sample, 0u);
            if (hr == MF_E_NOTACCEPTING) { DrainOutput(p.TimestampUs); hr = Call(Vt(_mft, 24), _mft, 0u, sample, 0u); }
            Check(hr, "ProcessInput");
            DrainOutput(p.TimestampUs);
        }
        finally
        {
            if (sample != 0) Marshal.Release(sample);
            if (buffer != 0) Marshal.Release(buffer);
        }
    }

    private void DrainOutput(long fallbackPts)
    {
        while (true)
        {
            nint own = 0;
            if (!_providesSamples)
            {
                Check(MFCreateSample(out own), "MFCreateSample");
                Check(MFCreateMemoryBuffer((uint)Math.Max(_outputBufferSize, _width * _height * 3 / 2), out nint ob), "MFCreateMemoryBuffer");
                Call(Vt(own, 42), own, ob); Marshal.Release(ob);
            }
            var data = new MFT_OUTPUT_DATA_BUFFER { pSample = own };
            uint status;
            int hr = Call(Vt(_mft, 25), _mft, 0u, 1u, &data, &status);
            if (data.pEvents != 0) Marshal.Release(data.pEvents);
            try
            {
                if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                if (hr == MF_E_TRANSFORM_STREAM_CHANGE) { PickOutputType(); continue; }
                Check(hr, "ProcessOutput");
                // Live video must never queue: when newer packets are already waiting, this picture is stale. It was
                // decoded (later frames depend on it) but skip converting and showing it, so delay stays at a frame or two.
                if (data.pSample != 0)
                {
                    if (!DropStaleFrames || _queue.Count == 0) Deliver(data.pSample, fallbackPts);
                    else Interlocked.Increment(ref _skipped);
                }
            }
            finally
            {
                if (data.pSample != 0 && data.pSample != own) Marshal.Release(data.pSample);
                if (own != 0) Marshal.Release(own);
            }
        }
    }

    private void Deliver(nint sample, long fallbackPts)
    {
        long t100;
        long pts = Call(Vt(sample, 35), sample, &t100) >= 0 ? t100 / 10 : fallbackPts;
        Check(Call(Vt(sample, 40), sample, 0u, out nint buf), "GetBufferByIndex");
        try
        {
            byte[] bgra = Frame(_cropW * _cropH * 4); int w, h;
            var iid2d = IID_IMF2DBuffer;
            if (Marshal.QueryInterface(buf, in iid2d, out nint b2d) >= 0)
            {
                try
                {
                    byte* scan0; int pitch;
                    Check(Call(Vt(b2d, 3), b2d, &scan0, &pitch), "Lock2D");
                    try
                    {
                        // Top-down only (negative pitch = bottom-up, which NV12 never is).
                        int p = Math.Abs(pitch);
                        long total = (long)p * _height * 3 / 2;
                        PhoneVideo.Nv12ToBgra(new ReadOnlySpan<byte>(scan0, (int)total), p, _cropW, _cropH, p * _height, Rotation, bgra, out w, out h);
                    }
                    finally { Call(Vt(b2d, 4), b2d); }
                }
                finally { Marshal.Release(b2d); }
            }
            else
            {
                byte* data; uint max, cur;
                Check(Call(Vt(buf, 3), buf, &data, &max, &cur), "Lock");
                try
                {
                    int p = Math.Max(_pitchHint, _width);
                    int rows = (int)Math.Min(_height, cur * 2 / 3 / Math.Max(1, p));
                    PhoneVideo.Nv12ToBgra(new ReadOnlySpan<byte>(data, (int)cur), p, _cropW, Math.Min(_cropH, rows), p * rows, Rotation, bgra, out w, out h);
                }
                finally { Call(Vt(buf, 4), buf); }
            }
            FramesDecoded++;
            FrameDecoded?.Invoke(new DecodedFrame(bgra, w, h, pts));
        }
        finally { Marshal.Release(buf); }
    }

    // One picture buffer, reused: a fresh 8 MB array per 4K-ish frame would keep the garbage collector busy.
    private byte[]? _frame;
    private byte[] Frame(int size) => _frame is { } f && f.Length >= size ? f : _frame = new byte[size];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _thread?.Join(2000);
        Release();
        _queue.Dispose();
    }

    private void Release()
    {
        if (_mft != 0) { Marshal.Release(_mft); _mft = 0; }
        if (_dxgiManager != 0) { Marshal.Release(_dxgiManager); _dxgiManager = 0; }
        if (_d3dDevice != 0) { Marshal.Release(_d3dDevice); _d3dDevice = 0; }
        if (_started) { MFShutdown(); _started = false; }
    }

    // ───── COM plumbing: calls through vtable slots, so no interface definitions are needed ─────

    private static nint Vt(nint obj, int slot) => (*(nint**)obj)[slot];
    private static int Call(nint f, nint self) => ((delegate* unmanaged[Stdcall]<nint, int>)f)(self);
    private static int Call(nint f, nint self, int a) => ((delegate* unmanaged[Stdcall]<nint, int, int>)f)(self, a);
    private static int Call(nint f, nint self, uint a) => ((delegate* unmanaged[Stdcall]<nint, uint, int>)f)(self, a);
    private static int Call(nint f, nint self, long a) => ((delegate* unmanaged[Stdcall]<nint, long, int>)f)(self, a);
    private static int Call(nint f, nint self, nint a) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)f)(self, a);
    private static int Call(nint f, nint self, out nint a) { nint r; int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)f)(self, &r); a = r; return hr; }
    private static int Call(nint f, nint self, uint a, nuint b) => ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)f)(self, a, b);
    private static int Call(nint f, nint self, nint a, uint b) => ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)f)(self, a, b);
    private static int Call(nint f, nint self, Guid* a, uint b) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)f)(self, a, b);
    private static int Call(nint f, nint self, Guid* a, Guid* b) => ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)f)(self, a, b);
    private static int Call(nint f, nint self, Guid* a, ulong* b) => ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)f)(self, a, b);
    private static int Call(nint f, nint self, Guid* a, uint* b) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)f)(self, a, b);
    private static int Call(nint f, nint self, Guid* a, MFVideoArea* b, uint c, uint* d) => ((delegate* unmanaged[Stdcall]<nint, Guid*, MFVideoArea*, uint, uint*, int>)f)(self, a, b, c, d);
    private static int Call(nint f, nint self, long* a) => ((delegate* unmanaged[Stdcall]<nint, long*, int>)f)(self, a);
    private static int Call(nint f, nint self, uint a, out nint b) { nint r; int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)f)(self, a, &r); b = r; return hr; }
    private static int Call(nint f, nint self, uint a, uint b, out nint c) { nint r; int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)f)(self, a, b, &r); c = r; return hr; }
    private static int Call(nint f, nint self, uint a, nint b, uint c) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)f)(self, a, b, c);
    private static int Call(nint f, nint self, uint a, MFT_OUTPUT_STREAM_INFO* b) => ((delegate* unmanaged[Stdcall]<nint, uint, MFT_OUTPUT_STREAM_INFO*, int>)f)(self, a, b);
    private static int Call(nint f, nint self, uint a, uint b, MFT_OUTPUT_DATA_BUFFER* c, uint* d) => ((delegate* unmanaged[Stdcall]<nint, uint, uint, MFT_OUTPUT_DATA_BUFFER*, uint*, int>)f)(self, a, b, c, d);
    private static int Call(nint f, nint self, byte** a, uint* b, uint* c) => ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)f)(self, a, b, c);
    private static int Call(nint f, nint self, byte** a, int* b) => ((delegate* unmanaged[Stdcall]<nint, byte**, int*, int>)f)(self, a, b);

    private static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})."); }

    private static nint NewType(Guid major, Guid sub)
    {
        Check(MFCreateMediaType(out nint t), "MFCreateMediaType");
        Guid kMajor = MFMT_MAJOR_TYPE, kSub = MFMT_SUBTYPE;
        Call(Vt(t, 24), t, &kMajor, &major);
        Call(Vt(t, 24), t, &kSub, &sub);
        return t;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MFT_OUTPUT_DATA_BUFFER { public uint dwStreamID; public nint pSample; public uint dwStatus; public nint pEvents; }
    [StructLayout(LayoutKind.Sequential)] private struct MFT_OUTPUT_STREAM_INFO { public uint dwFlags, cbSize, cbAlignment; }
    [StructLayout(LayoutKind.Sequential)] private struct MFVideoArea { public short OffsetXFract, OffsetX, OffsetYFract, OffsetY; public int Width, Height; }

    private const uint MF_VERSION = 0x00020070;
    private const uint MFT_MESSAGE_COMMAND_FLUSH = 0, MFT_MESSAGE_SET_D3D_MANAGER = 2;
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    private static readonly Guid CLSID_MSH264DecoderMFT = new("62CE7E72-4C71-4d20-B15D-452831A87D9D");
    private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IID_IMF2DBuffer = new("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB");
    private static readonly Guid IID_ID3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFMT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MFMT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MFMT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MFMT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MFMT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint maxLength, out nint buffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateDXGIDeviceManager(out uint resetToken, out nint manager);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint obj);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint levels, uint sdkVersion, out nint device, out int level, out nint context);
}
