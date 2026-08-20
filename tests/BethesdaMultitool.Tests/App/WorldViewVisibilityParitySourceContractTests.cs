using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Pins the live-view parent visibility contract at the profiler-capture and HUD seams. Renderer
///     stats are retained between draws, so a disabled layer must gate both submission and every
///     diagnostic consumer; otherwise hidden geometry can appear in the PNG while telemetry reports
///     it disabled, or the HUD can present the last enabled frame as current.
/// </summary>
public sealed class WorldViewVisibilityParitySourceContractTests
{
    [Fact]
    public void CaptureUsesOneParentDecisionForOpaqueDepthAndDeferredPasses()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var core = SourceContract.Extract(
            source,
            "private async Task<byte[]?> CaptureSceneCoreAsync",
            "private RendererProfilerScenarioSnapshot BuildProfilerScenarioSnapshot");
        var compact = Compact(core);

        Assert.Contains(
            "varcaptureTerrainEnabled=_showTerrain&&_terrainisnotnull;",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "varcaptureReferencesEnabled=_showReferences&&_referencesisnotnull;",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "varcaptureWaterEnabled=_showWater&&_waterisnotnull;",
            compact,
            StringComparison.Ordinal);

        Assert.Contains(
            "varcaptureDepthAvailable=(captureReferencesEnabled||captureWaterEnabled)&&",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "varcaptureReferencesUseDepth=captureReferencesEnabled&&captureDepthAvailable;",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "varcaptureWaterUsesDepth=captureWaterEnabled&&captureDepthAvailable;",
            compact,
            StringComparison.Ordinal);

        Assert.Contains(
            "if(captureTerrainEnabled){_terrain!.Render(viewProj,cylinder);}",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "if(captureReferencesEnabled){_references!.Render(",
            compact,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_terrain?.Render(viewProj,cylinder);", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("_references?.Render(", compact, StringComparison.Ordinal);

        var streamDecision = SourceContract.Extract(
            core,
            "var captureStreamTransparency",
            "_references?.SetTransparencyStreamActive");
        Assert.Contains("captureWaterEnabled && captureReferencesEnabled", streamDecision, StringComparison.Ordinal);
        Assert.Contains(
            "if(captureReferencesEnabled&&_water!.HasVisibleWaterToPartition(cylinder))",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "if(captureReferencesEnabled){if(captureWaterTransparencyPartitioned){" +
            "_references!.RenderBlendedDeferredAtOrAboveWater",
            compact,
            StringComparison.Ordinal);

        // Water is a sibling, not a child, of Meshes. Already-discovered placed-NIF planes stay in
        // the Water pass when reference geometry is hidden, exactly as in the live frame.
        Assert.Contains(
            "if(captureWaterEnabled&&_referencesisnotnull){" +
            "_water!.SetNifWaterPlanes(_references.NifWaterPlanes);}",
            compact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureSettleAndTelemetryTreatDisabledStatsAsUnavailable()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var hooks = SourceContract.Extract(
            source,
            "internal bool Profiler_IsReferenceStreamingQuiesced",
            "internal void Profiler_SetGameDay");
        var hooksCompact = Compact(hooks);

        Assert.Contains(
            "StreamingQuiescence.IsQuiesced(" +
            "_showReferences?_references?.LastStats:null," +
            "terrain:_showTerrain?_terrain?.LastStats:null,strict:true);",
            hooksCompact,
            StringComparison.Ordinal);
        Assert.Contains(
            "CaptureSceneCensus.From(" +
            "_showReferences?_references?.LastStats:null," +
            "_showTerrain?_terrain?.LastStats:null);",
            hooksCompact,
            StringComparison.Ordinal);

        var core = SourceContract.Extract(
            source,
            "private async Task<byte[]?> CaptureSceneCoreAsync",
            "private RendererProfilerScenarioSnapshot BuildProfilerScenarioSnapshot");
        var coreCompact = Compact(core);
        Assert.Contains(
            "captureReferencesEnabled?_references!.LastStats:null," +
            "captureWaterEnabled?_water!.LastStats:null," +
            "captureTerrainEnabled?_terrain!.LastStats:null,",
            coreCompact,
            StringComparison.Ordinal);

        var telemetry = SourceContract.Extract(
            source,
            "private void EmitCaptureParityTelemetry",
            "RendererProfilerTrace.Event(\"capture-state\"");
        var telemetryCompact = Compact(telemetry);
        Assert.Contains(
            "if(referenceStatsisnull||_referencesisnull){" +
            "fields[\"belowWaterBlendPartition\"]=null;}",
            telemetryCompact,
            StringComparison.Ordinal);
        Assert.DoesNotContain("varterrainStats=_showTerrain", telemetryCompact, StringComparison.Ordinal);
    }

    [Fact]
    public void HudGatesRetainedLayerCountersButAlwaysReportsPipelineFailure()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.Hud.cs");
        var update = SourceContract.Extract(source, "private void UpdateHud", "private float NormalizedYawDegrees");
        var compact = Compact(update);

        Assert.Contains(
            "varbelowWaterBlendedDraws=_showReferences?" +
            "_references?.LastBelowWaterBlendedDraws??0:0;",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "varpartitionedBlendedCandidates=_showReferences?" +
            "_references?.LastPartitionedBlendedCandidates??0:0;",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "vartruncatedDraws=_showReferences?_references?.LastFrameDrawsTruncated??0:0;",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "if(_showFrameStats&&_showTerrain&&_terrainisnotnull)",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "if(_showFrameStats&&_showReferences&&_referencesisnotnull)",
            compact,
            StringComparison.Ordinal);

        // This is availability, not a retained per-frame counter: hiding Meshes must not conceal a
        // real renderer initialization failure that explains why the layer cannot be re-enabled.
        Assert.Contains(
            "if(_referencePipelineInitErroris{Length:>0}referenceError)",
            compact,
            StringComparison.Ordinal);
    }

    private static string Compact(string source)
    {
        return string.Concat(source.Where(character => !char.IsWhiteSpace(character)));
    }
}