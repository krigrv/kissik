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
    /// <summary>Mirror only the on-screen preview; meetings and recordings get the true (unmirrored) view.</summary>
    public bool SmartMirror { get; set; }
    /// <summary>Ask the native phone app for H.264 instead of JPEG when it offers it.</summary>
    public bool PhoneH264 { get; set; } = true;
    public bool PrivacyGuard { get; set; }
    /// <summary>Show one small, labelled sponsored pick in the Camera panel (never in the video).</summary>
    public bool SponsoredPicks { get; set; } = true;
    public SceneTransition Transition { get; set; } = SceneTransition.Fade;
    /// <summary>Re-check each captioned sentence with Whisper for accuracy (uses more power).</summary>
    public bool AccurateCaptions { get; set; }
    /// <summary>Send crash reports automatically: null = not asked yet.</summary>
    public bool? CrashReportsAuto { get; set; }
    public List<string> DismissedSponsors { get; set; } = [];
    public BackgroundSettings Background { get; set; } = BackgroundSettings.Off;
    /// <summary>Gallery favourites: "scene:id", "bg:id", "filter:Name", "lut:Name".</summary>
    public HashSet<string> Favourites { get; set; } = [];
    public ScreenLightSettings ScreenLight { get; set; } = new();
    public AudioMixSettings Audio { get; set; } = new();
    /// <summary>Capture microphone audio (meters + sound in recordings). Off until the user turns it on.</summary>
    public bool AudioOn { get; set; }
    /// <summary>Mix what the PC plays (loopback) into recordings and the meeting mix.</summary>
    public bool SystemSoundOn { get; set; }
    /// <summary>Counts finished recordings, for an occasional quiet "support the project" line.</summary>
    public int RecordingsSaved { get; set; }
    /// <summary>"System" (follow Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>The one-time welcome has been shown.</summary>
    public bool SeenWelcome { get; set; }
    /// <summary>"Simple" (five big buttons) or "Studio" (the full window). Empty until the user picks on first run.</summary>
    public string UiMode { get; set; } = "";
    /// <summary>Bigger buttons and text everywhere (View → Large controls).</summary>
    public bool LargeControls { get; set; }
    /// <summary>Offline caption model language code (e.g. "en-us").</summary>
    public string CaptionLanguage { get; set; } = "en-us";
    /// <summary>Local control API / MCP server (localhost + token). Off until turned on.</summary>
    public bool ControlApiOn { get; set; }
    /// <summary>Record a 9:16 vertical copy next to the normal recording.</summary>
    public bool RecordVertical { get; set; }
    /// <summary>Show the frame-rate and delay details over the picture.</summary>
    public bool ShowPictureStats { get; set; }
    /// <summary>Size of the crash log when the user last saw the "KISSIK closed unexpectedly" note.</summary>
    public long CrashLogSeen { get; set; }
    /// <summary>Livestream destinations (keys encrypted for this Windows user).</summary>
    public List<StreamDestination> StreamDestinations { get; set; } = [];
    /// <summary>The user picked a meeting output themselves (so we stop auto-picking a virtual cable).</summary>
    public bool MeetOutChosen { get; set; }
    public int Rotation { get; set; }
    public List<LookPreset> Presets { get; set; } = [];
    public PrompterSettings Prompter { get; set; } = new();
    /// <summary>Per-app profiles: app name (as Windows reports it) → preset name.</summary>
    public Dictionary<string, string> AppPresets { get; set; } = [];

    /// <summary>Imported <c>.cube</c> files are copied here.</summary>
    public static string LutFolder => Path.Combine(
        AppPaths.Data, "LUTs");

    public static string DefaultPath => Path.Combine(
        AppPaths.Data, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
                s.ImageFolder = AppPaths.Remap(s.ImageFolder); // a folder picked inside the old OpenKamera folder
                return s;
            }
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
