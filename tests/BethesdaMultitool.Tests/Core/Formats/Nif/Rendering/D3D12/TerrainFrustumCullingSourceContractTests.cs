using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Pins the WINDOWS_GUI-only propagation and call ordering around the portable culling math.
///     The behavioral geometry tests live in TerrainCellDrawCullingTests.
/// </summary>
public sealed class TerrainFrustumCullingSourceContractTests
{
    private static string Source(string fileName) => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", fileName);

    [Fact]
    public void ExactHeightBounds_PropagateThroughBothAsyncAndSyncCachePaths()
    {
        var builtData = Source("BuiltCellCpuData.cs");
        var cachedMesh = Source("CachedCellMesh12.cs");
        var builder = Source("TerrainCellCpuBuilder.cs");
        var renderer = Source("TerrainRenderer12.cs");

        Assert.Contains("TerrainCellHeightBounds HeightBounds", builtData, StringComparison.Ordinal);
        Assert.Contains("public required TerrainCellHeightBounds HeightBounds", cachedMesh, StringComparison.Ordinal);
        Assert.Contains("TerrainCellHeightBounds.FromVertices(vertices)", builder, StringComparison.Ordinal);
        Assert.Contains("HeightBounds = cpu.HeightBounds", renderer, StringComparison.Ordinal);
        Assert.Contains("TerrainCellHeightBounds.FromVertices(_vertexScratch)", renderer, StringComparison.Ordinal);
        Assert.Equal(2, SourceContract.CountOccurrences(renderer, "HeightBounds = "));
    }

    [Fact]
    public void MainPass_CullsOnlyAfterStreamingAndCacheResolution()
    {
        var renderer = Source("TerrainRenderer12.cs");
        var renderInternal = SourceContract.Extract(
            renderer,
            "private int RenderInternal(",
            "private static void BindCellGrid(");
        var localFunctionStart = renderInternal.IndexOf(
            "void TryDrawVisibleCell(VisibleCell vc)", StringComparison.Ordinal);
        Assert.True(localFunctionStart >= 0, "missing main-pass draw helper");
        var drawHelper = renderInternal[localFunctionStart..];

        Assert.Contains(
            "TerrainCellDrawCulling.CreateFrustum(viewProj, renderOrigin)",
            renderInternal,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            drawHelper,
            "GetOrUploadMesh(vc.Key, vc.Cell, ref uploadBudget)",
            "TerrainCellDrawCulling.ShouldDraw(drawFrustum, entry.Grid, entry.HeightBounds)",
            "cmd.IASetVertexBuffers(0, entry.VertexView())",
            "DrawCell(cmd, entry)");
    }

    [Fact]
    public void CameraRelativeLiveAndCaptureCalls_PassTheirExactRenderOrigin()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");

        Assert.Contains("_terrain?.Render(viewProjScene, cylinder, sceneRenderOrigin)", frame,
            StringComparison.Ordinal);
        Assert.Contains("_terrain!.Render(viewProj, cylinder, captureRenderOrigin)", capture,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShadowAndMirrorPasses_DoNotUseMainDrawFrustumCulling()
    {
        var renderer = Source("TerrainRenderer12.cs");
        var shadow = SourceContract.Extract(
            renderer,
            "public int RenderShadowDepth(",
            "public int RenderMirror(");
        var mirror = SourceContract.Extract(
            renderer,
            "public int RenderMirror(",
            "private IEnumerable<(int gx, int gy)> EnumerateCellKeysInCylinder(");

        Assert.DoesNotContain("TerrainCellDrawCulling", shadow, StringComparison.Ordinal);
        Assert.DoesNotContain("TerrainCellDrawCulling", mirror, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileTrace_ReportsCullActivationAndRejectedCells()
    {
        var renderer = Source("TerrainRenderer12.cs");
        var trace = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Profiling",
            "RendererProfilerTrace.cs");

        Assert.Contains("LastStats.TerrainFrustumCullingActive = drawFrustum.HasValue", renderer,
            StringComparison.Ordinal);
        Assert.Contains("LastStats.TerrainFrustumRejected++", renderer, StringComparison.Ordinal);
        Assert.Contains("terrainFrustumActive", trace, StringComparison.Ordinal);
        Assert.Contains("terrainFrustumRejected", trace, StringComparison.Ordinal);
    }
}
