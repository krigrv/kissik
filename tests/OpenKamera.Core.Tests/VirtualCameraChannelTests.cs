using System.IO.MemoryMappedFiles;
using OpenKamera.Core;

namespace OpenKamera.Core.Tests;

public class VirtualCameraChannelTests
{
    /// <summary>
    /// Two copies of KISSIK once fed the meeting camera in turn, and the meeting flickered between their pictures.
    /// The copy that is already feeding keeps the camera; a second one waits until the first has gone quiet.
    /// </summary>
    [Fact]
    public void ASecondKissik_DoesNotWriteOverTheFirst()
    {
        string name = @"Local\KissikVcamTest" + Guid.NewGuid().ToString("N");
        using var map = MemoryMappedFile.CreateNew(name, VirtualCameraChannel.MappingSize);
        using var view = map.CreateViewAccessor();
        view.Write(0, VirtualCameraChannel.Magic);
        view.Write(36, 1u); // one meeting app is watching
        using var channel = new VirtualCameraChannel(name);
        var frame = new byte[4 * 2 * 4];

        Assert.True(channel.TryWrite(frame, 4, 2));
        Assert.False(channel.AnotherWriter);

        // Another process wrote a moment ago.
        view.Write(32, (uint)Environment.ProcessId + 1);
        view.Write(24, DateTime.UtcNow.ToFileTimeUtc());
        int seq = view.ReadInt32(8);
        Assert.False(channel.TryWrite(frame, 4, 2));
        Assert.True(channel.AnotherWriter);
        Assert.Equal(seq, view.ReadInt32(8));

        // It stopped two seconds ago: this copy takes over.
        view.Write(24, DateTime.UtcNow.AddSeconds(-2).ToFileTimeUtc());
        Assert.True(channel.TryWrite(frame, 4, 2));
        Assert.False(channel.AnotherWriter);
        Assert.Equal((uint)Environment.ProcessId, view.ReadUInt32(32));
    }
}
