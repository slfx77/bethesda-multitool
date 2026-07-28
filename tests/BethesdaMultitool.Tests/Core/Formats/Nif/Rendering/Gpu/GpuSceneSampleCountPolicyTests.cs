using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class GpuSceneSampleCountPolicyTests
{
    [Theory]
    [InlineData("1")]
    [InlineData(" 1 ")]
    public void ExactSingleSampleOverrideForcesOne(string raw)
    {
        Assert.Equal(1, GpuDevice12.ResolveRequestedSceneSampleCount(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("4")]
    [InlineData("auto")]
    public void MissingOrUnsupportedOverrideRetainsFourSampleProbe(string? raw)
    {
        Assert.Equal(4, GpuDevice12.ResolveRequestedSceneSampleCount(raw));
    }

    [Fact]
    public void OverrideNameIsStableForProfilerFixtures()
    {
        Assert.Equal("FALLOUT_VIEWER_SCENE_SAMPLES", GpuDevice12.SceneSampleCountEnvironmentVariable);
    }

    [Fact]
    public void PerspectiveCaptureReportsTheActualTargetSampleCount()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");

        Assert.Contains("target.TonemapHistoryResetReason,", source, StringComparison.Ordinal);
        Assert.Contains("int sceneSampleCount)", source, StringComparison.Ordinal);
        Assert.Contains("SceneSampleCount: sceneSampleCount", source, StringComparison.Ordinal);
    }
}