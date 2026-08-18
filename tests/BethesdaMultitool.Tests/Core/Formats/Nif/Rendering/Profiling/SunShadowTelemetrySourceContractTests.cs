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
            "terrainReplayCompleted = _terrain!.LastShadowReplayCompleted;",
            method, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A cascade's PUBLISHED availability must stay a function of what was actually SUBMITTED:
    ///     <c>drewCascade</c> is the sole carrier, produced by the reference replay's post-filter draw
    ///     count, widened only by resident terrain cells, and consumed unmodified by all three
    ///     publication calls. A future edit that publishes a literal, or re-derives availability from
    ///     the captured batch list, reintroduces the 2026-08-13 defect where a cleared cascade was
    ///     advertised as populated.
    ///     <para>
    ///         ⚠ This pins SUBMISSION, which is NOT the same as RASTERIZATION, and the gap is a live
    ///         defect rather than a hypothetical: measured 2026-08-14 at the user's yaw-pair poses,
    ///         cascade 0 was published ENABLED (Params0.x = 1) after 24 reference draws and 4 terrain
    ///         cells were submitted, yet its map came back with <b>0 of 16,777,216 texels written</b> —
    ///         every submitted caster was clipped by the ortho box. Since <c>ShadowFactor</c> has no
    ///         fallback once a cascade's geometric test passes, that is a fully-lit hole. Do not read
    ///         this test as proof that an available cascade has content; it only proves availability
    ///         cannot be decoupled from submission behind our back.
    ///     </para>
    /// </summary>
    [Fact]
    public void CascadePublishedAvailabilityIsDerivedFromSubmittedWork()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var method = SourceContract.Extract(
            frame, "private void RecordSunShadowPass(", "private void RenderFrameD3D12(");

        SourceContract.AssertOrder(method,
            "var drewCascade = _references.RenderShadowDepth(frustums[i], i);",
            "terrainCellDraws = _terrain!.RenderShadowDepth(",
            "drewCascade |= terrainCellDraws > 0;",
            "cascadeHasDraws[i] = drewCascade;",
            "_shadowMap.SetCascadeAvailability(i, drewCascade);",
            "_shadowMap.PublishCascade(i, frustums[i], renderOrigin, drewCascade);",
            "_shadowMap.Publish(frustums, renderOrigin, cascadeHasDraws);");

        // Exactly one producer and one contributor: no later statement may re-point the flag.
        // ("drewCascade |=" does not contain "drewCascade =", so these count distinct sites.)
        Assert.Equal(1, SourceContract.CountOccurrences(method, "drewCascade ="));
        Assert.Equal(1, SourceContract.CountOccurrences(method, "drewCascade |="));

        // Every availability publication passes that flag — never a literal or a second variable.
        Assert.Equal(
            SourceContract.CountOccurrences(method, "SetCascadeAvailability("),
            SourceContract.CountOccurrences(method, "SetCascadeAvailability(i, drewCascade)"));
        Assert.Equal(
            SourceContract.CountOccurrences(method, "PublishCascade("),
            SourceContract.CountOccurrences(
                method, "PublishCascade(i, frustums[i], renderOrigin, drewCascade)"));
    }

    /// <summary>
    ///     The terrain shadow gather must distinguish an AUTHORITATIVE zero (no terrain loaded, or no
    ///     resident cell in the cylinder) from a gather that could not RUN (ring allocation failed).
    ///     Its <c>int</c> return collapses all three into 0, so the host cannot tell an empty cascade
    ///     it may cache from a failed one it must retry — which is why that renderer's own
    ///     "the host ... retries" comment was never honoured.
    /// </summary>
    [Fact]
    public void TerrainShadowGatherDistinguishesAnAuthoritativeZeroFromAFailedRun()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "TerrainRenderer12.cs");
        var method = SourceContract.Extract(
            source, "public int RenderShadowDepth(Matrix4x4 lightViewProj",
            "private IEnumerable<(int gx, int gy)> EnumerateCellKeysInCylinder(");

        SourceContract.AssertOrder(method,
            "LastShadowReplayCompleted = false;",
            "LastShadowReplayCompleted = true;", // no terrain loaded at all — an authoritative zero
            "if (!_ringBuffer.TryAllocate(",
            "return 0; // the host disables this cleared cascade and retries",
            "LastShadowReplayCompleted = true;", // the gather ran to completion
            "return drawn;");

        // Exactly two authoritative sites: the ring-failure return BETWEEN them must not be one.
        Assert.Equal(2, SourceContract.CountOccurrences(method, "LastShadowReplayCompleted = true;"));
        Assert.Equal(1, SourceContract.CountOccurrences(method, "LastShadowReplayCompleted = false;"));
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
        Assert.Contains("animatedLeaves={7} animatedMeshes={8}", capture, StringComparison.Ordinal);

        var profiling = SourceContract.ReadAppSource("WorldView3DControl.Profiling.cs");
        foreach (var field in new[]
                 {
                     "shadowCapturedBatchCount",
                     "shadowReferenceDrawsByCascade",
                     "shadowReferenceInstancesByCascade",
                     "shadowTerrainCellDrawsByCascade",
                 })
        {
            Assert.Contains($"fields[\"{field}\"]", profiling, StringComparison.Ordinal);
        }
    }
}
