using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Pins the one-shot perspective capture's exact-dimension contract. Projection exports use
///     2048-pixel tiles, but a perspective PNG is a single target and must not inherit that tile
///     clamp behind the profiler's back.
/// </summary>
public sealed class WorldViewPerspectiveCaptureDimensionSourceContractTests
{
    [Fact]
    public void PerspectiveCaptureDoesNotInheritProjectionTileClamp()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        Assert.Contains("private const int PerspectiveCaptureMaxDimension = 16384;", source,
            StringComparison.Ordinal);

        var core = SourceContract.Extract(
            source,
            "private async Task<byte[]?> CaptureSceneCoreAsync",
            "private RendererProfilerScenarioSnapshot BuildProfilerScenarioSnapshot");

        Assert.Contains(
            "var w = Math.Clamp(pixelWidth, 1, PerspectiveCaptureMaxDimension);",
            core,
            StringComparison.Ordinal);
        Assert.Contains(
            "var h = Math.Clamp(pixelHeight, 1, PerspectiveCaptureMaxDimension);",
            core,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Export3DMaxTileDimension", core, StringComparison.Ordinal);
    }
}