namespace OpenKamera.Core.Tests;

public class PrompterTests
{
    [Fact]
    public void FasterSpeechScrollsFaster() =>
        Assert.True(Prompter.PixelsPerSecond(200, 26, 560) > Prompter.PixelsPerSecond(120, 26, 560));

    [Fact]
    public void WiderLinesScrollSlower() =>
        Assert.True(Prompter.PixelsPerSecond(140, 26, 900) < Prompter.PixelsPerSecond(140, 26, 400));

    [Fact]
    public void SpeedIsClamped() =>
        Assert.Equal(Prompter.PixelsPerSecond(Prompter.MaxWpm, 26, 560), Prompter.PixelsPerSecond(999, 26, 560));

    [Fact]
    public void ReadingTime_CountsWords() =>
        Assert.Equal(1, Prompter.ReadingTime(string.Join(' ', Enumerable.Repeat("word", 140)), 140).TotalMinutes, 6);
}
