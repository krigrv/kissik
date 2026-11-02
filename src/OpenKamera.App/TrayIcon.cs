using System.Runtime.InteropServices;

namespace OpenKamera_App;

/// <summary>
/// Notification-area icon with a small menu, so KISSIK is one click away while a meeting app has the screen.
/// Uses Shell_NotifyIcon with the main window as the message target (WinUI has no tray API of its own).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 0x4B, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;
    private readonly List<(string Text, Func<bool> Checked, Action Run)> _items = [];
    private readonly Action _open;
    private NOTIFYICONDATA _data;

    public TrayIcon(IntPtr hwnd, string iconPath, string tip, Action open)
    {
        _hwnd = hwnd; _open = open;
        _proc = WndProc;
        SetWindowSubclass(_hwnd, _proc, 0x7A1, IntPtr.Zero);
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY, hIcon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 16, 16, 0x10 /*LR_LOADFROMFILE*/), szTip = tip,
        };
        Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    public void Add(string text, Action run, Func<bool>? isChecked = null) => _items.Add((text, isChecked ?? (() => false), run));
    public void AddSeparator() => _items.Add(("-", () => false, () => { }));

    public void SetTip(string tip) { _data.szTip = tip; Shell_NotifyIcon(NIM_MODIFY, ref _data); }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)lParam & 0xFFFF;
            if (ev == WM_LBUTTONUP) _open();
            else if (ev == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        for (int i = 0; i < _items.Count; i++)
        {
            var (text, chk, _) = _items[i];
            if (text == "-") AppendMenu(menu, 0x800 /*MF_SEPARATOR*/, 0, null);
            else AppendMenu(menu, chk() ? 0x8u /*MF_CHECKED*/ : 0u, (nuint)(i + 1), text);
        }
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // so the menu closes when you click elsewhere
        int cmd = TrackPopupMenu(menu, 0x100 /*TPM_RETURNCMD*/ | 0x20 /*TPM_BOTTOMALIGN*/, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd > 0 && cmd <= _items.Count) { try { _items[cmd - 1].Run(); } catch { } }
    }

    public void Dispose()
    {
        Shell_NotifyIcon(NIM_DELETE, ref _data);
        if (_data.hIcon != IntPtr.Zero) DestroyIcon(_data.hIcon);
        RemoveWindowSubclass(_hwnd, _proc, 0x7A1);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public int uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int r, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr h, SubclassProc p, nuint id, IntPtr d);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr h, SubclassProc p, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
