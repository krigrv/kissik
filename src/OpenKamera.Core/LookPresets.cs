namespace OpenKamera.Core;

/// <summary>A saved whole look: enhance sliders, filter, LUT, background and mirror, recalled in one click.</summary>
public sealed record LookPreset(
    string Name, Dictionary<string, double> Enhance, FilterPreset Filter, string? LutName, bool Mirror, BackgroundSettings Background)
{
    public static LookPreset Capture(string name, AppSettings s) =>
        new(name, new Dictionary<string, double>(s.Enhance), s.Filter, s.LutName, s.Mirror, s.Background);

    /// <summary>Writes this look into the settings (the caller then reloads the UI from them).</summary>
    public void ApplyTo(AppSettings s)
    {
        s.Enhance = new Dictionary<string, double>(Enhance);
        s.Filter = Filter; s.LutName = LutName; s.Mirror = Mirror; s.Background = Background;
    }

    /// <summary>Adds or replaces (by name, ignoring case) and keeps the list sorted.</summary>
    public static void Upsert(List<LookPreset> list, LookPreset p)
    {
        list.RemoveAll(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(p);
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
    }
}
