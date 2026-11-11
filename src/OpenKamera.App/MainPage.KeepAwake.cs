using System.Runtime.InteropServices;

namespace OpenKamera_App;

// Keep the PC awake while KISSIK is doing something that must not be cut off: recording, streaming, or feeding
// a meeting. Windows would otherwise go to sleep on its idle timer in the middle of a long recording.
public sealed partial class MainPage
{
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    private bool _keptAwake;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Called every couple of seconds on the UI thread (the request belongs to the thread that made it).</summary>
    private void UpdateKeepAwake()
    {
        bool busy = _recorder is not null || _savingRecording || _live is not null || _replay is not null || _vcam.HasClients;
        if (busy == _keptAwake) return;
        _keptAwake = busy;
        try { SetThreadExecutionState(busy ? EsContinuous | EsSystemRequired : EsContinuous); } catch { }
    }

    private void ReleaseKeepAwake()
    {
        if (!_keptAwake) return;
        _keptAwake = false;
        try { SetThreadExecutionState(EsContinuous); } catch { }
    }
}
