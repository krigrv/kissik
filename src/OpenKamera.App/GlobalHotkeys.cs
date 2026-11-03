using System.Runtime.InteropServices;

namespace OpenKamera_App;

/// <summary>
/// System-wide shortcuts (work while another app, like a meeting, has focus): RegisterHotKey on the main window
/// plus a window subclass to receive WM_HOTKEY. A shortcut another app already owns is skipped and reported.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly SubclassProc _proc;
    private int _nextId = 0xB000;

    public List<string> Taken { get; } = [];
    /// <summary>Shortcuts that were taken and now use Ctrl+Alt+Shift instead.</summary>
    public List<string> Moved { get; } = [];

    public GlobalHotkeys(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc; // keep the delegate alive
        SetWindowSubclass(_hwnd, _proc, 0xB0B, IntPtr.Zero);
    }

    /// <summary>Registers Ctrl+Alt+<paramref name="key"/> (a virtual-key code).</summary>
    public void Add(uint key, string label, Action action)
    {
        int id = _nextId++;
        if (RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, key)) { _actions[id] = action; return; }
        // Another app owns it: offer the same key with Shift added rather than losing the shortcut.
        if (RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, key)) { _actions[id] = action; Moved.Add(label.Replace("Ctrl+Alt+", "Ctrl+Alt+Shift+")); return; }
        Taken.Add(label);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue((int)wParam, out var act)) { try { act(); } catch { } return IntPtr.Zero; }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        RemoveWindowSubclass(_hwnd, _proc, 0xB0B);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
