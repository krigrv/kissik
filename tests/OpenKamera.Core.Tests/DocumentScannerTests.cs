using Windows.Data.Pdf;
using Windows.Storage;

namespace OpenKamera.Core.Tests;

public class DocumentScannerTests
{
    /// <summary>A white sheet photographed at an angle on a dark desk, with a black bar of "text" across it.</summary>
    private static (byte[] Px, PageQuad Truth) Photo(int w, int h)
    {
        var truth = new PageQuad((180, 90), (470, 110), (450, 400), (160, 370));
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool inside = Inside(truth, x, y);
                byte v = inside ? (byte)225 : (byte)45;
                if (inside && y > 230 && y < 245 && x > 230 && x < 400) v = 20; // a line of ink
                px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
            }
        return (px, truth);
    }

    private static bool Inside(PageQuad q, double x, double y)
    {
        (double X, double Y)[] p = [q.TL, q.TR, q.BR, q.BL];
        bool c = false;
        for (int i = 0, j = 3; i < 4; j = i++)
            if ((p[i].Y > y) != (p[j].Y > y) && x < (p[j].X - p[i].X) * (y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) c = !c;
        return c;
    }

    [Fact]
    public void FindsThePageCornersOnADarkDesk()
    {
        var (px, t) = Photo(640, 480);
        var q = DocumentScanner.FindPage(px, 640, 480);
        Assert.NotNull(q);
        foreach (var (a, b) in new[] { (q!.Value.TL, t.TL), (q.Value.TR, t.TR), (q.Value.BR, t.BR), (q.Value.BL, t.BL) })
            Assert.True(Math.Abs(a.X - b.X) < 8 && Math.Abs(a.Y - b.Y) < 8, $"corner {a} vs {b}");
    }

    [Fact]
    public void NoPageMeansNoQuad()
    {
        var px = new byte[320 * 240 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 90; px[i + 3] = 255; }
        Assert.Null(DocumentScanner.FindPage(px, 320, 240));
    }

    [Fact]
    public void FlattenGivesAStraightPageWithTheInkInside()
    {
        var (px, t) = Photo(640, 480);
        var (w, h) = DocumentScanner.PageSize(t, 600);
        var page = DocumentScanner.Flatten(px, 640, 480, t, w, h);
        Assert.True(page[(5 * w + 5) * 4] > 200 && page[((h - 6) * w + (w - 6)) * 4] > 200, "corners are paper, not desk");
        int dark = 0; for (int i = 0; i < page.Length; i += 4) if (page[i] < 60) dark++;
        Assert.InRange(dark, 1, w * h / 5); // the ink line survives, the desk is gone
    }

    [Fact]
    public void CleanLookEvensOutShadowsAndWhitensPaper()
    {
        int w = 200, h = 100; var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; byte v = (byte)(120 + x * 100 / w); px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; } // light falls off left
        DocumentScanner.Enhance(px, w, h, DocumentScanner.Look.Clean);
        Assert.True(px[(50 * w + 10) * 4] > 240 && px[(50 * w + 190) * 4] > 240, "both sides read as white paper");
    }

    [Fact]
    public async Task PdfOpensInWindowsWithEveryPage()
    {
        var bmp = new Windows.Graphics.Imaging.SoftwareBitmap(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, 64, 90, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, ms);
        enc.SetSoftwareBitmap(bmp); await enc.FlushAsync();
        var jpeg = new byte[ms.Size]; ms.Seek(0); await ms.AsStreamForRead().ReadExactlyAsync(jpeg);
        string path = Path.Combine(Path.GetTempPath(), $"kissik-scan-{Guid.NewGuid():N}.pdf");
        using (var f = File.Create(path)) DocumentScanner.WritePdf(f, [(jpeg, 64, 90), (jpeg, 64, 90)]);
        var doc = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        Assert.Equal(2u, doc.PageCount);
        File.Delete(path);
    }
}
