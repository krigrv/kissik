namespace OpenKamera.Core.Tests;

public class MeetingCheckTests
{
    private static CheckInputs Good() => new(true, 30, new FaceBox(0.4, 0.25, 0.2, 0.3), 140, 120, true, 0.4, true, null);

    [Fact]
    public void AllGood_IsAllGood() => Assert.All(MeetingCheck.Run(Good()), r => Assert.Equal(CheckLevel.Good, r.Level));

    [Fact]
    public void Backlit_IsFlagged()
    {
        var r = MeetingCheck.Run(Good() with { FaceLuma = 90, ImageLuma = 180 });
        Assert.Contains(r, x => x.Item == "Light" && x.Level == CheckLevel.Warn && x.Message.Contains("backlit"));
    }

    [Fact]
    public void BusyCamera_NamesTheAppAndComesFirst()
    {
        var r = MeetingCheck.Run(Good() with { CameraConnected = false, CameraHeldBy = "Zoom" });
        Assert.Equal(CheckLevel.Bad, r[0].Level);
        Assert.Contains("Zoom", r[0].Message);
    }

    [Fact]
    public void SmallFace_SuggestsMovingCloser() =>
        Assert.Contains(MeetingCheck.Run(Good() with { Face = new FaceBox(0.45, 0.35, 0.06, 0.08) }), x => x.Message.Contains("closer"));

    [Fact]
    public void Luma_AveragesTheBox()
    {
        var px = new byte[10 * 10 * 4]; for (int i = 0; i < px.Length; i++) px[i] = 200;
        Assert.InRange(MeetingCheck.Luma(px, 10, 10, new FaceBox(0, 0, 1, 1)), 195, 201);
    }
}
