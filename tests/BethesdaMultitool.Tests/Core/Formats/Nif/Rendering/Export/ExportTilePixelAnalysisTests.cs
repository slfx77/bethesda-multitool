using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class ExportTilePixelAnalysisTests
{
    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0 })]
    public void IsTransparentClear_RejectsIncompleteBuffers(byte[] bgra) =>
        Assert.False(ExportTilePixelAnalysis.IsTransparentClear(bgra));

    [Fact]
    public void IsTransparentClear_AcceptsTransparentBlackPixels() =>
        Assert.True(ExportTilePixelAnalysis.IsTransparentClear(
            [0, 0, 0, 0, 0, 0, 0, 0]));

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 255 })]
    [InlineData(new byte[] { 40, 80, 120, 255, 40, 80, 120, 255 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 1, 0 })]
    public void IsTransparentClear_RejectsUniformOrNonuniformRenderedPixels(byte[] bgra) =>
        Assert.False(ExportTilePixelAnalysis.IsTransparentClear(bgra));
}
