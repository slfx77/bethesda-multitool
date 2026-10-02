using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private ShadowContentKey CaptureShadowContentKey() => new(
        _references!.BatchContentVersion, _terrain?.ContentVersion ?? 0,
        _references.VisibilityKey, _showTerrain && _terrain is not null);

    private void ArmCaptureSunShadows()
    {
        // Same fit and caster-classification math as live rendering, resampled for EVERY attempt.
        // Frame-local captures contain ring addresses and must never survive BeginFrame.
        _shadowFrameAnchor = ResolveShadowAnchor(_camera.Position);
        _shadowFrameSceneZSpan = ResolveShadowSceneZSpan();
        EnsureShadowLadderScale();
        _references!.ArmShadowCapture(
            MathF.Min(_shadowCasterRingRadiusScaled, _renderDistance),
            (_shadowFrameAnchor, Vector3.Normalize(_lastResolvedSunDirection),
                ResolveShadowCascadeRadii(), _shadowCascadeSnapScaled, _shadowFrameSceneZSpan));
    }

    private void EmitCaptureShadowPassTelemetry(bool isPrime, int attempt, bool coherent, bool diagnosticDefer)
    {
        RendererProfilerTrace.Event("capture-shadow-pass", new Dictionary<string, object?>
        {
            ["phase"] = isPrime ? "prime" : "real-end",
            ["primeAttempt"] = attempt,
            ["primingPolicy"] = coherent ? "coherent" : "legacy",
            ["deferredReason"] = _lastShadowDeferredReason,
            ["diagnosticDeferRequested"] = diagnosticDefer,
            ["referenceExtentChanged"] = _lastShadowReferenceExtentChanged,
            ["armedSceneZSpan"] = _shadowFrameSceneZSpan,
            ["postCullSceneZSpan"] = _lastShadowPostCullSceneZSpan,
            ["executedCascadeMask"] = _lastShadowCascadeMask,
            ["completedCascadeMask"] = _lastShadowCompletedCascadeMask,
            ["referenceBatchContentVersion"] = _references!.BatchContentVersion,
            ["diagnosticOmitFo4SplineShadows"] = _references.OmitsFo4SplineShadows,
            ["shadowOmittedSplineDrawsByCascade"] = _lastShadowOmittedSplineDrawsByCascade.ToArray(),
            ["shadowOmittedSplineInstancesByCascade"] = _lastShadowOmittedSplineInstancesByCascade.ToArray(),
            ["shadowSubmittedSplineDrawsByCascade"] = _lastShadowSubmittedSplineDrawsByCascade.ToArray(),
            ["shadowSubmittedSplineInstancesByCascade"] = _lastShadowSubmittedSplineInstancesByCascade.ToArray(),
            ["terrainContentVersion"] = _terrain?.ContentVersion ?? 0,
            ["referenceVisibility"] = _references.VisibilityKey.ToString(),
            ["cascadeGenerations"] = _shadowCascadeGenerations.ToArray()
        });
        Log.Info(
            "[Capture] shadow prime policy={0} attempt={1} phase={2} completed=0x{3:X} defer={4} span={5:R}->{6:R}",
            coherent ? "coherent" : "legacy", attempt, isPrime ? "prime" : "real-end",
            _lastShadowCompletedCascadeMask, _lastShadowDeferredReason,
            _shadowFrameSceneZSpan, _lastShadowPostCullSceneZSpan);
    }

    private void EmitCaptureShadowSampleTelemetry(string phase, int attempt, Vector3 origin,
        ShadowMapRenderer12.ShadowSampleConstants samples, string[]? fingerprints = null)
    {
        RendererProfilerTrace.Event("capture-shadow-samples", new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["primeAttempt"] = attempt,
            ["mapHasContent"] = _shadowMap!.HasContent,
            ["diagnosticOmitFo4SplineShadows"] = _references!.OmitsFo4SplineShadows,
            ["resolution"] = _shadowMap.Resolution,
            ["renderOrigin"] = ShadowVector(origin),
            ["publishedOrigins"] = _shadowPublishedOrigins.Select(ShadowVector).ToArray(),
            ["publishedMatrices"] = _shadowPublishedFrustums.Select(f => ShadowMatrix(f.ViewProj)).ToArray(),
            ["sampleMatrices"] = new[] { samples.Matrix0, samples.Matrix1, samples.Matrix2, samples.Matrix3 }
                .Select(ShadowMatrix).ToArray(),
            ["sampleParams"] = new[] { samples.Params0, samples.Params1, samples.Params2, samples.Params3 }
                .Select(p => new[] { p.X, p.Y, p.Z, p.W }).ToArray(),
            ["cascadeGenerations"] = _shadowCascadeGenerations.ToArray(),
            ["completedCascadeMask"] = _lastShadowCompletedCascadeMask,
            ["depthSha256"] = fingerprints,
            ["depthHashDomain"] = "R32_FLOAT little-endian pixel bytes in row order; excludes row padding",
            ["lastSubmittedFence"] = _commandRecorder12!.LastSubmittedFenceValue
        });
    }

    /// <summary>Copies and hashes each completed shadow cascade while retaining readbacks through their final GPU use.</summary>
    /// <returns>One hash per cascade, or null when the shadow map has no published content.</returns>
    /// <remarks>A rejected recorder transfer aborts the unsubmitted copy and releases only the locally owned readback.</remarks>
    private string[]? ReadCaptureShadowFingerprints()
    {
        if (_shadowMap is not { HasContent: true } map) return null;
        var recorder = _commandRecorder12!;
        var fingerprints = new string[ShadowMapRenderer12.CascadeCount];
        for (var cascade = 0; cascade < fingerprints.Length; cascade++)
        {
            // One readback buffer at a time bounds diagnostic RAM. No scene work and no awaits
            // intervene between the final prime, these copies, and the real color binding.
            recorder.BeginFrame();
            Vortice.Direct3D12.ID3D12Resource? readback = null;
            uint rowPitch;
            var transferred = false;
            try
            {
                readback = map.RecordDiagnosticReadback(recorder.CommandList, cascade, out rowPitch);
                // Transfer before submission. Successful registration protects both fenced and
                // unfenced work; rejection leaves the unsubmitted readback owned here.
                recorder.EnqueueDisposeAfterCurrentFrame(readback);
                transferred = true;
            }
            catch
            {
                try
                {
                    recorder.AbortFrame();
                }
                finally
                {
                    if (!transferred) { readback?.Dispose(); }
                }
                throw;
            }

            // The next BeginFrame retires this buffer only after the synchronous hash has finished.
            recorder.EndFrame();
            WaitForFrameFence(_gpu12!.FrameFence, recorder.LastSubmittedFenceValue);
            fingerprints[cascade] = WorldViewCaptureTelemetry.ComputeShadowFingerprint(
                readback, map.Resolution, rowPitch);
        }

        return fingerprints;
    }

    private static float[] ShadowVector(Vector3 value) => [value.X, value.Y, value.Z];

    private static float[] ShadowMatrix(Matrix4x4 value) =>
    [
        value.M11, value.M12, value.M13, value.M14,
        value.M21, value.M22, value.M23, value.M24,
        value.M31, value.M32, value.M33, value.M34,
        value.M41, value.M42, value.M43, value.M44
    ];
}
