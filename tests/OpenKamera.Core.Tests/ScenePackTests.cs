namespace OpenKamera.Core.Tests;

public class ScenePackTests
{
    private static List<SceneDocument> Pack => SceneDocument.Templates.Where(t => t.Id.StartsWith("pack-")).ToList();

    [Fact]
    public void HundredScenes_TenPerCategory()
    {
        Assert.Equal(100, Pack.Count);
        Assert.All(Pack.GroupBy(s => s.Category), g => Assert.Equal(10, g.Count()));
        Assert.Equal(Pack.Count, Pack.Select(s => s.Id).Distinct().Count());
        Assert.Equal(SceneDocument.Templates.Count, SceneDocument.Templates.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void EveryLayerSitsOnTheCanvas()
    {
        foreach (var s in Pack)
            foreach (var l in s.Layers.Where(l => l.Kind != "camera" || l.Visible))
            {
                bool decorative = l.Kind == "shape" && l.Opacity < 0.5; // soft glows may bleed off the edge on purpose
                if (decorative) continue;
                Assert.True(l.X >= -0.001 && l.Y >= -0.001 && l.X + l.W <= 1.001 && l.Y + l.H <= 1.001, $"{s.Name}: {l.Name} off canvas");
            }
    }

    [Fact]
    public void TextIsBigEnoughToRead() =>
        Assert.All(Pack.SelectMany(s => s.Layers.Where(l => l.Kind == "text").Select(l => (s.Name, l))),
            p => Assert.True(p.l.FontSize >= 0.028, $"{p.Name}: {p.l.Name} is too small"));
}
