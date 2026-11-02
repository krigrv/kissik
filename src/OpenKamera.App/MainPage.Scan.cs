using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenKamera.Core;
using Windows.Graphics.Imaging;

namespace OpenKamera_App;

// Document scanner: point any camera (webcam or phone) at a page. KISSIK finds its edges, straightens it, cleans it
// up like a scan, reads the text (Windows' built-in OCR, on this PC), and saves several pages as one PDF.
public sealed partial class MainPage
{
    private async void ScanDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _lastFrame is null) { Status("Connect a camera first, then point it at the page."); return; }
        var pages = new List<(byte[] Px, int W, int H)>();
        var look = DocumentScanner.Look.Clean;
        (byte[] Px, int W, int H)? current = null;

        var preview = new Image { Height = 340, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(preview, "Straightened page preview");
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = "Lay the page on a darker surface and fill most of the picture with it." };
        var count = new TextBlock { FontSize = 12, Text = "No pages yet" };
        var lookBox = new ComboBox { Header = "Look", ItemsSource = new[] { "Clean (scanned)", "Black and white", "Colour (as photographed)" }, SelectedIndex = 0, MinWidth = 200 };
        lookBox.SelectionChanged += (_, _) => look = lookBox.SelectedIndex switch { 1 => DocumentScanner.Look.BlackWhite, 2 => DocumentScanner.Look.Color, _ => DocumentScanner.Look.Clean };
        var copyText = new Button { Content = "Copy text" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(copyText, "Reads the text on the current page and copies it");
        copyText.Click += async (_, _) =>
        {
            if (current is not { } c) { Status("No page in view yet."); return; }
            string text = await ReadTextAsync(c.Px, c.W, c.H);
            if (text.Length == 0) { Status("No text found on this page."); return; }
            var d = new Windows.ApplicationModel.DataTransfer.DataPackage(); d.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(d);
            Status($"Copied {text.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length} words from the page.");
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; row.Children.Add(lookBox); row.Children.Add(copyText);
        var body = new StackPanel { Spacing = 10, Width = 520 };
        body.Children.Add(preview); body.Children.Add(hint); body.Children.Add(row); body.Children.Add(count);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Scan a document", Content = body,
            PrimaryButtonText = "Add page", SecondaryButtonText = "Save PDF", CloseButtonText = "Done", DefaultButton = ContentDialogButton.Primary,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 640.0;

        // Live preview: find and straighten the page a few times a second.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        bool busy = false;
        timer.Tick += async (_, _) =>
        {
            if (busy || _lastFrame is not { } f) return;
            busy = true;
            try
            {
                var lk = look;
                var result = await Task.Run(() =>
                {
                    if (DocumentScanner.FindPage(f.Px, f.W, f.H) is not { } q) return ((byte[], int, int)?)null;
                    var (pw, ph) = DocumentScanner.PageSize(q, 1600);
                    var page = DocumentScanner.Flatten(f.Px, f.W, f.H, q, pw, ph);
                    DocumentScanner.Enhance(page, pw, ph, lk);
                    return (page, pw, ph);
                });
                current = result;
                hint.Text = result is null ? "Looking for the page… lay it on a darker surface and fill most of the picture with it." : "Page found. Hold still and press Add page.";
                if (result is { } r) preview.Source = await ToImageAsync(r.Item1, r.Item2, r.Item3);
            }
            catch { /* skip a frame */ }
            finally { busy = false; }
        };
        timer.Start();

        dlg.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true; // stay open for the next page
            if (current is not { } c) { Status("No page found yet: lay it on a darker surface."); return; }
            pages.Add(c);
            count.Text = pages.Count == 1 ? "1 page" : $"{pages.Count} pages";
            Status($"Page {pages.Count} added.");
        };
        dlg.SecondaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (pages.Count == 0 && current is { } c) pages.Add(c);
                if (pages.Count == 0) { args.Cancel = true; Status("Add a page first."); return; }
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"Scan {DateTime.Now:yyyy-MM-dd HHmm}" };
                picker.FileTypeChoices.Add("PDF document", [".pdf"]);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                if (await picker.PickSaveFileAsync() is not { } file) { args.Cancel = true; return; }
                var jpegs = new List<(byte[], int, int)>();
                foreach (var p in pages) jpegs.Add((await ToJpegAsync(p.Px, p.W, p.H), p.W, p.H));
                using (var s = await file.OpenStreamForWriteAsync()) { s.SetLength(0); DocumentScanner.WritePdf(s, jpegs); }
                Status($"Saved {pages.Count} page{(pages.Count == 1 ? "" : "s")}: {file.Path}");
            }
            catch (Exception ex) { args.Cancel = true; Status("Couldn't save the PDF: " + ex.Message); }
            finally { deferral.Complete(); }
        };
        try { await dlg.ShowAsync(); }
        finally { timer.Stop(); }
    }

    private static SoftwareBitmap ToBitmap(byte[] px, int w, int h) =>
        SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);

    private static async Task<SoftwareBitmapSource> ToImageAsync(byte[] px, int w, int h)
    {
        var src = new SoftwareBitmapSource();
        await src.SetBitmapAsync(ToBitmap(px, w, h));
        return src;
    }

    private static async Task<byte[]> ToJpegAsync(byte[] px, int w, int h)
    {
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms,
            [new("ImageQuality", new BitmapTypedValue(0.88, Windows.Foundation.PropertyType.Single))]);
        enc.SetSoftwareBitmap(ToBitmap(px, w, h));
        await enc.FlushAsync();
        var bytes = new byte[ms.Size]; ms.Seek(0);
        await ms.AsStreamForRead().ReadExactlyAsync(bytes);
        return bytes;
    }

    /// <summary>Reads the page's text with Windows' built-in OCR (languages from the user's Windows settings), on this PC.</summary>
    private static async Task<string> ReadTextAsync(byte[] px, int w, int h)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return "";
        int max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        if (Math.Max(w, h) > max)
        {
            double s = (double)max / Math.Max(w, h);
            int nw = (int)(w * s), nh = (int)(h * s);
            px = OutputFit.Stretch(px, w, h, nw, nh).Pixels; (w, h) = (nw, nh);
        }
        var result = await engine.RecognizeAsync(ToBitmap(px, w, h));
        return string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
    }
}
