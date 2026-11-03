namespace OpenKamera.Core;

/// <summary>
/// The user's own name and details, filled into scene text that still holds a template placeholder
/// ("Your Name", "@yourhandle"…). Text the user typed themselves is never touched.
/// </summary>
public sealed class MyDetails
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Handle { get; set; } = "";
    public string Email { get; set; } = "";
    public string Location { get; set; } = "";
    public string Pronouns { get; set; } = "";
    /// <summary>A logo or profile picture (file on this PC); shown in place of the initials circle.</summary>
    public string LogoPath { get; set; } = "";
    /// <summary>Your own fields: "{Company}" in any scene text is replaced by the value saved under "Company".</summary>
    public Dictionary<string, string> Custom { get; set; } = [];

    public bool HasLogo => !string.IsNullOrWhiteSpace(LogoPath) && File.Exists(LogoPath);

    /// <summary>True once the user has given at least their name; until then templates show their placeholders.</summary>
    public bool IsSet => !string.IsNullOrWhiteSpace(Name);

    public string Initials =>
        string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    private string At => string.IsNullOrWhiteSpace(Handle) ? "" : "@" + Handle.Trim().TrimStart('@');

    /// <summary>
    /// The text to show for a scene text layer: the placeholder replaced by the matching detail, or null when that
    /// detail is empty (the layer is hidden rather than showing "you@example.com"). Before any details are given,
    /// and for any text that isn't a placeholder, the text comes back as it is.
    /// </summary>
    public string? Fill(string text)
    {
        if (!IsSet) return text;
        string? whole = text switch
        {
            "Your Name" or "YOUR NAME" => Name.Trim(),
            "YN" => Initials,
            "@yourhandle" or "@yourname" => At,
            "you@example.com" => Email.Trim(),
            "City, Country" => Location.Trim(),
            "they/them" or "Pronouns" => Pronouns.Trim(),
            "Title or topic" or "Head of Product · Company" => Title.Trim(),
            _ => null,
        };
        if (whole is not null) return whole.Length == 0 ? null : (text == "YOUR NAME" ? whole.ToUpperInvariant() : whole);
        // Placeholders inside a longer line ("Golden hour with @yourname").
        if (At.Length > 0) text = text.Replace("@yourname", At).Replace("@yourhandle", At);
        text = text.Replace("Your Name", Name.Trim()).Replace("{pronouns}", Pronouns.Trim());
        foreach (var (key, value) in Custom) text = text.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>Reads "Key = value" lines (one field per line) into <see cref="Custom"/>.</summary>
    public void SetCustom(string lines)
    {
        Custom = [];
        foreach (var line in lines.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim().Trim('{', '}'), value = line[(eq + 1)..].Trim();
            if (key.Length > 0) Custom[key] = value;
        }
    }

    public string CustomText => string.Join("\n", Custom.Select(kv => $"{kv.Key} = {kv.Value}"));

    /// <summary>True when the scene has template placeholders that the user's details would fill.</summary>
    public static bool HasPlaceholders(SceneDocument scene) =>
        scene.Layers.Any(l => l.Kind == "text" && (l.Text.Contains("Your Name") || l.Text.Contains("@your") || l.Text == "you@example.com"));
}
