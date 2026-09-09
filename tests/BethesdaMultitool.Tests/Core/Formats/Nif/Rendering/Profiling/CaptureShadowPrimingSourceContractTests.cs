using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>Pins capture-only shadow priming, sampled-map identity, and readback lifetime.</summary>
public sealed class CaptureShadowPrimingSourceContractTests
{
    [Fact]
    public void CoherentCaptureArmsAfterTheMainSunBindingAndOnlyPrimesForceTheLadder()
    {
        var loop = CaptureLoop();
        SourceContract.AssertOrder(loop,
            "var isPrime = !shadowPrimeReady;",
            "var forceCurrentShadowLadder = isPrime && coherentShadowPrime;",
            "if (!coherentShadowPrime) ArmCaptureSunShadows();",
            "BindAtmosphereConstants(",
            "if (coherentShadowPrime)",
            "ArmCaptureSunShadows();",
            "captureArmedSunDirection = _lastResolvedSunDirection;",
            "target.Bind(cmd);",
            "RecordSunShadowPass(cmd, captureRenderOrigin, _camera.Position,",
            "forceCurrentLadder: forceCurrentShadowLadder,",
            "captureArmedSunDirection: captureArmedSunDirection,");
        Assert.Equal(1, SourceContract.CountOccurrences(loop, "forceCurrentShadowLadder ="));

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        Assert.Contains("bool forceCurrentLadder = false,", frame, StringComparison.Ordinal);
        Assert.Contains("RecordSunShadowPass(cmd, referenceRenderOrigin, _camera.Position);",
            frame, StringComparison.Ordinal);
        Assert.Contains("cascadeDue[i] = forceCurrentLadder || posePending || contentPending;",
            frame, StringComparison.Ordinal);
        Assert.Contains("if (!forceCurrentLadder && !firstPublish && !anyVisibilityPending &&",
            frame, StringComparison.Ordinal);
    }

    [Fact]
    public void CoherentRefitsStillDeferWhenCapturedExtentsOrSunNoLongerMatch()
    {
        var replay = ShadowReplay();
        SourceContract.AssertOrder(replay,
            "_lastShadowPostCullSceneZSpan = ResolveShadowSceneZSpan();",
            "SunShadowMath.ShouldDeferForReferenceExtentChange(",
            "referenceExtentIdentityChanged || forceCurrentLadder,",
            "_shadowFrameSceneZSpan,",
            "_lastShadowPostCullSceneZSpan);",
            "armedSun != _lastResolvedSunDirection;",
            "if (_lastShadowReferenceExtentChanged || captureSunChanged || diagnosticDefer)",
            "_references.DisarmShadowCapture();",
            "return;",
            "_shadowMap.BeginCascade(cmd, i);");
    }

    [Fact]
    public void EachReplayResetsCompletionAndOnlyAuthoritativeSubpassesSetItsBits()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var reset = SourceContract.Extract(frame,
            "private void ResetShadowPassTelemetry()", "private void RecordSunShadowPass(");
        Assert.Contains("_lastShadowCompletedCascadeMask = 0;", reset, StringComparison.Ordinal);
        Assert.Equal(1, SourceContract.CountOccurrences(frame, "_lastShadowCompletedCascadeMask ="));
        Assert.Equal(1, SourceContract.CountOccurrences(frame, "_lastShadowCompletedCascadeMask |="));

        SourceContract.AssertOrder(ShadowReplay(),
            "ResetShadowPassTelemetry();",
            "if (_shadowMap is null || _references is null)",
            "var drewCascade = _references.RenderShadowDepth(frustums[i], i);",
            "var referenceReplayCompleted = _references.LastShadowReplayCompleted;",
            "terrainReplayCompleted = _terrain!.LastShadowReplayCompleted;",
            "cascadeHasDraws[i] = drewCascade;",
            "var replayCompleted = SunShadowMath.ShouldCommitCascadeState(",
            "referenceReplayCompleted, terrainCasts, terrainReplayCompleted);",
            "if (replayCompleted) _lastShadowCompletedCascadeMask |= 1 << i;");
    }

    [Fact]
    public void FailedOrRetryingPrimesCannotReachRealImageReadback()
    {
        var loop = CaptureLoop();
        SourceContract.AssertOrder(loop,
            "var shadowPrimeReady = !captureShadows;",
            "while (true)",
            "var isPrime = !shadowPrimeReady;",
            "if (isPrime) primeAttempts++;",
            "if (!isPrime) break;",
            "var primeDecision = CaptureShadowPrimingPolicy.Decide(",
            "coherentShadowPrime, _lastShadowCompletedCascadeMask, primeAttempts);",
            "if (primeDecision == CaptureShadowPrimingDecision.Fail)",
            "throw new InvalidOperationException(",
            "if (primeDecision == CaptureShadowPrimingDecision.Ready)",
            "shadowPrimeReady = true;");
        Assert.Equal(2, SourceContract.CountOccurrences(loop, "shadowPrimeReady ="));
        Assert.Equal(1, SourceContract.CountOccurrences(loop, "target.RecordReadback(recorder);"));
        Assert.Contains(
            "if (!isPrime)\n                {\n                    target.RecordReadback(recorder);\n                }",
            loop, StringComparison.Ordinal);
        Assert.DoesNotContain("await ", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedPrimeIsFingerprintedAndContentGuardedBeforeRealReadback()
    {
        var loop = CaptureLoop();
        var accepted = SourceContract.Extract(loop,
            "if (primeDecision == CaptureShadowPrimingDecision.Ready)", "shadowPrimeReady = true;");
        SourceContract.AssertOrder(accepted,
            "ReadCaptureShadowFingerprints()",
            "EmitCaptureShadowSampleTelemetry(\"post-prime\"",
            "_shadowMap!.GetSampleConstants(captureRenderOrigin), fingerprints);",
            "shadowPrimeContent = CaptureShadowContentKey();");
        SourceContract.AssertOrder(loop,
            "ShadowContentKey? shadowPrimeContent = null;",
            "if (!isPrime && coherentShadowPrime && shadowPrimeContent != CaptureShadowContentKey())",
            "throw new InvalidOperationException(",
            "RecordSunShadowPass(cmd, captureRenderOrigin, _camera.Position,",
            "target.RecordReadback(recorder);");

        var helper = SourceContract.ReadAppSource("WorldView3DControl.ShadowCapture.cs");
        var identity = SourceContract.Extract(helper,
            "private ShadowContentKey CaptureShadowContentKey()", "private void ArmCaptureSunShadows()");
        Assert.Contains("BatchContentVersion", identity, StringComparison.Ordinal);
        Assert.Contains("ContentVersion", identity, StringComparison.Ordinal);
        Assert.Contains("VisibilityKey", identity, StringComparison.Ordinal);
        Assert.Contains("_showTerrain", identity, StringComparison.Ordinal);
    }

    [Fact]
    public void RealColorTelemetryRecordsTheActualMainAtmosphereBinding()
    {
        var mainBinding = SourceContract.Extract(CaptureLoop(), "BindAtmosphereConstants(", "target.Bind(cmd);");
        SourceContract.AssertOrder(mainBinding,
            "BindAtmosphereConstants(",
            "if (!isPrime)",
            "EmitCaptureShadowSampleTelemetry(",
            "\"real-color-bind\", primeAttempts, captureRenderOrigin, _lastBoundShadowConstants);");

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var atmosphere = SourceContract.Extract(frame,
            "private void BindAtmosphereConstants(", "private void ResetShadowPassTelemetry()");
        SourceContract.AssertOrder(atmosphere,
            "shadow = shadowMap.GetSampleConstants(cameraOrigin);",
            "_lastBoundShadowConstants = shadow;",
            "cameraOrigin: cameraOrigin, ambientScale: ambientScale, shadow: shadow,",
            "*(AtmosphereConstants*)alloc.CpuPtr = constants;",
            "cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, alloc.GpuAddress);");
    }

    [Fact]
    public void FingerprintReadbackTransfersOwnershipBeforeSubmissionAndHashesBeforeAnyYield()
    {
        var helper = SourceContract.ReadAppSource("WorldView3DControl.ShadowCapture.cs");
        var readback = SourceContract.Extract(helper,
            "private string[]? ReadCaptureShadowFingerprints()", "private static float[] ShadowVector(");
        SourceContract.AssertOrder(readback,
            "recorder.BeginFrame();",
            "readback = map.RecordDiagnosticReadback(recorder.CommandList, cascade, out rowPitch);",
            "recorder.EnqueueDisposeAfterCurrentFrame(readback);",
            "recorder.EndFrame();",
            "WaitForFrameFence(_gpu12!.FrameFence, recorder.LastSubmittedFenceValue);",
            "fingerprints[cascade] = WorldViewCaptureTelemetry.ComputeShadowFingerprint(",
            "readback, map.Resolution, rowPitch);");
        Assert.Equal(1, SourceContract.CountOccurrences(readback, "EnqueueDisposeAfterCurrentFrame(readback)"));
        Assert.DoesNotContain("await ", readback, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(", readback, StringComparison.Ordinal);
        Assert.DoesNotContain("readback.Dispose(", readback, StringComparison.Ordinal);
        Assert.DoesNotContain("using var readback", readback, StringComparison.Ordinal);
        SourceContract.AssertOrder(readback, "catch", "recorder.AbortFrame();", "throw;");
    }

    [Fact]
    public void MappedDepthIsAlwaysUnmappedAndTheCallerRetainsOwnership()
    {
        var telemetry = SourceContract.ReadAppSource("WorldViewCaptureTelemetry.cs");
        var compute = SourceContract.Extract(telemetry,
            "internal static unsafe string ComputeShadowFingerprint(",
            "internal static unsafe void AnalyzeAndLogShadowDump(");
        SourceContract.AssertOrderIgnoringWhitespace(compute,
            "buffer.Map(0, &data).CheckError();",
            "try",
            "return ShadowMapFingerprint.Compute(new ReadOnlySpan<byte>(data, length), resolution, checked((int)rowPitch));",
            "finally",
            "buffer.Unmap(0, null);");
        Assert.Equal(1, SourceContract.CountOccurrences(compute, "buffer.Map("));
        Assert.Equal(1, SourceContract.CountOccurrences(compute, "buffer.Unmap("));
        Assert.DoesNotContain("buffer.Dispose(", compute, StringComparison.Ordinal);
    }

    private static string CaptureLoop()
    {
        return SourceContract.Extract(
            SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs"),
            "var primeAttempts = 0;", "var captureReferenceStats = captureReferencesEnabled");
    }

    private static string ShadowReplay()
    {
        return SourceContract.Extract(
            SourceContract.ReadAppSource("WorldView3DControl.Frame.cs"),
            "private void RecordSunShadowPass(", "private void RenderFrameD3D12(");
    }
}
