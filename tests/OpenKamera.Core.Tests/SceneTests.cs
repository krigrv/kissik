using System.IO.Compression;

namespace OpenKamera.Core.Tests;

public class SceneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OpenKameraScenes-" + Guid.NewGuid().ToString("N")[..6]);
    public SceneTests() => SceneLibrary.RootOverride = _root;
    public void Dispose()
    {
        SceneLibrary.RootOverride = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static byte[] Solid(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; }
        return px;
    }

    [Fact]
    public void Templates_HaveUniqueIdsAndAreInsideTheCanvas()
    {
        Assert.Equal(SceneDocument.Templates.Count, SceneDocument.Templates.Select(t => t.Id).Distinct().Count());
        foreach (var t in SceneDocument.Templates)
            foreach (var l in t.Layers)
            {
                // Soft glows may bleed past the edge on purpose; every layer must still overlap the canvas.
                Assert.True(l.X < 1 && l.Y < 1 && l.X + l.W > 0 && l.Y + l.H > 0, $"{t.Id}: {l.Name} is off the canvas");
                Assert.True(l.X >= -0.5 && l.Y >= -0.5 && l.X + l.W <= 1.5 && l.Y + l.H <= 1.5, $"{t.Id}: {l.Name} is far outside the canvas");
            }
    }

    [Fact]
    public void CameraLayers_InTemplatesAreCircularOnA16x9Canvas()
    {
        foreach (var t in SceneDocument.Templates.Where(t => t.Layers.Any(l => l.Kind == "camera" && l.Shape == "ellipse")))
        {
            var c = t.Camera;
            Assert.Equal(c.W * 1280, c.H * 720, 1);
        }
    }

    [Fact]
    public void Json_RoundTripsAllLayerFields()
    {
        var s = SceneDocument.Templates.Single(t => t.Id == "presentation").Clone();
        var copy = SceneDocument.FromJson(s.ToJson());
        Assert.Equal(s.Layers.Count, copy.Layers.Count);
        Assert.Equal("ellipse", copy.Camera.Shape);
        Assert.Equal(s.Background2, copy.Background2);
    }

    [Fact]
    public void Library_EditedTemplateOverridesAndResets()
    {
        var s = SceneLibrary.Load("lower-third")!;
        s.Layers.Single(l => l.Name == "Name").Text = "Krishna";
        SceneLibrary.Save(s);
        Assert.Equal("Krishna", SceneLibrary.Load("lower-third")!.Layers.Single(l => l.Name == "Name").Text);
        Assert.True(SceneLibrary.HasEdits("lower-third"));
        SceneLibrary.ResetOrDelete("lower-third");
        Assert.Equal("Your Name", SceneLibrary.Load("lower-third")!.Layers.Single(l => l.Name == "Name").Text);
        // Templates are never mutated by edits.
        Assert.Equal("Your Name", SceneDocument.Templates.Single(t => t.Id == "lower-third").Layers.Single(l => l.Name == "Name").Text);
    }

    [Fact]
    public void Library_ExportImportCarriesAssets()
    {
        var s = SceneLibrary.CreateBlank("Logo test");
        string img = Path.Combine(_root, "logo.png");
        File.WriteAllBytes(img, [0x89, 0x50, 0x4E, 0x47]);
        s.Layers.Add(new SceneLayer { Kind = "image", Source = SceneLibrary.AddAsset(s, img) });
        SceneLibrary.Save(s);
        string zip = Path.Combine(_root, "test.okscene");
        SceneLibrary.Export(s, zip);

        var imported = SceneLibrary.Import(zip);
        Assert.NotEqual(s.Id, imported.Id);
        Assert.Equal("Logo test", imported.Name);
        Assert.NotNull(imported.ResolveAsset(imported.Layers[0]));
        Assert.Contains(SceneLibrary.All(), x => x.Id == imported.Id);
    }

    [Fact]
    public void Import_RejectsZipSlip()
    {
        string zip = Path.Combine(_root, "evil.okscene");
        Directory.CreateDirectory(_root);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("scene.json").Open())) w.Write(new SceneDocument { Name = "x" }.ToJson());
            using (var w = new StreamWriter(z.CreateEntry("assets/../../evil.txt").Open())) w.Write("x");
        }
        Assert.Throws<InvalidDataException>(() => SceneLibrary.Import(zip));
    }

    [Fact]
    public void Compose_FullCanvasCameraKeepsCameraAndBlendsOverlay()
    {
        int w = 4, h = 2;
        var cam = Solid(w, h, 0, 0, 200);
        var overlay = new byte[w * h * 4];
        overlay[0] = 255; overlay[3] = 255; // opaque blue top-left
        var outPx = SceneCompositor.Compose(cam, w, h, SceneDocument.Templates[0], overlay);
        Assert.Equal(255, outPx[0]);       // overlay pixel
        Assert.Equal(200, outPx[4 + 2]);   // camera elsewhere
    }

    [Fact]
    public void Compose_CircleCameraSitsOnBackgroundWithCornersClipped()
    {
        int w = 160, h = 90;
        var scene = new SceneDocument
        {
            Background = "#FF00FF00",
            Layers = [new SceneLayer { Kind = "camera", X = 0.25, Y = 0, W = 0.5625, H = 1, Shape = "ellipse" }],
        };
        var outPx = SceneCompositor.Compose(Solid(w, h, 0, 0, 255), w, h, scene, null);
        int centre = (45 * w + 80) * 4, corner = 0, rectCorner = (1 * w + 41) * 4;
        Assert.Equal(255, outPx[centre + 2]);     // red camera in the middle
        Assert.Equal(255, outPx[corner + 1]);     // green background outside
        Assert.True(outPx[rectCorner + 2] < 60);  // inside the box but outside the circle → background
    }

    [Fact]
    public void Compose_RemovedBackgroundShowsSceneColour()
    {
        int w = 2, h = 1;
        var cam = Solid(w, h, 0, 0, 0, 0); // fully transparent (Remove mode)
        var scene = new SceneDocument { Background = "#FF0000FF" };
        var outPx = SceneCompositor.Compose(cam, w, h, scene, null);
        Assert.Equal(255, outPx[0]); Assert.Equal(255, outPx[3]);
    }

    [Theory]
    [InlineData("#FF2EA3F2", 255, 46, 163, 242)]
    [InlineData("#2EA3F2", 255, 46, 163, 242)]
    [InlineData("#80000000", 128, 0, 0, 0)]
    public void ColorHex_Parses(string hex, int a, int r, int g, int b) =>
        Assert.Equal(((byte)a, (byte)r, (byte)g, (byte)b), ColorHex.Parse(hex));
}
