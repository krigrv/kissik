using System.IO.MemoryMappedFiles;

namespace OpenKamera.Core;

/// <summary>
/// Writes OpenKamera's final picture into the shared-memory channel the virtual camera reads.
/// Layout must match src/OpenKamera.MediaSource/FrameChannel.h. The media source creates the channel when a
/// meeting app opens the camera; until then <see cref="TryWrite"/> is a cheap no-op (re-checked once a second).
/// </summary>
public sealed class VirtualCameraChannel : IDisposable
{
    public const string GlobalName = @"Global\OpenKameraVirtualCamera";
    public const string LocalName = @"Local\OpenKameraVirtualCamera";
    public const uint Magic = 0x43564B4F; // "OKVC"
    public const int MaxWidth = 1920, MaxHeight = 1080, HeaderSize = 256;
    public const long MappingSize = HeaderSize + (long)MaxWidth * MaxHeight * 4;

    // Header offsets (FrameHeader, pack 8).
    private const int OffMagic = 0, OffSequence = 8, OffWidth = 12, OffHeight = 16, OffStride = 20, OffTimestamp = 24, OffPid = 32, OffClients = 36;

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private DateTime _nextTry = DateTime.MinValue;
    private readonly string[] _names;

    public VirtualCameraChannel(params string[] names) => _names = names.Length > 0 ? names : [GlobalName, LocalName];

    /// <summary>True while at least one meeting app has the virtual camera open.</summary>
    public bool HasClients => Open() && _view!.ReadUInt32(OffClients) > 0;
    public long FramesWritten { get; private set; }

    /// <summary>
    /// True while another KISSIK is feeding the same camera. Two copies writing in turn made the meeting picture
    /// flicker between their two pictures, so the copy that got there first keeps the camera and this one waits.
    /// </summary>
    public bool AnotherWriter { get; private set; }

    private static readonly long Fresh = TimeSpan.FromSeconds(1).Ticks;

    private bool Open()
    {
        if (_view is not null) return true;
        if (DateTime.UtcNow < _nextTry) return false;
        _nextTry = DateTime.UtcNow.AddSeconds(1);
        foreach (var name in _names)
        {
            try
            {
                _map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
                _view = _map.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.ReadWrite);
                if (_view.ReadUInt32(OffMagic) != Magic) { Close(); continue; }
                return true;
            }
            catch (Exception) { Close(); } // not there yet: no meeting app is using the camera
        }
        return false;
    }

    /// <summary>
    /// Writes a BGRA frame if a meeting app is watching. Larger frames are downscaled to fit 1920×1080.
    /// Uses the seqlock the reader expects: sequence odd while copying, even when done.
    /// </summary>
    public bool TryWrite(byte[] bgra, int width, int height)
    {
        if (!HasClients) return false;
        byte[] px = bgra; int w = width, h = height;
        if (w > MaxWidth || h > MaxHeight)
        {
            double s = Math.Min((double)MaxWidth / w, (double)MaxHeight / h);
            int nw = Math.Max(2, (int)(w * s)) & ~1, nh = Math.Max(2, (int)(h * s)) & ~1;
            (px, w, h) = OutputFit.Stretch(bgra, width, height, nw, nh);
        }
        try
        {
            var v = _view!;
            uint owner = v.ReadUInt32(OffPid);
            long age = DateTime.UtcNow.ToFileTimeUtc() - v.ReadInt64(OffTimestamp);
            AnotherWriter = owner != 0 && owner != (uint)Environment.ProcessId && age >= 0 && age < Fresh;
            if (AnotherWriter) return false;
            int seq = v.ReadInt32(OffSequence);
            if ((seq & 1) == 0) seq++;
            v.Write(OffSequence, seq);                 // odd: writing
            v.Write(OffWidth, (uint)w);
            v.Write(OffHeight, (uint)h);
            v.Write(OffStride, (uint)(w * 4));
            v.WriteArray(HeaderSize, px, 0, w * h * 4);
            v.Write(OffTimestamp, DateTime.UtcNow.ToFileTimeUtc());
            v.Write(OffPid, (uint)Environment.ProcessId);
            System.Threading.Thread.MemoryBarrier();
            v.Write(OffSequence, seq + 1);             // even: stable
            FramesWritten++;
            return true;
        }
        catch (Exception) { Close(); return false; }
    }

    private void Close()
    {
        _view?.Dispose(); _view = null;
        _map?.Dispose(); _map = null;
    }

    public void Dispose() => Close();
}
