using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenKamera.Core;

namespace OpenKamera_App;

/// <summary>
/// One display's light: a borderless, always-on-top, non-activating window covering the display, cut to the
/// light's shape with a window region. Outside the shape the screen is untouched and clicks pass through.
/// </summary>
public sealed class ScreenLightWindow : Window
{
    private readonly Grid _surface = new();
    private readonly DisplayArea _display;

    public ScreenLightWindow(DisplayArea display)
    {
        _display = display;
        Title = "KISSIK light";
        Content = _surface;
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Tool window (no taskbar button), never takes focus, and excluded from screen capture so it
        // doesn't show up in screen shares or recordings.
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST));
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    public string DisplayId => _display.DisplayId.Value.ToString();

    /// <summary>Applies colour and shape; shows the window if it is hidden.</summary>
    public void Apply(ScreenLightSettings s)
    {
        var (r, g, b) = ScreenLight.LightColor(s);
        _surface.Background = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));

        var bounds = _display.OuterBounds; // physical pixels
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        if (s.Shape == LightShape.Ring) AddRingRegion(region, s, bounds.Width, bounds.Height);
        else
            foreach (var a in ScreenLight.Areas(s.Shape, s.Size, bounds.Width, bounds.Height))
            {
                IntPtr part = a.Ellipse ? CreateEllipticRgn(a.X, a.Y, a.X + a.W, a.Y + a.H) : CreateRectRgn(a.X, a.Y, a.X + a.W, a.Y + a.H);
                CombineRgn(region, region, part, a.Subtract ? RGN_DIFF : RGN_OR);
                DeleteObject(part);
            }
        SetWindowRgn(hwnd, region, true); // the window owns the region from here on

        if (!AppWindow.IsVisible) AppWindow.Show(activateWindow: false);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Window regions have hard edges, so Soft is drawn as Classic and Halo as a solid disc here; the
    /// in-app light shows the feathered versions.
    /// </summary>
    private static void AddRingRegion(IntPtr region, ScreenLightSettings s, int w, int h)
    {
        var (cx, cy, radius, band) = ScreenLight.RingLayout(s, w, h);
        var (start, sweep) = ScreenLight.DirectionArc(s.Direction, s.ArcStart, s.ArcSweep);
        void Or(IntPtr part) { CombineRgn(region, region, part, RGN_OR); DeleteObject(part); }
        void Band(double rOuter, double rInner)
        {
            var pts = ScreenLight.ArcBandPolygon(cx, cy, rOuter, rInner, start, sweep);
            var arr = pts.Select(p => new POINT { X = p.X, Y = p.Y }).ToArray();
            Or(CreatePolygonRgn(arr, arr.Length, 1));
        }
        switch (s.RingType)
        {
            case RingType.Halo:
                Or(CreateEllipticRgn((int)(cx - radius), (int)(cy - radius), (int)(cx + radius), (int)(cy + radius)));
                break;
            case RingType.Double:
                Band(radius, radius - band * 0.36);
                Band(radius - band * 0.64, radius - band);
                break;
            case RingType.Led:
                foreach (var (x, y) in ScreenLight.LedPositions(cx, cy, radius - band / 2, band, start, sweep))
                {
                    double d = band * 0.45;
                    Or(CreateEllipticRgn((int)(x - d), (int)(y - d), (int)(x + d), (int)(y + d)));
                }
                break;
            default:
                Band(radius, radius - band);
                break;
        }
    }

    // ── Win32 ──
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePolygonRgn(POINT[] points, int count, int fillMode);
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int RGN_OR = 2, RGN_DIFF = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
