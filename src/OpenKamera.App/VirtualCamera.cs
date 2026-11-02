using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenKamera_App;

/// <summary>
/// Installs and registers the "OpenKamera" camera that meeting apps can choose (Windows 11 virtual camera).
/// Install copies the media source DLL to ProgramData and registers it machine-wide (one UAC prompt); after
/// that, <see cref="EnsureRegistered"/> asks Windows to list the camera. It stays listed even when OpenKamera
/// is closed and then shows a friendly slate.
/// </summary>
public static class VirtualCamera
{
    public const string Clsid = "{53B8FDEE-DF3B-440D-9C66-399F14BC6C50}";
    public const string FriendlyName = "KISSIK";
    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenKamera", "VirtualCamera");
    public static string InstalledDll => Path.Combine(InstallFolder, "OpenKamera.VirtualCamera.dll");
    public static string BundledDll => Path.Combine(AppContext.BaseDirectory, "OpenKamera.VirtualCamera.dll");

    private static IMFVirtualCamera? _camera;

    /// <summary>Windows 11 (build 22000+) has the virtual camera API.</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 22000 && NativeLibrary.TryLoad("mfsensorgroup.dll", out _);

    /// <summary>The DLL is copied and registered machine-wide (HKLM).</summary>
    public static bool IsInstalled
    {
        get
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32");
            return key?.GetValue(null) is string path && File.Exists(path);
        }
    }

    /// <summary>Runs the one-time install elevated (UAC). Returns false if the user declined or it failed.</summary>
    public static async Task<bool> InstallAsync()
    {
        if (!File.Exists(BundledDll)) throw new FileNotFoundException("The virtual camera component is missing from this build.", BundledDll);
        string script =
            $"$ErrorActionPreference='Stop'; New-Item -ItemType Directory -Force '{InstallFolder}' | Out-Null; " +
            $"Copy-Item -Force '{BundledDll}' '{InstalledDll}'; " +
            // The camera service runs as LOCAL SERVICE: make sure it can read the DLL.
            $"icacls '{InstallFolder}' /grant '*S-1-5-19:(OI)(CI)RX' | Out-Null; " +
            $"$p = Start-Process regsvr32.exe -ArgumentList '/s','\"{InstalledDll}\"' -Wait -PassThru; exit $p.ExitCode";
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 && IsInstalled;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC declined
    }

    /// <summary>Removes the camera from Windows and unregisters the DLL (UAC).</summary>
    public static async Task<bool> UninstallAsync()
    {
        try { _camera?.Remove(); } catch { }
        _camera = null;
        string script = $"$p = Start-Process regsvr32.exe -ArgumentList '/s','/u','\"{InstalledDll}\"' -Wait -PassThru; Remove-Item -Recurse -Force '{InstallFolder}' -ErrorAction SilentlyContinue; exit $p.ExitCode";
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        try { using var p = Process.Start(psi); if (p is null) return false; await p.WaitForExitAsync(); return !IsInstalled; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>
    /// Asks Windows to list the camera. Tries a persistent (system-lifetime) camera first so it is available
    /// even when OpenKamera is closed; falls back to a session camera that lives while the app runs.
    /// </summary>
    public static string EnsureRegistered()
    {
        if (_camera is not null) return "KISSIK camera is on.";
        if (!IsSupported) return "The virtual camera needs Windows 11.";
        if (!IsInstalled) return "Install the KISSIK camera first.";
        foreach (var lifetime in new[] { MFVirtualCameraLifetime.System, MFVirtualCameraLifetime.Session })
        {
            int hr = MFCreateVirtualCamera(MFVirtualCameraType.SoftwareCameraSource, lifetime, MFVirtualCameraAccess.CurrentUser,
                FriendlyName, Clsid, null, 0, out var cam);
            if (hr < 0 || cam is null) continue;
            hr = cam.Start(IntPtr.Zero);
            if (hr >= 0)
            {
                _camera = cam;
                return lifetime == MFVirtualCameraLifetime.System
                    ? "KISSIK camera is on: pick \"OpenKamera\" in Zoom, Teams, Meet or OBS."
                    : "KISSIK camera is on while KISSIK is open: pick \"OpenKamera\" in your meeting app.";
            }
            Marshal.ReleaseComObject(cam);
        }
        return "Windows did not accept the camera. Try reinstalling it from the camera menu.";
    }

    /// <summary>Stops listing a session camera (a system camera stays listed until uninstalled).</summary>
    public static void Shutdown()
    {
        try { _camera?.Shutdown(); } catch { }
        _camera = null;
    }

    // ── Interop (mfvirtualcamera.h) ──
    private enum MFVirtualCameraType { SoftwareCameraSource = 0 }
    private enum MFVirtualCameraLifetime { Session = 0, System = 1 }
    private enum MFVirtualCameraAccess { CurrentUser = 0, AllUsers = 1 }

    [DllImport("mfsensorgroup.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateVirtualCamera(MFVirtualCameraType type, MFVirtualCameraLifetime lifetime, MFVirtualCameraAccess access,
        string friendlyName, string sourceId, Guid[]? categories, uint categoryCount, out IMFVirtualCamera camera);

    /// <summary>IMFVirtualCamera; the first 30 slots are IMFAttributes, which we never call.</summary>
    [ComImport, Guid("1C08A864-EF6C-4C75-AF59-5F2D68DA9563"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFVirtualCamera
    {
        void _a01(); void _a02(); void _a03(); void _a04(); void _a05(); void _a06(); void _a07(); void _a08(); void _a09(); void _a10();
        void _a11(); void _a12(); void _a13(); void _a14(); void _a15(); void _a16(); void _a17(); void _a18(); void _a19(); void _a20();
        void _a21(); void _a22(); void _a23(); void _a24(); void _a25(); void _a26(); void _a27(); void _a28(); void _a29(); void _a30();
        [PreserveSig] int AddDeviceSourceInfo([MarshalAs(UnmanagedType.LPWStr)] string deviceSourceInfo);
        [PreserveSig] int AddProperty(IntPtr key, int type, IntPtr data, uint size);
        [PreserveSig] int AddRegistryEntry([MarshalAs(UnmanagedType.LPWStr)] string name, int type, IntPtr data, uint size);
        [PreserveSig] int Start(IntPtr callback);
        [PreserveSig] int Stop();
        [PreserveSig] int Remove();
        [PreserveSig] int GetMediaSource(out IntPtr source);
        [PreserveSig] int SendCameraProperty(IntPtr propertySet, uint id, uint flags, IntPtr data, uint size, IntPtr resp, uint respSize, out uint used);
        [PreserveSig] int CreateSyncEvent(IntPtr kseventSet, uint id, uint flags, IntPtr eventHandle, out IntPtr cameraSyncObject);
        [PreserveSig] int CreateSyncSemaphore(IntPtr kseventSet, uint id, uint flags, IntPtr semaphore, int adjust, out IntPtr cameraSyncObject);
        [PreserveSig] int Shutdown();
    }
}
