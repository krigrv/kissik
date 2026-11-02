using System.Text;

namespace OpenKamera.Core;

/// <summary>A page's four corners in frame pixels: top-left, top-right, bottom-right, bottom-left.</summary>
public readonly record struct PageQuad((double X, double Y) TL, (double X, double Y) TR, (double X, double Y) BR, (double X, double Y) BL);

/// <summary>
/// Turns any camera into a document scanner: finds the page (a bright sheet against a darker background), flattens
/// it to a straight rectangle, and cleans it up so it looks scanned. Pure maths on BGRA frames; no UI.
/// </summary>
public static class DocumentScanner
{
    /// <summary>Finds the largest bright four-cornered region (the page), or null if there's no clear page.</summary>
    public static PageQuad? FindPage(byte[] bgra, int w, int h)
    {
        // Work small: ~320 px wide is plenty to find a sheet of paper.
        int step = Math.Max(1, w / 320), sw = w / step, sh = h / step;
        var g = new byte[sw * sh];
        var hist = new int[256];
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                int i = ((y * step) * w + x * step) * 4;
                byte v = (byte)((bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8);
                g[y * sw + x] = v; hist[v]++;
            }
        int t = Otsu(hist, sw * sh);
        // Largest bright connected region.
        var label = new int[g.Length]; int best = 0, bestSize = 0, id = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < g.Length; s++)
        {
            if (label[s] != 0 || g[s] <= t) continue;
            id++; int size = 0; label[s] = id; stack.Push(s);
            while (stack.Count > 0)
            {
                int p = stack.Pop(); size++;
                int px = p % sw, py = p / sw;
                void Visit(int q) { if (label[q] == 0 && g[q] > t) { label[q] = id; stack.Push(q); } }
                if (px > 0) Visit(p - 1); if (px < sw - 1) Visit(p + 1);
                if (py > 0) Visit(p - sw); if (py < sh - 1) Visit(p + sw);
            }
            if (size > bestSize) { bestSize = size; best = id; }
        }
        // A page fills a fair part of the view but not the whole of it (then there's no background to find edges against).
        if (bestSize < g.Length * 0.08 || bestSize > g.Length * 0.97) return null;
        // Corners = the region's extreme points along the two diagonals.
        (int X, int Y) tl = default, tr = default, br = default, bl = default;
        int mTl = int.MaxValue, mBr = int.MinValue, mTr = int.MinValue, mBl = int.MaxValue;
        for (int p = 0; p < g.Length; p++)
        {
            if (label[p] != best) continue;
            int x = p % sw, y = p / sw;
            if (x + y < mTl) { mTl = x + y; tl = (x, y); }
            if (x + y > mBr) { mBr = x + y; br = (x, y); }
            if (x - y > mTr) { mTr = x - y; tr = (x, y); }
            if (x - y < mBl) { mBl = x - y; bl = (x, y); }
        }
        (double, double) S((int X, int Y) c) => ((c.X + 0.5) * step, (c.Y + 0.5) * step);
        var quad = new PageQuad(S(tl), S(tr), S(br), S(bl));
        // Reject shapes that aren't page-like (very thin, or corners collapsed onto each other).
        double top = Dist(quad.TL, quad.TR), bottom = Dist(quad.BL, quad.BR), left = Dist(quad.TL, quad.BL), right = Dist(quad.TR, quad.BR);
        if (Math.Min(Math.Min(top, bottom), Math.Min(left, right)) < Math.Min(w, h) * 0.12) return null;
        return quad;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static int Otsu(int[] hist, int total)
    {
        double sum = 0; for (int i = 0; i < 256; i++) sum += i * hist[i];
        double sumB = 0, best = -1; int wB = 0, t = 128;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i]; if (wB == 0) continue;
            int wF = total - wB; if (wF == 0) break;
            sumB += i * hist[i];
            double mB = sumB / wB, mF = (sum - sumB) / wF, between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; t = i; }
        }
        return t;
    }

    /// <summary>The output size for a page: keeps the sheet's proportions, long side <paramref name="longSide"/> px.</summary>
    public static (int W, int H) PageSize(PageQuad q, int longSide = 2000)
    {
        double w = (Dist(q.TL, q.TR) + Dist(q.BL, q.BR)) / 2, h = (Dist(q.TL, q.BL) + Dist(q.TR, q.BR)) / 2;
        double s = longSide / Math.Max(w, h);
        return (Math.Max(16, (int)(w * s)), Math.Max(16, (int)(h * s)));
    }

    /// <summary>Flattens the page to a straight outW×outH image (perspective correction, bilinear sampling).</summary>
    public static byte[] Flatten(byte[] bgra, int w, int h, PageQuad q, int outW, int outH)
    {
        var H = Homography(outW, outH, q);
        var dst = new byte[outW * outH * 4];
        Parallel.For(0, outH, y =>
        {
            for (int x = 0; x < outW; x++)
            {
                double d = H[6] * x + H[7] * y + 1, sx = (H[0] * x + H[1] * y + H[2]) / d, sy = (H[3] * x + H[4] * y + H[5]) / d;
                int o = (y * outW + x) * 4;
                if (sx < 0 || sy < 0 || sx > w - 1 || sy > h - 1) { dst[o] = dst[o + 1] = dst[o + 2] = 255; dst[o + 3] = 255; continue; }
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
                double fx = sx - x0, fy = sy - y0;
                for (int c = 0; c < 3; c++)
                {
                    double a = bgra[(y0 * w + x0) * 4 + c] * (1 - fx) + bgra[(y0 * w + x1) * 4 + c] * fx;
                    double b = bgra[(y1 * w + x0) * 4 + c] * (1 - fx) + bgra[(y1 * w + x1) * 4 + c] * fx;
                    dst[o + c] = (byte)Math.Clamp(a * (1 - fy) + b * fy, 0, 255);
                }
                dst[o + 3] = 255;
            }
        });
        return dst;
    }

    /// <summary>Homography from output rectangle (0,0)-(W,H) to the quad in the frame (8 coefficients, h33 = 1).</summary>
    internal static double[] Homography(int W, int H, PageQuad q)
    {
        (double, double)[] src = [(0, 0), (W - 1, 0), (W - 1, H - 1), (0, H - 1)];
        (double X, double Y)[] dstP = [q.TL, q.TR, q.BR, q.BL];
        var A = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = src[i]; var (u, v) = dstP[i];
            double[] r1 = [x, y, 1, 0, 0, 0, -u * x, -u * y, u], r2 = [0, 0, 0, x, y, 1, -v * x, -v * y, v];
            for (int j = 0; j < 9; j++) { A[2 * i, j] = r1[j]; A[2 * i + 1, j] = r2[j]; }
        }
        // Gaussian elimination with partial pivoting.
        for (int c = 0; c < 8; c++)
        {
            int p = c; for (int r = c + 1; r < 8; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[p, c])) p = r;
            for (int j = 0; j < 9; j++) (A[c, j], A[p, j]) = (A[p, j], A[c, j]);
            for (int r = 0; r < 8; r++)
            {
                if (r == c || A[c, c] == 0) continue;
                double f = A[r, c] / A[c, c];
                for (int j = c; j < 9; j++) A[r, j] -= f * A[c, j];
            }
        }
        var hcoef = new double[8];
        for (int i = 0; i < 8; i++) hcoef[i] = A[i, i] == 0 ? 0 : A[i, 8] / A[i, i];
        return hcoef;
    }

    public enum Look { Color, Clean, BlackWhite }

    /// <summary>
    /// "Scanned" look: removes uneven lighting and shadows by dividing by a heavily blurred copy (the paper's own
    /// brightness), then whitens the paper. BlackWhite also turns ink to pure black for crisp text.
    /// </summary>
    public static void Enhance(byte[] px, int w, int h, Look look)
    {
        if (look == Look.Color) return;
        int step = 8, bw = (w + step - 1) / step, bh = (h + step - 1) / step;
        var bg = new float[bw * bh];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                float mx = 0; // brightest nearby value ≈ paper colour (ink is darker)
                for (int yy = y * step; yy < Math.Min(h, y * step + step); yy++)
                    for (int xx = x * step; xx < Math.Min(w, x * step + step); xx++)
                    {
                        int i = (yy * w + xx) * 4; mx = Math.Max(mx, (px[i] + px[i + 1] + px[i + 2]) / 3f);
                    }
                bg[y * bw + x] = mx;
            }
        bg = BoxSmall(bg, bw, bh, 3);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float paper = Math.Max(24, bg[Math.Min(bh - 1, y / step) * bw + Math.Min(bw - 1, x / step)]);
                if (look == Look.BlackWhite)
                {
                    float l = (px[i] + px[i + 1] + px[i + 2]) / 3f / paper;
                    byte v = l < 0.72f ? (byte)0 : (byte)255;
                    px[i] = px[i + 1] = px[i + 2] = v;
                }
                else
                    for (int c = 0; c < 3; c++)
                    {
                        float n = px[i + c] / paper; // 1 = paper
                        n = n > 0.88f ? 1 : Math.Clamp((n - 0.25f) / 0.63f, 0, 1); // white paper, deeper ink
                        px[i + c] = (byte)(n * 255);
                    }
            }
        });
    }

    private static float[] BoxSmall(float[] s, int w, int h, int r)
    {
        var d = new float[s.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0; int n = 0;
                for (int yy = Math.Max(0, y - r); yy <= Math.Min(h - 1, y + r); yy++)
                    for (int xx = Math.Max(0, x - r); xx <= Math.Min(w - 1, x + r); xx++) { sum += s[yy * w + xx]; n++; }
                d[y * w + x] = sum / n;
            }
        return d;
    }

    /// <summary>Writes pages (JPEG bytes with their pixel sizes) into a PDF, one page each, A4-proportioned media box.</summary>
    public static void WritePdf(Stream output, IReadOnlyList<(byte[] Jpeg, int W, int H)> pages)
    {
        var offsets = new List<long>();
        var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        void Text(string s) => w.Write(Encoding.ASCII.GetBytes(s));
        void Obj(Action body) { offsets.Add(output.Position); Text($"{offsets.Count} 0 obj\n"); body(); Text("\nendobj\n"); }
        Text("%PDF-1.4\n");
        int n = pages.Count; // objects: 1 catalog, 2 pages, then per page: page, content, image
        Obj(() => Text("<< /Type /Catalog /Pages 2 0 R >>"));
        Obj(() => Text($"<< /Type /Pages /Count {n} /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + i * 3} 0 R"))}] >>"));
        for (int i = 0; i < n; i++)
        {
            var (jpeg, pw, ph) = pages[i];
            double ptW = 595, ptH = 595.0 * ph / pw; // width of A4 in points, height keeps the page's shape
            int page = 3 + i * 3, content = page + 1, image = page + 2;
            string draw = $"q {ptW:0.##} 0 0 {ptH:0.##} 0 0 cm /Im0 Do Q";
            Obj(() => Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {ptW:0.##} {ptH:0.##}] /Resources << /XObject << /Im0 {image} 0 R >> >> /Contents {content} 0 R >>"));
            Obj(() => { Text($"<< /Length {draw.Length} >>\nstream\n{draw}\nendstream"); });
            Obj(() => { Text($"<< /Type /XObject /Subtype /Image /Width {pw} /Height {ph} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n"); w.Write(jpeg); Text("\nendstream"); });
        }
        long xref = output.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Text($"{o:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        w.Flush();
    }
}
