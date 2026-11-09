using OpenKamera.Core;

namespace OpenKamera.Core.Tests;

/// <summary>Things that must hold when something goes wrong: settings survive, recordings survive.</summary>
[Collection("Timing")]
public class ReliabilityTests
{
    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "OpenKameraTests", "rel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void DamagedSettings_LoadTheLastGoodCopy_AndKeepTheDamagedFile()
    {
        string path = Path.Combine(Dir(), "settings.json");
        new AppSettings { ProfileName = "First" }.Save(path);
        new AppSettings { ProfileName = "Second" }.Save(path); // the first save is now the backup
        Assert.True(File.Exists(path + AppSettings.BackupSuffix));

        File.WriteAllText(path, "{ \"ProfileName\": \"Sec"); // a save cut off half-way

        var loaded = AppSettings.Load(path);
        Assert.True(loaded.RestoredFromBackup);
        Assert.Equal("First", loaded.ProfileName);
        Assert.True(File.Exists(path + ".damaged"), "the damaged file should be set aside, not lost");
    }

    [Fact]
    public void HealthySettings_AreNotMarkedAsRestored_AndLeaveNoTempFile()
    {
        string path = Path.Combine(Dir(), "settings.json");
        new AppSettings { ProfileName = "Mine" }.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.False(loaded.RestoredFromBackup);
        Assert.Equal("Mine", loaded.ProfileName);
        Assert.False(File.Exists(path + ".new"));
        Assert.DoesNotContain("RestoredFromBackup", File.ReadAllText(path));
    }

    [Fact]
    public void SettingsWithNothingReadable_FallBackToDefaults()
    {
        string path = Path.Combine(Dir(), "settings.json");
        File.WriteAllText(path, "not json at all");
        var loaded = AppSettings.Load(path);
        Assert.False(loaded.RestoredFromBackup);
        Assert.Equal(new AppSettings().ProfileName, loaded.ProfileName);
    }

    [Fact]
    public void ManySavesAtOnce_NeverLeaveABrokenFile()
    {
        string path = Path.Combine(Dir(), "settings.json");
        Parallel.For(0, 40, i => new AppSettings { ProfileName = "P" + i }.Save(path));
        Assert.StartsWith("P", AppSettings.Load(path).ProfileName);
    }

    [Fact]
    public void PhoneCertificate_IsKeptWhenThePcGainsANetworkAddress()
    {
        // Made when the PC had one address. A VPN or virtual switch adding another must not replace it: paired
        // phones trust this exact certificate and would all have to scan the code again.
        using var cert = PhoneServer.CreateCertificate(["192.168.1.20"]);
        Assert.True(PhoneServer.StillGood(cert, DateTime.Now));
        Assert.True(PhoneServer.StillGood(cert, DateTime.Now.AddYears(5)));
        Assert.False(PhoneServer.StillGood(cert, DateTime.Now.AddYears(10))); // run out: then a new one is right
    }

    private static async Task<string> RecordPieceAsync(string path, int frames = 20)
    {
        var rec = new CompositeRecorder(320, 180, 30);
        await rec.StartAsync(path);
        var frame = new byte[320 * 180 * 4];
        for (int f = 0; f < frames; f++)
        {
            Array.Fill(frame, (byte)(f * 8));
            rec.Push(frame, 320, 180, TimeSpan.FromSeconds(f / 30.0));
            await Task.Delay(8);
        }
        await rec.StopAsync();
        await rec.DisposeAsync();
        return path;
    }

    [Fact]
    public async Task Join_WithOnePlayablePieceAfterABrokenOne_SavesThePlayablePiece()
    {
        string dir = Dir(), parts = Path.Combine(dir, "rec.mp4" + SegmentedRecorder.PartsSuffix), output = Path.Combine(dir, "rec.mp4");
        Directory.CreateDirectory(parts);
        // The first piece is unreadable but too big to be thrown away as empty; only the second one plays.
        File.WriteAllBytes(Path.Combine(parts, "part0001.mp4"), new byte[4096]);
        await RecordPieceAsync(Path.Combine(parts, "part0002.mp4"));
        long good = new FileInfo(Path.Combine(parts, "part0002.mp4")).Length;

        Assert.True(await SegmentedRecorder.JoinAsync(parts, output));
        Assert.Equal(good, new FileInfo(output).Length); // used to be the 4096 broken bytes
        var video = await (await Windows.Storage.StorageFile.GetFileFromPathAsync(output)).Properties.GetVideoPropertiesAsync();
        Assert.True(video.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task StoppingWhileANewPieceIsOpening_LeavesNothingBehind()
    {
        for (int round = 0; round < 3; round++)
        {
            string dir = Dir(), path = Path.Combine(dir, "race.mp4");
            var rec = new SegmentedRecorder(320, 180, 30) { SegmentLength = TimeSpan.FromSeconds(1) };
            await rec.StartAsync(path);
            var frame = new byte[320 * 180 * 4];
            for (int f = 0; f <= 31; f++) { rec.Push(frame, 320, 180, TimeSpan.FromSeconds(f / 30.0)); await Task.Delay(8); }
            await rec.StopAsync(); // straight after the frame that starts the piece change
            await rec.DisposeAsync(); // used to run a second stop and throw "Nothing was recorded"
            Assert.True(File.Exists(path));
            Assert.False(Directory.Exists(path + SegmentedRecorder.PartsSuffix), "pieces folder left behind");
        }
    }

    [Fact]
    public async Task FramesArrivingWhileAStreamStops_AreDroppedQuietly()
    {
        if (LiveStream.FindFfmpeg() is null) return; // FFmpeg not installed on this machine
        string outFile = Path.Combine(Dir(), "stop.flv");
        var s = new LiveStream(320, 180, 30);
        await s.StartAsync([outFile], withAudio: true);
        var frame = new byte[320 * 180 * 4];
        using var stop = new CancellationTokenSource();
        Exception? thrown = null;
        // A frame thread that keeps pushing right through the stop, as the camera does.
        var pusher = Task.Run(() =>
        {
            try { while (!stop.IsCancellationRequested) { s.Push(frame, 320, 180); s.PushAudio(new float[960]); } }
            catch (Exception ex) { thrown = ex; }
        });
        await Task.Delay(400);
        await s.StopAsync();
        await Task.Delay(100);
        stop.Cancel();
        await pusher;
        Assert.Null(thrown);
    }
}
