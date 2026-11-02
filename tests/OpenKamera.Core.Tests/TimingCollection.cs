namespace OpenKamera.Core.Tests;

/// <summary>Speed and GPU tests run one at a time: run alongside each other they fight for the GPU and CPU and fail
/// on timing alone.</summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public sealed class TimingCollection;
