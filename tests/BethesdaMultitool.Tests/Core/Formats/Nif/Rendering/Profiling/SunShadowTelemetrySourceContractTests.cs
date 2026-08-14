using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>
///     Pins the shadow-loss diagnostic seam. Captured batch count is not submitted work: every
///     cascade filters that list independently, and terrain has a separate resident-only replay.
/// </summary>
public sealed class SunShadowTelemetrySourceContractTests
{
    [Fact]
    public void ReferenceReplayReportsExactPostFilterDrawsAndInstances()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var method = SourceContract.Extract(
            source, "public bool RenderShadowDepth(in SunShadowMath.LightFrustum frustum",
            "public void Dispose()");

        SourceContract.AssertOrder(method,
            "LastShadowSubmittedDrawCount = 0;",
            "LastShadowSubmittedInstanceCount = 0;",
            "LastShadowReplayCompleted = false;",
            "var cascadeInstances = Math.Min(draw.Cascades[cascadeIndex], draw.DrawCount);",
            "if (cascadeInstances <= 0)",
            "cmd.DrawIndexedInstanced",
            "LastShadowSubmittedDrawCount++;",
            "LastShadowSubmittedInstanceCount += cascadeInstances;",
            "LastShadowReplayCompleted = true;",
            "return LastShadowSubmittedDrawCount > 0;");
        Assert.DoesNotContain("return true;", method, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameTelemetryResetsOnSkippedPassesAndRetainsEachCascadeSeparately()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var reset = SourceContract.Extract(
            frame, "private void ResetShadowPassTelemetry()", "private void RecordSunShadowPass(");
        var method = SourceContract.Extract(
            frame, "private void RecordSunShadowPass(", "private void RenderFrameD3D12(");

        SourceContract.AssertOrder(reset,
            "_lastShadowMode = ShadowPassMode.Skipped;",
            "_lastShadowAvailableWithoutSubmissionMask = 0;",
            "Array.Clear(_lastShadowReferenceDrawsByCascade);",
            "Array.Clear(_lastShadowReferenceInstancesByCascade);",
            "Array.Clear(_lastShadowTerrainCellDrawsByCascade);");
        SourceContract.AssertOrder(method,
            "ResetShadowPassTelemetry();",
            "var terrainCasts = _showTerrain");
        Assert.True(SourceContract.CountOccurrences(frame, "ResetShadowPassTelemetry();") >= 2,
            "Both the live-frame gate and RecordSunShadowPass must clear stale telemetry.");

        Assert.Contains(
            "_lastShadowReferenceDrawsByCascade[i] = _references.LastShadowSubmittedDrawCount;",
            method, StringComparison.Ordinal);
        Assert.Contains(
            "_lastShadowReferenceInstancesByCascade[i] = _references.LastShadowSubmittedInstanceCount;",
            method, StringComparison.Ordinal);
        Assert.Contains(
            "var referenceReplayCompleted = _references.LastShadowReplayCompleted;",
            method, StringComparison.Ordinal);
        Assert.Contains("_lastShadowTerrainCellDrawsByCascade[i] = terrainCellDraws;",
            method, StringComparison.Ordinal);
        Assert.Contains(
            "if (drewCascade && _lastShadowReferenceDrawsByCascade[i] == 0 && terrainCellDraws == 0)",
            method, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureLogsPostPassCascadeTruthAndProfilerExportsTheSameFields()
    {
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        SourceContract.AssertOrder(capture,
            "[Capture] shadow state pass={0}",
            "RecordSunShadowPass(cmd, captureRenderOrigin, _camera.Position);",
            "[Capture] shadow result pass={0}");
        Assert.Contains("refDraws=[{4}] refInstances=[{5}] terrainCells=[{6}]", capture,
            StringComparison.Ordinal);
        Assert.Contains("availableWithoutSubmission=0x{7:X}", capture, StringComparison.Ordinal);

        var profiling = SourceContract.ReadAppSource("WorldView3DControl.Profiling.cs");
        foreach (var field in new[]
                 {
                     "shadowCapturedBatchCount",
                     "shadowReferenceDrawsByCascade",
                     "shadowReferenceInstancesByCascade",
                     "shadowTerrainCellDrawsByCascade",
                     "shadowAvailableWithoutSubmissionMask"
                 })
        {
            Assert.Contains($"fields[\"{field}\"]", profiling, StringComparison.Ordinal);
        }
    }
}
