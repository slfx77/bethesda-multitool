using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerAlphaToCoveragePolicyTests
{
    [Theory]
    [InlineData(false, null, (int)BethesdaViewerAlphaToCoverageMode.BlendFallbackSingleSample)]
    [InlineData(false, "0", (int)BethesdaViewerAlphaToCoverageMode.BlendFallbackSingleSample)]
    [InlineData(true, null, (int)BethesdaViewerAlphaToCoverageMode.Hardware)]
    [InlineData(true, "1", (int)BethesdaViewerAlphaToCoverageMode.Hardware)]
    [InlineData(true, "0", (int)BethesdaViewerAlphaToCoverageMode.BlendFallbackDisabled)]
    public void ResolveKeepsSceneMsaaIndependentFromTheViewerOnlyOverride(
        bool pipelineAvailable,
        string? overrideValue,
        int expected)
    {
        Assert.Equal((BethesdaViewerAlphaToCoverageMode)expected, BethesdaViewerAlphaToCoveragePolicy.Resolve(
            pipelineAvailable,
            overrideValue));
    }

    [Fact]
    public void FallbackDescriptionsDistinguishSingleSampleFromExplicitDisable()
    {
        Assert.Equal(
            "the scene target is single-sampled",
            BethesdaViewerAlphaToCoveragePolicy.DescribeFallback(
                BethesdaViewerAlphaToCoverageMode.BlendFallbackSingleSample));
        Assert.Equal(
            "native A2C is disabled by FALLOUT_VIEWER_NATIVE_A2C=0",
            BethesdaViewerAlphaToCoveragePolicy.DescribeFallback(
                BethesdaViewerAlphaToCoverageMode.BlendFallbackDisabled));
        Assert.Null(BethesdaViewerAlphaToCoveragePolicy.DescribeFallback(
            BethesdaViewerAlphaToCoverageMode.Hardware));
    }
}