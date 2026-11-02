using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenKamera.Core;

/// <summary>
/// One layer of a scene. Geometry is normalised to the canvas: X/Y/W/H in 0..1, sizes as a fraction of the
/// canvas height, so a scene looks the same at 720p, 4K or vertical.
/// </summary>
public sealed class SceneLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Kind { get; set; } = "text"; // text | image | shape | camera
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    /// <summary>Drawn behind the person (in front of the background): needs the person cut-out.</summary>
    public bool Behind { get; set; }
    public double X { get; set; } = 0.05;
    public double Y { get; set; } = 0.05;
    public double W { get; set; } = 0.4;
    public double H { get; set; } = 0.1;
    public double Opacity { get; set; } = 1;

    // Text
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 0.06;
    public bool Bold { get; set; }
    public string Align { get; set; } = "left"; // left | center | right

    // Paint
    public string Color { get; set; } = "#FFFFFF";   // text colour / shape fill
    public string Fill { get; set; } = "";           // text box background ("" = none)
    public string Stroke { get; set; } = "";         // outline colour ("" = none)
    public double StrokeWidth { get; set; }          // fraction of canvas height
    public double CornerRadius { get; set; }         // fraction of canvas height
    public string Shape { get; set; } = "rect";      // rect | ellipse (shapes, images, camera)

    // Image
    public string Source { get; set; } = "";         // "assets/logo.png" inside the scene folder, or an absolute path

    [JsonIgnore] public string Glyph => Kind switch
    {
        "text" => "", "image" => "", "camera" => "", _ => Shape == "ellipse" ? "" : "",
    };
    [JsonIgnore] public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : Kind switch
    {
        "text" => string.IsNullOrWhiteSpace(Text) ? "Text" : Text,
        "image" => "Image", "camera" => "Video source", _ => Shape == "ellipse" ? "Circle" : "Shape",
    };

    [JsonIgnore] public string EyeGlyph => Visible ? "" : "";
    [JsonIgnore] public double ListOpacity => Visible ? 1 : 0.45;

    public SceneLayer Clone() => JsonSerializer.Deserialize<SceneLayer>(JsonSerializer.Serialize(this))!;
}

public sealed class SceneDocument
{
    public string Id { get; set; } = "user-" + Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Untitled";
    public string Category { get; set; } = "Utility"; // Professional | Social | Streaming | Utility
    /// <summary>Canvas colour behind a camera that does not fill the frame.</summary>
    public string Background { get; set; } = "#101418";
    /// <summary>Optional second colour for a vertical gradient background ("" = solid).</summary>
    public string Background2 { get; set; } = "";
    public List<SceneLayer> Layers { get; set; } = [];

    /// <summary>Ctrl+N shortcut shown on the scene rail (1-based), set by the UI.</summary>
    [JsonIgnore] public int Shortcut { get; set; }
    /// <summary>1 when the scene has a Ctrl+1…9 shortcut, else 0 (badge opacity).</summary>
    [JsonIgnore] public string ShortcutText => Shortcut is >= 1 and <= 9 ? $"Ctrl {Shortcut}" : "";
    [JsonIgnore] public double ShortcutOpacity => Shortcut is >= 1 and <= 9 ? 1 : 0;
    [JsonIgnore] public bool IsTemplate => Templates.Any(t => t.Id == Id);
    /// <summary>Folder that holds this scene's assets (null for unsaved templates).</summary>
    [JsonIgnore] public string? Folder { get; set; }
    [JsonIgnore] public string Glyph => Layers.FirstOrDefault(l => l.Kind != "camera")?.Glyph ?? "";

    /// <summary>The camera placement: an explicit camera layer, or the full canvas.</summary>
    [JsonIgnore] public SceneLayer Camera =>
        Layers.FirstOrDefault(l => l.Kind == "camera") ?? new SceneLayer { Kind = "camera", X = 0, Y = 0, W = 1, H = 1 };
    [JsonIgnore] public IEnumerable<SceneLayer> Overlays => Layers.Where(l => l.Kind != "camera");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static SceneDocument FromJson(string json) =>
        JsonSerializer.Deserialize<SceneDocument>(json, Json) ?? new SceneDocument();

    public SceneDocument Clone()
    {
        var c = FromJson(ToJson());
        c.Folder = Folder;
        return c;
    }

    /// <summary>Resolves an image layer's source to a file path, or null.</summary>
    public string? ResolveAsset(SceneLayer layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Source)) return null;
        string p = Path.IsPathRooted(layer.Source) ? layer.Source
                 : Folder is null ? "" : Path.GetFullPath(Path.Combine(Folder, layer.Source));
        return File.Exists(p) ? p : null;
    }

    // ── Built-in templates (our own designs, placeholder text only) ─────────
    private static SceneLayer T(string text, double x, double y, double w, double h, double size,
        bool bold = false, string color = "#FFFFFF", string align = "left", string fill = "", string name = "") =>
        new() { Kind = "text", Text = text, X = x, Y = y, W = w, H = h, FontSize = size, Bold = bold, Color = color, Align = align, Fill = fill, Name = name };

    public static IReadOnlyList<SceneDocument> Templates { get; } =
    [
        new() { Id = "blank", Name = "Blank", Category = "Utility", Layers = [] },
        new()
        {
            // Just your name, small and quiet in the corner: no branding, no handles, no watermark.
            Id = "just-my-name", Name = "Just my name", Category = "Professional",
            Layers =
            [
                new() { Kind = "shape", Name = "Plate", X = 0.03, Y = 0.865, W = 0.26, H = 0.075, Color = "#B3101214", CornerRadius = 0.012 },
                T("Your Name", 0.045, 0.875, 0.24, 0.055, 0.04, bold: true, name: "Name"),
            ],
        },
        new()
        {
            Id = "lower-third", Name = "Lower Third", Category = "Professional",
            Layers =
            [
                new() { Kind = "shape", Name = "Bar", X = 0, Y = 0.80, W = 0.62, H = 0.16, Color = "#E6141618", CornerRadius = 0 },
                new() { Kind = "shape", Name = "Accent", X = 0.035, Y = 0.83, W = 0.006, H = 0.10, Color = "#FF2EA3F2" },
                T("Your Name", 0.055, 0.815, 0.55, 0.07, 0.055, bold: true, name: "Name"),
                T("Title or topic", 0.055, 0.885, 0.55, 0.05, 0.034, color: "#FFC9CDD0", name: "Title"),
            ],
        },
        new()
        {
            Id = "name-card", Name = "Name Card", Category = "Professional",
            Layers =
            [
                new() { Kind = "shape", Name = "Badge", X = 0.03, Y = 0.05, W = 0.07, H = 0.124, Shape = "ellipse", Color = "#FF2EA3F2" },
                T("YN", 0.03, 0.078, 0.07, 0.07, 0.05, bold: true, align: "center", name: "Initials"),
                T("Your Name", 0.115, 0.045, 0.6, 0.09, 0.075, bold: true, name: "Name"),
                T("@yourhandle", 0.115, 0.125, 0.6, 0.06, 0.042, color: "#FFDDE3E8", name: "Handle"),
                T("you@example.com", 0.03, 0.22, 0.6, 0.05, 0.034, name: "Email"),
                T("City, Country", 0.03, 0.275, 0.6, 0.05, 0.034, name: "Location"),
            ],
        },
        new()
        {
            Id = "framed", Name = "Framed", Category = "Social",
            Layers =
            [
                new() { Kind = "shape", Name = "Frame", X = 0.02, Y = 0.035, W = 0.96, H = 0.93, Color = "#00000000",
                        Stroke = "#FFE8A33D", StrokeWidth = 0.008, CornerRadius = 0.03 },
            ],
        },
        new()
        {
            Id = "bubble", Name = "Bubble Cam", Category = "Streaming", Background = "#FF1B1035", Background2 = "#FF0B0716",
            Layers =
            [
                new() { Kind = "camera", Name = "Video source", X = 0.34, Y = 0.12, W = 0.3375, H = 0.6, Shape = "ellipse",
                        Stroke = "#FFFF4FA3", StrokeWidth = 0.01 },
                T("Your Name", 0.1, 0.76, 0.8, 0.08, 0.06, bold: true, align: "center", name: "Name"),
                T("Live now", 0.1, 0.845, 0.8, 0.05, 0.036, color: "#FFFF8BC6", align: "center", name: "Tagline"),
            ],
        },
        new()
        {
            Id = "presentation", Name = "Presentation", Category = "Professional", Background = "#FF0E2A1E", Background2 = "#FF06140E",
            Layers =
            [
                new() { Kind = "shape", Name = "Slide", X = 0.04, Y = 0.07, W = 0.66, H = 0.86, Color = "#FFFFFFFF", CornerRadius = 0.02 },
                T("Today's topic", 0.08, 0.12, 0.58, 0.09, 0.06, bold: true, color: "#FF101418", name: "Heading"),
                T("• First point\n• Second point\n• Third point", 0.08, 0.25, 0.58, 0.4, 0.045, color: "#FF2B3036", name: "Bullets"),
                new() { Kind = "camera", Name = "Video source", X = 0.73, Y = 0.5, W = 0.2475, H = 0.44, Shape = "ellipse",
                        Stroke = "#FFB8F23A", StrokeWidth = 0.008 },
            ],
        },
        new()
        {
            Id = "corner-logo", Name = "Corner Logo", Category = "Utility",
            Layers =
            [
                T("YOUR LOGO", 0.78, 0.04, 0.19, 0.06, 0.032, bold: true, align: "center", fill: "#99000000", name: "Logo"),
            ],
        },
    ];
}

/// <summary>Scene storage: built-in templates (editable; edits are saved as an override) plus user scenes.</summary>
public static class SceneLibrary
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenKamera", "Scenes");

    /// <summary>Overrides the folder (tests).</summary>
    public static string? RootOverride { get; set; }
    private static string Base => RootOverride ?? Root;

    private static string FolderOf(string id) => Path.Combine(Base, SafeId(id));
    private static string SafeId(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    /// <summary>Templates (with any saved edits) followed by user scenes.</summary>
    public static List<SceneDocument> All()
    {
        var list = SceneDocument.Templates.Select(t => Load(t.Id)!).ToList();
        if (Directory.Exists(Base))
            foreach (var dir in Directory.EnumerateDirectories(Base).OrderBy(Directory.GetCreationTimeUtc))
            {
                string id = Path.GetFileName(dir);
                if (SceneDocument.Templates.Any(t => t.Id == id)) continue;
                if (Load(id) is { } s) list.Add(s);
            }
        return list;
    }

    public static SceneDocument? Load(string id)
    {
        string file = Path.Combine(FolderOf(id), "scene.json");
        if (File.Exists(file))
        {
            try
            {
                var s = SceneDocument.FromJson(File.ReadAllText(file));
                s.Id = id; s.Folder = FolderOf(id);
                return s;
            }
            catch (JsonException) { /* fall back to the template */ }
        }
        return SceneDocument.Templates.FirstOrDefault(t => t.Id == id)?.Clone();
    }

    public static void Save(SceneDocument scene)
    {
        string dir = FolderOf(scene.Id);
        Directory.CreateDirectory(dir);
        scene.Folder = dir;
        File.WriteAllText(Path.Combine(dir, "scene.json"), scene.ToJson());
    }

    /// <summary>Templates go back to their built-in design; user scenes are deleted.</summary>
    public static void ResetOrDelete(string id)
    {
        string dir = FolderOf(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public static bool HasEdits(string id) => File.Exists(Path.Combine(FolderOf(id), "scene.json"));

    public static SceneDocument CreateBlank(string name = "New scene")
    {
        var s = new SceneDocument { Name = name, Category = "Utility" };
        Save(s);
        return s;
    }

    public static SceneDocument Duplicate(SceneDocument source)
    {
        var copy = source.Clone();
        copy.Id = "user-" + Guid.NewGuid().ToString("N")[..10];
        copy.Name = source.Name + " copy";
        Save(copy);
        if (source.Folder is { } from && Directory.Exists(Path.Combine(from, "assets")))
            CopyDir(Path.Combine(from, "assets"), Path.Combine(copy.Folder!, "assets"));
        return copy;
    }

    /// <summary>Copies an image into the scene's assets and returns the relative source path.</summary>
    public static string AddAsset(SceneDocument scene, string imagePath)
    {
        if (scene.Folder is null) Save(scene);
        string assets = Path.Combine(scene.Folder!, "assets");
        Directory.CreateDirectory(assets);
        string name = Path.GetFileName(imagePath);
        File.Copy(imagePath, Path.Combine(assets, name), overwrite: true);
        return "assets/" + name;
    }

    /// <summary>Writes a shareable .okscene (zip of scene.json + assets/).</summary>
    public static void Export(SceneDocument scene, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("scene.json");
        using (var w = new StreamWriter(entry.Open())) w.Write(scene.ToJson());
        if (scene.Folder is { } f && Directory.Exists(Path.Combine(f, "assets")))
            foreach (var file in Directory.EnumerateFiles(Path.Combine(f, "assets")))
                zip.CreateEntryFromFile(file, "assets/" + Path.GetFileName(file));
    }

    /// <summary>Imports a .okscene as a new user scene. Rejects entries that would escape the scene folder.</summary>
    public static SceneDocument Import(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var json = zip.GetEntry("scene.json") ?? throw new InvalidDataException("Not an KISSIK scene: scene.json missing.");
        SceneDocument scene;
        using (var r = new StreamReader(json.Open())) scene = SceneDocument.FromJson(r.ReadToEnd());
        scene.Id = "user-" + Guid.NewGuid().ToString("N")[..10];
        Save(scene);
        string root = Path.GetFullPath(scene.Folder!) + Path.DirectorySeparatorChar;
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith("assets/", StringComparison.Ordinal) || e.FullName.EndsWith('/')) continue;
            string dest = Path.GetFullPath(Path.Combine(scene.Folder!, e.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe path in scene file.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
        }
        // Absolute image paths from someone else's machine are meaningless here.
        foreach (var l in scene.Layers.Where(l => Path.IsPathRooted(l.Source))) l.Source = "";
        Save(scene);
        return scene;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }
}
