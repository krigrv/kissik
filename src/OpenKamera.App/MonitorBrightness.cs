using System.Runtime.InteropServices;

namespace OpenKamera_App;

/// <summary>
/// Reads and sets an external monitor's backlight over DDC/CI (the same channel as the monitor's own menu).
/// Not every monitor supports it; laptop panels usually don't (Windows handles those separately).
/// </summary>
public static class MonitorBrightness
{
    /// <summary>Current brightness 0..100 of the monitor at a screen point, or null if unsupported.</summary>
    public static int? Get(int x, int y) => WithMonitor(x, y, (h) =>
        GetMonitorBrightness(h, out uint min, out uint cur, out uint max) && max > min
            ? (int?)Math.Round(100.0 * (cur - min) / (max - min)) : null);

    /// <summary>Sets brightness 0..100; returns false if the monitor refused or doesn't support DDC/CI.</summary>
    public static bool Set(int x, int y, int percent) => WithMonitor(x, y, (h) =>
    {
        if (!GetMonitorBrightness(h, out uint min, out _, out uint max) || max <= min) return false;
        uint value = (uint)Math.Round(min + (max - min) * Math.Clamp(percent, 0, 100) / 100.0);
        return SetMonitorBrightness(h, value);
    });

    private static T WithMonitor<T>(int x, int y, Func<IntPtr, T> use)
    {
        IntPtr hmon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint count) || count == 0) return default!;
        var monitors = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hmon, count, monitors)) return default!;
        try { return use(monitors[0].hPhysicalMonitor); }
        finally { DestroyPhysicalMonitors(count, monitors); }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool SetMonitorBrightness(IntPtr h, uint value);
}
