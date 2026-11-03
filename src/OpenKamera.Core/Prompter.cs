namespace OpenKamera.Core;

public sealed class PrompterSettings
{
    public string Script { get; set; } = "Paste your notes or script here.\n\nThey scroll just under your webcam, so you read while looking almost straight at the lens.\n\nSpace pauses, ↑ ↓ change speed, + − change size.";
    public int WordsPerMinute { get; set; } = 140;
    public double FontSize { get; set; } = 26;
    public double Width { get; set; } = 560;
    public double Height { get; set; } = 150;
    public double Opacity { get; set; } = 0.92;
    public bool Mirror { get; set; }
}

/// <summary>Scroll maths for the prompter: words per minute → pixels per second for the current text layout.</summary>
public static class Prompter
{
    public const int MinWpm = 60, MaxWpm = 260;

    /// <summary>
    /// Average words on a line ≈ line width / (font size × 0.5 per char × 6 chars per word incl. space);
    /// lines per second = wpm / 60 / words per line; pixels per second = that × line height (1.35 × font size).
    /// </summary>
    public static double PixelsPerSecond(int wpm, double fontSize, double lineWidth)
    {
        double charW = fontSize * 0.5, wordsPerLine = Math.Max(1, lineWidth / (charW * 6));
        double linesPerSecond = Math.Clamp(wpm, MinWpm, MaxWpm) / 60.0 / wordsPerLine;
        return linesPerSecond * fontSize * 1.35;
    }

    public static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Reading time at the chosen speed, for the "about 2 min" hint.</summary>
    public static TimeSpan ReadingTime(string text, int wpm) => TimeSpan.FromMinutes(WordCount(text) / (double)Math.Clamp(wpm, MinWpm, MaxWpm));
}
