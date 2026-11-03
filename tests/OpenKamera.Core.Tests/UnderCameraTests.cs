namespace OpenKamera.Core.Tests;

public class UnderCameraTests
{
    private static SceneDocument Polaroid() => new()
    {
        Id = "t", Name = "Print", Background = "#FF000000",
        Layers =
        [
            new() { Id = "card", Kind = "shape", X = 0.25, Y = 0.05, W = 0.5, H = 0.9, Color = "#FFFFFFFF" },
            new() { Id = "cam", Kind = "camera", X = 0.3, Y = 0.1, W = 0.4, H = 0.6 },
            new() { Id = "caption", Kind = "text", Text = "Hi", X = 0.3, Y = 0.75, W = 0.4, H = 0.08 },
        ],
    };

    [Fact]
    public void LayersBeforeTheCameraGoUnderIt()
    {
        Assert.Equal(["card"], SceneCompositor.UnderCameraIds(Polaroid()));
    }

    [Fact]
    public void NothingIsUnderAFullScreenCamera()
    {
        var s = Polaroid();
        s.Layers[1].X = 0; s.Layers[1].Y = 0; s.Layers[1].W = 1; s.Layers[1].H = 1;
        Assert.Empty(SceneCompositor.UnderCameraIds(s));
    }

    [Fact]
    public void CameraIsDrawnOverTheCard()
    {
        const int w = 64, h = 36;
        var cam = new byte[w * h * 4];
        for (int i = 0; i < cam.Length; i += 4) { cam[i] = 0; cam[i + 1] = 0; cam[i + 2] = 255; cam[i + 3] = 255; } // red camera
        var under = new byte[w * h * 4];
        for (int i = 0; i < under.Length; i += 4) { under[i] = under[i + 1] = under[i + 2] = 255; under[i + 3] = 255; } // white card everywhere
        var output = SceneCompositor.Compose(cam, w, h, Polaroid(), overlay: null, under: under);
        int mid = ((h / 2) * w + w / 2) * 4; // centre of the camera box
        Assert.Equal(255, output[mid + 2]); // red: the camera, not the white card
        Assert.True(output[mid] < 40);
    }
}
