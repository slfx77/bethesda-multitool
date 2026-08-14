using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Integration pins for the tiled-export settlement loop. The pure policy tests establish which
///     current-frame statuses are eligible; these contracts ensure the WinUI/D3D path samples those
///     statuses before deciding and never maps a status-only submission's readback buffer.
/// </summary>
public sealed class Export3DReadbackPolicySourceContractTests
{
    [Fact]
    public void RenderPassSamplesCurrentStatusBeforeItsGuardedReadback()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.Export3D.cs");
        var render = SourceContract.Extract(
            source,
            "internal async Task<Export3DRenderResult?> RenderProjectionTileAsync(",
            "internal sealed record Export3DOptions(");

        SourceContract.AssertOrder(
            render,
            "isComplete = StreamingQuiescence.IsQuiesced(refStats, terrainStats, strict: false);",
            "isFullySettled = StreamingQuiescence.IsQuiesced(refStats, terrainStats, strict: true);",
            "captureReadback = ExportTileCaptureDecision.ShouldCapture(",
            "if (captureReadback)",
            "target.RecordReadback(cmd);",
            "target.FinishFrameWithoutReadback(cmd);",
            "if (!captureReadback)",
            "return new Export3DRenderResult(null, isComplete, isFullySettled);",
            "var ssBytes = target.ReadbackToBytes();",
            "return new Export3DRenderResult(readback, isComplete, isFullySettled);");

        Assert.Equal(1, SourceContract.CountOccurrences(render, "target.RecordReadback(cmd);"));
        Assert.Equal(1, SourceContract.CountOccurrences(render, "target.ReadbackToBytes();"));
    }

    [Fact]
    public void SettlementLoopExitsOnlyWithTypedReadbackAndPromotesBoundedPolicies()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.ExportButton.cs");
        var loop = SourceContract.Extract(
            source,
            "Export3DTile? tile = null;",
            "// Skip only the renderer's transparent-black clear.");

        SourceContract.AssertOrder(
            loop,
            "var looseGraceElapsed =",
            "var settleTimedOut =",
            "var capturePolicy = ExportTileCapturePolicy.FullySettledOnly;",
            "if (looseGraceElapsed)",
            "capturePolicy = ExportTileCapturePolicy.CompleteOrFullySettled;",
            "if (settleTimedOut)",
            "capturePolicy = ExportTileCapturePolicy.Always;",
            "var result = await RenderProjectionTileAsync(",
            "plan.TileWidth, plan.TileHeight, opts, capturePolicy, ct);",
            "if (result.Readback is { } readback)",
            "tile = readback;",
            "break;");

        Assert.DoesNotContain("tile.IsFullySettled", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusOnlyTargetEndingRestoresReusableState()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuOffscreenSceneTarget12.cs");
        var finish = SourceContract.Extract(
            source,
            "public void FinishFrameWithoutReadback(",
            "private void EnsureReadback(");

        Assert.Contains("RestoreWaterOpaqueSnapshot(cmd);", finish, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordReadback", finish, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadbackToBytes", finish, StringComparison.Ordinal);
    }
}
