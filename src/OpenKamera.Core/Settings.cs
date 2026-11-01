using System.Text.Json;

namespace OpenKamera.Core;

public sealed class DeviceSettings
{
    public Dictionary<string, double> Values { get; set; } = [];
    public Dictionary<string, bool> Auto { get; set; } = [];
}

public sealed class AppSettings
{
    public string? LastDeviceId { get; set; }
    public string ProfileName { get; set; } = OutputProfile.P720.Name;
    public string SceneId { get; set; } = "blank";
    public string RecordingFolder { get; set; } = "";
    public string ImageFolder { get; set; } = "";
    public Dictionary<string, DeviceSettings> Devices { get; set; } = [];

    /// <summary>Enhance slider values by slider name.</summary>
    public Dictionary<string, double> Enhance { get; set; } = [];
    public FilterPreset Filter { get; set; } = FilterPreset.None;
    public string? LutName { get; set; }
    public bool Mirror { get; set; }
    public BackgroundSettings Background { get; set; } = BackgroundSettings.Off;
    /// <summary>Gallery favourites: "scene:id", "bg:id", "filter:Name", "lut:Name".</summary>
    public HashSet<string> Favourites { get; set; } = [];
    public int Rotation { get; set; }

    /// <summary>Imported <c>.cube</c> files are copied here.</summary>
    public static string LutFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenKamera", "LUTs");

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenKamera", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        }
        catch (JsonException) { /* fall through to defaults */ }
        catch (IOException) { /* fall through to defaults */ }
        return new();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public DeviceSettings ForDevice(string deviceId)
    {
        if (!Devices.TryGetValue(deviceId, out var d))
            Devices[deviceId] = d = new DeviceSettings();
        return d;
    }
}
