using Microsoft.ML.OnnxRuntime;
using Xunit.Abstractions;

namespace OpenKamera.Core.Tests;

/// <summary>Prints the bundled models' input/output signatures (diagnostic).</summary>
public class ModelProbeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("selfie_segmentation_fp16.onnx")]
    [InlineData("modnet_fp16.onnx")]
    public void Model_LoadsAndReportsSignature(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Models", file);
        Assert.True(File.Exists(path), $"missing {path}");
        using var session = new InferenceSession(path);
        foreach (var (name, meta) in session.InputMetadata)
            output.WriteLine($"{file} IN  {name}: {meta.ElementDataType} [{string.Join(",", meta.Dimensions)}]");
        foreach (var (name, meta) in session.OutputMetadata)
            output.WriteLine($"{file} OUT {name}: {meta.ElementDataType} [{string.Join(",", meta.Dimensions)}]");
    }
}
