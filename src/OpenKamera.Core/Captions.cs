namespace OpenKamera.Core;

/// <summary>
/// Live caption text: keeps the finished sentences plus the words being spoken now, shows the last two lines,
/// and clears itself after a few seconds of silence.
/// </summary>
public sealed class CaptionBuffer
{
    private readonly List<string> _final = [];
    private string _partial = "";
    private DateTime _lastSpeech = DateTime.MinValue;

    public int MaxCharsPerLine { get; set; } = 42;
    public TimeSpan ClearAfter { get; set; } = TimeSpan.FromSeconds(4);

    public void Partial(string text, DateTime now) { _partial = text.Trim(); _lastSpeech = now; }

    public void Final(string text, DateTime now)
    {
        _partial = "";
        if (!string.IsNullOrWhiteSpace(text)) _final.Add(text.Trim());
        if (_final.Count > 6) _final.RemoveRange(0, _final.Count - 6);
        _lastSpeech = now;
    }

    /// <summary>Replaces a finished line with its more accurate version, if it is still on screen.</summary>
    public void Correct(string draft, string corrected)
    {
        int i = _final.FindLastIndex(l => l == draft.Trim());
        if (i >= 0) _final[i] = corrected.Trim();
    }

    /// <summary>The caption to show now: at most two lines, newest words last; empty after silence.</summary>
    public string Text(DateTime now)
    {
        if (now - _lastSpeech > ClearAfter) { _final.Clear(); _partial = ""; return ""; }
        var words = string.Join(' ', _final.Append(_partial)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = Wrap(words, MaxCharsPerLine);
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Count - 2)));
    }

    internal static List<string> Wrap(IEnumerable<string> words, int max)
    {
        var lines = new List<string>(); var cur = "";
        foreach (var w in words)
        {
            if (cur.Length > 0 && cur.Length + 1 + w.Length > max) { lines.Add(cur); cur = w; }
            else cur = cur.Length == 0 ? w : cur + " " + w;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }
}

public static class CaptionBurn
{
    /// <summary>Blends a premultiplied BGRA image onto the frame, centred horizontally, <paramref name="bottomMargin"/> from the bottom.</summary>
    public static void Blend(byte[] dst, int w, int h, byte[] src, int sw, int sh, double bottomMargin = 0.06)
    {
        int x0 = (w - sw) / 2, y0 = h - sh - (int)(h * bottomMargin);
        for (int y = 0; y < sh; y++)
        {
            int dy = y0 + y; if (dy < 0 || dy >= h) continue;
            for (int x = 0; x < sw; x++)
            {
                int dx = x0 + x; if (dx < 0 || dx >= w) continue;
                int si = (y * sw + x) * 4, di = (dy * w + dx) * 4;
                int a = src[si + 3]; if (a == 0) continue;
                int ia = 255 - a;
                dst[di] = (byte)(src[si] + dst[di] * ia / 255);
                dst[di + 1] = (byte)(src[si + 1] + dst[di + 1] * ia / 255);
                dst[di + 2] = (byte)(src[si + 2] + dst[di + 2] * ia / 255);
            }
        }
    }
}
