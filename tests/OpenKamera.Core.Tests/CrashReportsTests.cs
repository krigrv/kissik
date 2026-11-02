using System.Text.Json;

namespace OpenKamera.Core.Tests;

public class CrashReportsTests
{
    [Fact]
    public void ScrubRemovesUserNamesEmailsAndMachineName()
    {
        string s = CrashReports.Scrub($@"at C:\Users\Krishna Gaurav\AppData\x.cs line 4 by someone@example.com on {Environment.MachineName}");
        Assert.DoesNotContain("Krishna", s);
        Assert.Contains(@"C:\Users\<user>\AppData", s);
        Assert.Contains("<email>", s);
        // Very short PC names (2 letters) are left alone: replacing them would mangle ordinary words.
        if (Environment.MachineName.Length > 2) Assert.DoesNotContain(Environment.MachineName, s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitsTheLogIntoCrashes()
    {
        var log = "2026-11-02T10:00:00.0000000+05:30 System.InvalidOperationException: a\n   at X\n\n2026-11-02T11:00:00.0000000+05:30 System.NullReferenceException: b\n";
        Assert.Equal(2, CrashReports.SplitEntries(log).Count);
    }

    [Fact]
    public void EnvelopeHasSentryShapeAndTheExceptionType()
    {
        string dsn = "https://abc123@o1.ingest.sentry.io/42";
        Assert.True(CrashReports.TryParseDsn(dsn, out var ep, out var key, out var project));
        Assert.Equal("https://o1.ingest.sentry.io/api/42/envelope/", ep.ToString());
        Assert.Equal(("abc123", "42"), (key, project));
        var lines = CrashReports.BuildEnvelope(dsn, "2026-11-02T10:00:00 System.InvalidOperationException: Camera gone\n   at Foo()", "1.0.0", "10.0.26200", DateTime.UtcNow).Split('\n');
        Assert.Equal("event", JsonDocument.Parse(lines[1]).RootElement.GetProperty("type").GetString());
        var evt = JsonDocument.Parse(lines[2]).RootElement;
        Assert.Equal("fatal", evt.GetProperty("level").GetString());
        var ex = evt.GetProperty("exception").GetProperty("values")[0];
        Assert.Equal("System.InvalidOperationException", ex.GetProperty("type").GetString());
        Assert.Equal("Camera gone", ex.GetProperty("value").GetString());
    }

    [Fact]
    public async Task NothingIsSentWhileNotSetUp()
    {
        Assert.False(CrashReports.IsConfigured);
        Assert.False(await CrashReports.SendAsync("anything", "1.0.0"));
    }
}
