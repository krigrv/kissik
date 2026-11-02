using System.Text;

namespace OpenKamera.Core.Tests;

public class PhoneStatusProTests
{
    private static PhoneStatus Parse(string json) => PhoneServer.TryParseStatus(Encoding.UTF8.GetBytes(json))!;

    [Fact]
    public void ReadsEveryProControl()
    {
        var st = Parse("""
        {"device":"Pixel","w":1080,"h":1920,"rot":90,"facing":"back","zoom":1,"zoomMin":0.6,"zoomMax":10,
         "lenses":[{"id":"2","facing":"back","label":"Ultra-wide"},{"id":"0","facing":"back","label":"Main"},{"id":"1","facing":"front","label":"Front"}],
         "lens":"0","ev":-2,"evMin":-12,"evMax":12,"evStep":0.1666,
         "manual":true,"iso":400,"isoMin":50,"isoMax":3200,"shutter":8000000,"shutterMin":100000,"shutterMax":250000000,
         "focusManual":true,"focus":0.25,"wbModes":["auto","daylight","cloudy"],"wb":"daylight",
         "fpsList":[30,15,60,30],"fps":0,"stab":true,"hdr":false,"codecs":["jpeg","h264"],"codec":"h264"}
        """);
        Assert.Equal(90, st.Rotation);
        Assert.Equal(0.6, st.ZoomMin);
        Assert.Equal(["Ultra-wide", "Main", "Front"], st.Lenses.Select(l => l.Label));
        Assert.Equal("front", st.Lenses[2].Facing);
        Assert.Equal("0", st.Lens);
        Assert.Equal(-2, st.Ev); Assert.Equal(-12, st.EvMin); Assert.Equal(12, st.EvMax); Assert.True(st.HasEv);
        Assert.True(st.Manual); Assert.True(st.HasIso); Assert.True(st.HasShutter);
        Assert.Equal(400, st.Iso); Assert.Equal(8_000_000, st.Shutter); Assert.Equal(250_000_000, st.ShutterMax);
        Assert.True(st.FocusManual); Assert.Equal(0.25, st.Focus);
        Assert.Equal(["auto", "daylight", "cloudy"], st.WbModes); Assert.Equal("daylight", st.Wb);
        Assert.Equal([15, 30, 60], st.FpsList); Assert.Equal(0, st.Fps);
        Assert.True(st.Stab); Assert.False(st.Hdr);
        Assert.True(st.CanH264); Assert.Equal("h264", st.Codec);
    }

    [Fact]
    public void MissingOrNullControlsAreHidden()
    {
        var st = Parse("""{"w":1280,"h":720,"facing":"front","iso":null,"focus":null,"stab":null,"hdr":null,"lens":null,"evMin":0,"evMax":0}""");
        Assert.Empty(st.Lenses); Assert.Null(st.Lens);
        Assert.False(st.HasEv); Assert.False(st.HasIso); Assert.False(st.HasShutter); Assert.False(st.FocusManual);
        Assert.Null(st.Iso); Assert.Null(st.Focus); Assert.Null(st.Stab); Assert.Null(st.Hdr);
        Assert.Empty(st.WbModes); Assert.Empty(st.FpsList); Assert.False(st.CanH264);
        Assert.Equal(0, st.Rotation);
    }

    [Fact]
    public void AutoValuesAndOddInputsAreSafe()
    {
        var st = Parse("""{"w":1,"h":1,"manual":true,"isoMin":100,"isoMax":100,"rot":-90,"focus":3,"lenses":[{"label":"no id"},{"id":5}],"lens":5}""");
        Assert.False(st.HasIso);         // no range to pick from
        Assert.Equal(270, st.Rotation);  // normalised
        Assert.Equal(1, st.Focus);       // clamped
        Assert.Single(st.Lenses); Assert.Equal("5", st.Lenses[0].Id); Assert.Equal("5", st.Lens);
        Assert.Equal(0, Parse("""{"rot":45}""").Rotation);
    }
}

public class PhoneVideoTests
{
    [Fact]
    public void ParsesVideoPacketHeader()
    {
        var data = new byte[14]; data[0] = (byte)'V'; data[1] = 1; BitConverter.GetBytes(123456789UL).CopyTo(data, 2);
        data[10] = 0; data[11] = 0; data[12] = 1; data[13] = 0x65;
        var p = PhoneVideoPacket.TryParse(data)!.Value;
        Assert.True(p.Keyframe); Assert.Equal(123456789, p.TimestampUs); Assert.Equal(4, p.AnnexB.Length);
        Assert.Null(PhoneVideoPacket.TryParse([0xFF, 0xD8, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    // A 2×2 NV12 picture with distinct luma per pixel and neutral chroma: grey levels show where each pixel landed.
    private static byte[] Grey2x2(int pitch) { var b = new byte[pitch * 3]; b[0] = 16; b[1] = 100; b[pitch] = 180; b[pitch + 1] = 235; b[pitch * 2] = b[pitch * 2 + 1] = 128; return b; }

    [Theory]
    [InlineData(0, new byte[] { 0, 98, 191, 255 })]
    [InlineData(90, new byte[] { 191, 0, 255, 98 })]
    [InlineData(180, new byte[] { 255, 191, 98, 0 })]
    [InlineData(270, new byte[] { 98, 255, 0, 191 })]
    public void Nv12TurnsClockwise(int rotation, byte[] expected)
    {
        var bgra = PhoneVideo.Nv12ToBgra(Grey2x2(4), 4, 2, 2, 8, rotation, out int w, out int h);
        Assert.Equal((2, 2), (w, h));
        Assert.Equal(expected, Enumerable.Range(0, 4).Select(i => bgra[i * 4]).ToArray());
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(255, bgra[i * 4 + 3]));
    }

    [Fact]
    public void Nv12SwapsSizeForQuarterTurns()
    {
        var bgra = PhoneVideo.Nv12ToBgra(new byte[4 * 2 * 3 / 2 * 2], 4, 4, 2, 8, 90, out int w, out int h);
        Assert.Equal((2, 4), (w, h)); Assert.Equal(2 * 4 * 4, bgra.Length);
    }

    [Fact]
    public void SplitsAnnexBIntoPictures()
    {
        var aus = PhoneVideo.SplitAccessUnits(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "test-320x240.h264"))).ToList();
        Assert.Equal(30, aus.Count);
        Assert.True(aus[0].Keyframe); Assert.True(aus[15].Keyframe); Assert.False(aus[1].Keyframe);
    }

    [Fact]
    public void MediaFoundationDecodesAndTurnsThePicture()
    {
        H264Decoder dec;
        try { dec = H264Decoder.Create(); }
        catch (InvalidOperationException) { return; } // Windows without the media features: nothing to test here
        using var _ = dec;
        dec.Rotation = 90;
        var frames = new List<(int W, int H, double Green)>(); string? error = null;
        using var done = new ManualResetEventSlim();
        dec.FrameDecoded += f =>
        {
            double green = f.Bgra.Take(f.Width * f.Height * 4).Where((_, i) => i % 4 == 1).Average(b => b);
            lock (frames) { frames.Add((f.Width, f.Height, green)); if (frames.Count == 30) done.Set(); }
        };
        dec.Error += e => error = e;
        long t = 0;
        foreach (var (data, key) in PhoneVideo.SplitAccessUnits(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "test-320x240.h264"))))
            Assert.True(dec.Push(new PhoneVideoPacket(key, t += 33_333, data)));
        done.Wait(TimeSpan.FromSeconds(10));
        Assert.Null(error);
        Assert.Equal(30, frames.Count);
        Assert.All(frames, f => Assert.Equal((240, 320), (f.W, f.H)));
        Assert.True(frames[0].Green > 40); // testsrc is a bright picture, not all black
    }

    [Fact]
    public void RotationCanChangeMidStreamWithoutARestart()
    {
        H264Decoder dec;
        try { dec = H264Decoder.Create(); }
        catch (InvalidOperationException) { return; }
        using var _ = dec;
        var sizes = new List<(int, int)>(); string? error = null;
        dec.FrameDecoded += f => { lock (sizes) sizes.Add((f.Width, f.Height)); };
        dec.Error += e => error = e;
        var aus = PhoneVideo.SplitAccessUnits(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "test-320x240.h264"))).ToList();
        long t = 0; int[] turns = [0, 90, 180, 270];
        for (int i = 0; i < aus.Count; i++)
        {
            if (i % 8 == 0)
            {
                // The phone was turned: wait for what's queued so the change lands between known pictures.
                SpinWait.SpinUntil(() => { lock (sizes) return sizes.Count == i; }, TimeSpan.FromSeconds(5));
                dec.Rotation = turns[i / 8 % 4];
            }
            dec.Push(new PhoneVideoPacket(aus[i].Keyframe, t += 33_333, aus[i].Data)); // P-frames too: no keyframe needed
        }
        SpinWait.SpinUntil(() => { lock (sizes) return sizes.Count == aus.Count; }, TimeSpan.FromSeconds(10));
        Assert.Null(error);
        Assert.Equal(aus.Count, sizes.Count);
        for (int i = 0; i < sizes.Count; i++)
            Assert.Equal(turns[i / 8 % 4] is 90 or 270 ? (240, 320) : (320, 240), sizes[i]);
    }

    [Fact]
    public void DecoderSkipsToTheFirstKeyframe()
    {
        H264Decoder dec;
        try { dec = H264Decoder.Create(allowHardware: false); }
        catch (InvalidOperationException) { return; }
        using var _ = dec;
        int count = 0; string? error = null;
        dec.FrameDecoded += _ => Interlocked.Increment(ref count);
        dec.Error += e => error = e;
        var aus = PhoneVideo.SplitAccessUnits(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "test-320x240.h264"))).ToList();
        long t = 0;
        foreach (var (data, key) in aus.Skip(3)) dec.Push(new PhoneVideoPacket(key, t += 33_333, data)); // joins mid-stream
        SpinWait.SpinUntil(() => Volatile.Read(ref count) >= 15, TimeSpan.FromSeconds(10));
        Assert.Null(error);
        Assert.Equal(15, count); // from the keyframe at picture 15 on
    }
}

public class TapToFocusTests
{
    [Theory]
    [InlineData(false, 0, 0.2, 0.3, 0.2, 0.3)]
    [InlineData(true, 0, 0.2, 0.3, 0.8, 0.3)]
    [InlineData(false, 90, 0.2, 0.3, 0.3, 0.8)]
    [InlineData(false, 180, 0.2, 0.3, 0.8, 0.7)]
    [InlineData(true, 270, 0.2, 0.3, 0.3, 0.2)]
    public void MapsTheShownPointBackToTheCamera(bool mirror, int rot, double x, double y, double u, double v)
    {
        var (su, sv) = new FrameTransform(mirror, rot).ToSource(x, y);
        Assert.Equal(u, su, 6); Assert.Equal(v, sv, 6);
    }

    [Fact]
    public void AccountsForZoom()
    {
        var (u, v) = new FrameTransform(Zoom: 2).ToSource(0, 1);
        Assert.Equal(0.25, u, 6); Assert.Equal(0.75, v, 6);
    }
}
