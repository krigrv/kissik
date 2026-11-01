using Windows.Devices.Enumeration;

namespace OpenKamera.Core;

/// <summary>Camera discovery via Windows.Devices.Enumeration (Media Foundation backed).</summary>
public sealed class CameraCatalog
{
    public async Task<IReadOnlyList<CameraDeviceInfo>> GetCamerasAsync()
    {
        var infos = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return infos.Select(d => new CameraDeviceInfo(d.Id, d.Name)).ToList();
    }

    /// <summary>Watches for camera hot-plug; invoke <paramref name="changed"/> on any add/remove/update.</summary>
    public DeviceWatcher CreateWatcher(Action changed)
    {
        var watcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
        watcher.Added += (_, _) => changed();
        watcher.Removed += (_, _) => changed();
        watcher.Updated += (_, _) => changed();
        return watcher;
    }
}
