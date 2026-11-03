namespace OpenKamera.Core;

/// <summary>Where the light sits on the screen.</summary>
public enum LightShape { SideBars, TopBar, Frame, Ring }

/// <summary>How a ring light is drawn.</summary>
public enum RingType
{
    /// <summary>A solid band.</summary>
    Classic,
    /// <summary>A band with a feathered glow on both edges.</summary>
    Soft,
    /// <summary>A glowing disc that fades out from the centre (no hole).</summary>
    Halo,
    /// <summary>Two thin concentric bands.</summary>
    Double,
    /// <summary>A ring of separate LED dots.</summary>
    Led,
}

/// <summary>Which side the light falls from. Each maps to an arc start and sweep.</summary>
public enum LightDirection { AllAround, Left, Right, Top, Bottom, KeyLeft, KeyRight, Custom }

/// <summary>A ready-made light look.</summary>
public sealed record LightLook(string Id, string Name, string Description, int Kelvin, string? Tint = null, double Brightness = 1.0);

public sealed class ScreenLightSettings
{
    public bool On { get; set; }
    public string LookId { get; set; } = "daylight";
    /// <summary>0..1 share of the screen the light covers (thickness of bars/frame, width of the ring).</summary>
    public double Size { get; set; } = 0.25;
    public int Kelvin { get; set; } = 5600;
    /// <summary>Optional colour that overrides the temperature ("#RRGGBB"); null = use Kelvin.</summary>
    public string? Tint { get; set; }
    public LightShape Shape { get; set; } = LightShape.Ring; // a ring light around the face by default
    /// <summary>0.2..1 light intensity (1 = full white for the chosen colour).</summary>
    public double Brightness { get; set; } = 1.0;
    /// <summary>Monitor backlight 0..100 to apply while the light is on; null = leave it alone.</summary>
    public int? ScreenBrightness { get; set; }
    public bool AutoTune { get; set; }
    /// <summary>False (default): the light is drawn inside OpenKamera's window. True: over the whole display, on top of other apps.</summary>
    public bool WholeScreen { get; set; }
    /// <summary>Display ids (DisplayArea ids as strings) to light; empty = the display OpenKamera is on.</summary>
    public List<string> Displays { get; set; } = [];

    // ── Ring controls ──
    public RingType RingType { get; set; } = RingType.Soft;
    public LightDirection Direction { get; set; } = LightDirection.AllAround;
    /// <summary>Ring diameter as a share of the shorter screen side (0.2..1.4; above 1 the ring runs off the edges).</summary>
    public double Diameter { get; set; } = 0.8;
    /// <summary>Ring centre as a share of width/height (0..1).</summary>
    public double CentreX { get; set; } = 0.5;
    public double CentreY { get; set; } = 0.5;
    /// <summary>0..1 how much the edges are feathered (Soft and Halo).</summary>
    public double Softness { get; set; } = 0.5;
    /// <summary>Arc start in degrees clockwise from 12 o'clock, and how far it goes round (10..360).</summary>
    public double ArcStart { get; set; }
    public double ArcSweep { get; set; } = 360;
}

/// <summary>Colour, geometry and auto-tune maths for the screen light. No UI here.</summary>
public static class ScreenLight
{
    public static IReadOnlyList<LightLook> Looks { get; } =
    [
        new("max-white", "Max white", "The brightest light your screen can make: pure white at full power. Use it in a dark room or when your face looks dull.", 6500),
        new("daylight", "Daylight", "Clean, neutral light like an overcast window. Keeps skin tones true and works for almost any call.", 5600),
        new("golden-hour", "Golden hour", "A soft amber glow like late-afternoon sun. Warms skin and makes a room feel cosy and relaxed.", 3200),
        new("soft-white", "Soft white", "Gentle indoor white, a little warmer than daylight. Flattering and easy on the eyes for long sessions.", 4300),
        new("cool-studio", "Cool studio", "Crisp, slightly blue light like a broadcast set. Makes faces look sharp and alert.", 7500),
        new("candle", "Candlelight", "Very warm and low, for evening streams with a calm mood.", 2200, Brightness: 0.75),
        new("neon-magenta", "Neon magenta", "A bold pink-purple wash for gaming and music streams.", 6500, Tint: "#FF3DD6"),
        new("teal-glow", "Teal glow", "A cool cyan accent light that pairs well with warm room lamps.", 6500, Tint: "#34E0D8"),
    ];

    /// <summary>
    /// Approximate RGB of a black-body light at <paramref name="kelvin"/> (1000–40000 K), the classic
    /// curve-fit used by photo apps. Returns sRGB 0..255.
    /// </summary>
    public static (byte R, byte G, byte B) KelvinToRgb(int kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 40000) / 100.0;
        double r, g, b;
        if (t <= 66) r = 255;
        else r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        if (t <= 66) g = 99.4708025861 * Math.Log(t) - 161.1195681661;
        else g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        if (t >= 66) b = 255;
        else if (t <= 19) b = 0;
        else b = 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        return (Clamp(r), Clamp(g), Clamp(b));
    }

    /// <summary>The colour actually shown: tint or temperature, scaled by brightness.</summary>
    public static (byte R, byte G, byte B) LightColor(ScreenLightSettings s)
    {
        var (r, g, b) = s.Tint is { } hex && ColorHex.Parse(hex) is var c && c.A > 0 ? (c.R, c.G, c.B) : KelvinToRgb(s.Kelvin);
        double k = Math.Clamp(s.Brightness, 0.05, 1);
        return (Clamp(r * k), Clamp(g * k), Clamp(b * k));
    }

    /// <summary>
    /// Brightest setup the screen can give: pure white at full power, a large thick ring that still leaves the
    /// face in the middle, full-strength classic band. Monitor backlight is handled by the app.
    /// </summary>
    public static void MaxBrightness(ScreenLightSettings s)
    {
        ApplyLook(s, Looks.First(l => l.Id == "max-white"));
        s.Brightness = 1.0;
        s.Shape = LightShape.Ring;
        s.RingType = RingType.Classic;
        s.Direction = LightDirection.AllAround;
        s.Diameter = 1.3;   // bigger than the screen height: the ring runs off top and bottom, lighting more area
        s.Size = 0.6;       // thick band
        s.CentreX = s.CentreY = 0.5;
        s.ScreenBrightness = 100;
    }

    public static void ApplyLook(ScreenLightSettings s, LightLook look)
    {
        s.LookId = look.Id; s.Kelvin = look.Kelvin; s.Tint = look.Tint; s.Brightness = look.Brightness;
    }

    /// <summary>A lit area in screen pixels: a rectangle, or an ellipse (Ring is an outer ellipse minus an inner one).</summary>
    public readonly record struct LightArea(int X, int Y, int W, int H, bool Ellipse = false, bool Subtract = false);

    /// <summary>
    /// The lit areas for a shape on a w×h screen. <paramref name="size"/> (0..1) is how much of the screen the
    /// light takes: bar thickness as a share of the screen width/height, or the ring's band width.
    /// </summary>
    public static IReadOnlyList<LightArea> Areas(LightShape shape, double size, int w, int h)
    {
        size = Math.Clamp(size, 0.02, 1);
        w = Math.Max(4, w); h = Math.Max(4, h); // a hidden/zero-sized surface must never yield negative areas
        switch (shape)
        {
            case LightShape.SideBars:
            {
                int bw = Math.Max(1, (int)Math.Round(w * size / 2)); // both bars together cover `size` of the width
                return [new(0, 0, bw, h), new(w - bw, 0, bw, h)];
            }
            case LightShape.TopBar:
            {
                int bh = Math.Max(1, (int)Math.Round(h * size));
                return [new(0, 0, w, bh)];
            }
            case LightShape.Ring:
            {
                // A ring centred on the screen, as large as fits; the band is `size` of its radius.
                int d = Math.Min(w, h);
                int x = (w - d) / 2, y = (h - d) / 2;
                int band = Math.Max(2, (int)Math.Round(d / 2.0 * size));
                if (band * 2 >= d) return [new(x, y, d, d, Ellipse: true)];
                return [new(x, y, d, d, Ellipse: true), new(x + band, y + band, d - 2 * band, d - 2 * band, Ellipse: true, Subtract: true)];
            }
            default: // Frame
            {
                int t = Math.Max(1, (int)Math.Round(Math.Min(w, h) * size / 2));
                return [new(0, 0, w, t), new(0, h - t, w, t), new(0, t, t, h - 2 * t), new(w - t, t, t, h - 2 * t)];
            }
        }
    }

    /// <summary>Arc start/sweep (degrees, clockwise from 12 o'clock) for a direction preset.</summary>
    public static (double Start, double Sweep) DirectionArc(LightDirection d, double customStart = 0, double customSweep = 360) => d switch
    {
        LightDirection.Left => (180, 180),      // 6 → 12 o'clock through 9
        LightDirection.Right => (0, 180),       // 12 → 6 through 3
        LightDirection.Top => (270, 180),       // 9 → 3 through 12
        LightDirection.Bottom => (90, 180),     // 3 → 9 through 6
        LightDirection.KeyLeft => (255, 120),   // upper-left key light, centred on 10–11 o'clock
        LightDirection.KeyRight => (-15, 120),  // upper-right, centred on 1–2 o'clock
        LightDirection.Custom => (customStart, Math.Clamp(customSweep, 10, 360)),
        _ => (0, 360),
    };

    /// <summary>The ring's centre and outer radius in pixels on a w×h surface.</summary>
    public static (double Cx, double Cy, double Radius, double Band) RingLayout(ScreenLightSettings s, double w, double h)
    {
        double side = Math.Max(4, Math.Min(w, h));
        double radius = Math.Clamp(s.Diameter, 0.1, 1.6) * side / 2;
        double band = Math.Max(2, radius * Math.Clamp(s.Size, 0.02, 1));
        return (Math.Clamp(s.CentreX, 0, 1) * w, Math.Clamp(s.CentreY, 0, 1) * h, radius, Math.Min(band, radius));
    }

    /// <summary>A point on a circle at an angle measured clockwise from 12 o'clock (screen coordinates, y down).</summary>
    public static (double X, double Y) OnCircle(double cx, double cy, double r, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return (cx + r * Math.Sin(a), cy - r * Math.Cos(a));
    }

    /// <summary>Polygon of an arc band (outer edge then inner edge back), for window regions.</summary>
    public static List<(int X, int Y)> ArcBandPolygon(double cx, double cy, double rOuter, double rInner, double start, double sweep, int segments = 96)
    {
        sweep = Math.Clamp(sweep, 1, 360);
        int n = Math.Max(4, (int)(segments * sweep / 360));
        var pts = new List<(int, int)>(2 * n + 2);
        for (int i = 0; i <= n; i++) { var (x, y) = OnCircle(cx, cy, rOuter, start + sweep * i / n); pts.Add(((int)Math.Round(x), (int)Math.Round(y))); }
        for (int i = n; i >= 0; i--) { var (x, y) = OnCircle(cx, cy, rInner, start + sweep * i / n); pts.Add(((int)Math.Round(x), (int)Math.Round(y))); }
        return pts;
    }

    /// <summary>LED positions along the arc (about one LED per band-width of circumference).</summary>
    public static List<(double X, double Y)> LedPositions(double cx, double cy, double r, double band, double start, double sweep)
    {
        double arcLength = 2 * Math.PI * r * Math.Clamp(sweep, 1, 360) / 360;
        int count = Math.Clamp((int)(arcLength / (band * 1.6)), 3, 400);
        bool closed = sweep >= 359.9;
        var list = new List<(double, double)>(count);
        for (int i = 0; i < count; i++)
        {
            double t = closed ? (double)i / count : (count == 1 ? 0.5 : (double)i / (count - 1));
            list.Add(OnCircle(cx, cy, r, start + sweep * t));
        }
        return list;
    }

    /// <summary>Share of the screen the shape lights (0..1), used to show coverage and for tests.</summary>
    public static double Coverage(LightShape shape, double size, int w, int h)
    {
        double lit = 0;
        foreach (var a in Areas(shape, size, w, h))
        {
            double area = a.Ellipse ? Math.PI * a.W * a.H / 4 : (double)a.W * a.H;
            lit += a.Subtract ? -area : area;
        }
        return lit / ((double)w * h);
    }

    /// <summary>
    /// One auto-tune step: nudges light brightness so the face's average luma (0..255) moves toward
    /// <paramref name="target"/>. Small dead-band and step limit keep the light from pumping.
    /// </summary>
    public static double AutoTuneStep(double brightness, double faceLuma, double target = 130, double maxStep = 0.05)
    {
        double error = (target - faceLuma) / 255.0;
        if (Math.Abs(error) < 0.03) return brightness;
        double step = Math.Clamp(error * 0.6, -maxStep, maxStep);
        return Math.Clamp(brightness + step, 0.2, 1.0);
    }

    /// <summary>Average luma of the centre third of a BGRA frame (where a face usually is).</summary>
    public static double CentreLuma(byte[] bgra, int w, int h, float[]? personMask = null)
    {
        long sum = 0, n = 0;
        int x0 = w / 3, x1 = w * 2 / 3, y0 = h / 6, y1 = h * 2 / 3;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                if (personMask is not null && personMask[y * w + x] < 0.5f) continue;
                int i = (y * w + x) * 4;
                sum += (bgra[i + 2] * 77 + bgra[i + 1] * 150 + bgra[i] * 29) >> 8; n++;
            }
        return n == 0 ? 128 : (double)sum / n;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
