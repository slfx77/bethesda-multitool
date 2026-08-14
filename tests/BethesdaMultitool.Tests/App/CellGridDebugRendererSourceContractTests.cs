using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     The cell-grid renderer is WINDOWS_GUI-only, so the platform-neutral suite pins its load-state
///     ordering at the source boundary.
/// </summary>
public sealed class CellGridDebugRendererSourceContractTests
{
    [Fact]
    public void LoadDataResetsWorldSpecificVerticalExtentBeforeAnyEmptyGridReturn()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "CellGridDebugRenderer12.cs");
        var load = SourceContract.Extract(
            source,
            "public void LoadData(\n        IEnumerable<CellRecord> exteriorCells,",
            "public int Render(");
        var reset = SourceContract.Extract(
            source,
            "private void ResetWorldZExtent()",
            "private void EnsureVertexCapacity(");

        SourceContract.AssertOrder(
            load,
            "_cellSize = spatialIndex?.CellSize ?? WorldGridConstants.CellSize;",
            "ResetWorldZExtent();",
            "if (_cellCount == 0) return;");
        SourceContract.AssertOrder(
            reset,
            "_zMin = 0f;",
            "_zMax = _cellSize;",
            "_horizontalLevels = 2;",
            "_verticesPerCell = 24;");
    }

    [Fact]
    public void BlendPreservesDestinationAlphaWithSourceOverComposition()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "CellGridDebugRenderer12.cs");
        var blend = SourceContract.Extract(
            source,
            "blend.RenderTarget[0] = new D12.RenderTargetBlendDescription",
            "var psoDesc = new GraphicsPipelineStateDescription");

        Assert.Contains("SourceBlend = D12.Blend.SourceAlpha", blend, StringComparison.Ordinal);
        Assert.Contains("DestinationBlend = D12.Blend.InverseSourceAlpha", blend, StringComparison.Ordinal);
        Assert.Contains("SourceBlendAlpha = D12.Blend.One", blend, StringComparison.Ordinal);
        Assert.Contains("DestinationBlendAlpha = D12.Blend.InverseSourceAlpha", blend, StringComparison.Ordinal);
        Assert.DoesNotContain("DestinationBlendAlpha = D12.Blend.Zero", blend, StringComparison.Ordinal);
    }
}
