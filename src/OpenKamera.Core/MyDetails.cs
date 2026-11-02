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
            "Title or topic" or "Head of Product · Company" => Title.Trim(),
            _ => null,
        };
        if (whole is not null) return whole.Length == 0 ? null : (text == "YOUR NAME" ? whole.ToUpperInvariant() : whole);
        // Placeholders inside a longer line ("Golden hour with @yourname").
        if (At.Length > 0) text = text.Replace("@yourname", At).Replace("@yourhandle", At);
        return text.Replace("Your Name", Name.Trim());
    }

    /// <summary>True when the scene has template placeholders that the user's details would fill.</summary>
    public static bool HasPlaceholders(SceneDocument scene) =>
        scene.Layers.Any(l => l.Kind == "text" && (l.Text.Contains("Your Name") || l.Text.Contains("@your") || l.Text == "you@example.com"));
}
