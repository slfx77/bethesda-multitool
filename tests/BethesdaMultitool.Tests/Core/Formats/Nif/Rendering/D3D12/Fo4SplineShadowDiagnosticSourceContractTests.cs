using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class Fo4SplineShadowDiagnosticSourceContractTests
{
    [Fact]
    public void AllFiveCaptureSitesKeepTheGameQualifiedSourceMarker()
    {
        var renderer = RendererSource();
        var captures = SourceContract.Extract(renderer,
            "if (_shadowCaptureArmed)", "private void ObserveFo4BendableSplineWindDraw(");
        Assert.Equal(5, SourceContract.CountOccurrences(captures, "new ShadowDraw("));
        Assert.Equal(5, SourceContract.CountOccurrences(captures,
            "IsFo4BendableSpline: Fo4SplineShadowDiagnosticPolicy.IsCaster("));
        Assert.Equal(2, SourceContract.CountOccurrences(captures,
            "_renderCache?.Game ?? BethesdaGame.Unknown, packetSub.IsBendableSplineWind"));
        Assert.Equal(3, SourceContract.CountOccurrences(captures,
            "_renderCache?.Game ?? BethesdaGame.Unknown, sub.IsBendableSplineWind"));

        var cache = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif",
            "Rendering", "D3D12", "ReferenceMeshCache12.cs");
        SourceContract.AssertOrder(cache, "IsBendableSplineWind: generated.UsesWindShader",
            "IsBendableSplineWind = sub.IsBendableSplineWind");
        Assert.DoesNotContain("ModelPath", Replay(renderer), StringComparison.Ordinal);
    }

    [Fact]
    public void LifetimeFlagFiltersOnlyBothShadowReplayLoopsBeforeGpuState()
    {
        var renderer = RendererSource();
        var replay = Replay(renderer);
        const string predicate =
            "Fo4SplineShadowDiagnosticPolicy.ShouldOmit(_omitFo4SplineShadows, draw.IsFo4BendableSpline)";
        Assert.Contains("private readonly bool _omitFo4SplineShadows =", renderer, StringComparison.Ordinal);
        Assert.Equal(1, SourceContract.CountOccurrences(renderer, "_omitFo4SplineShadows ="));
        Assert.Equal(2, SourceContract.CountOccurrences(renderer, predicate));
        Assert.Equal(2, SourceContract.CountOccurrences(replay, predicate));
        SourceContract.AssertOrder(replay, "var hasCascadeInstances = false;", predicate,
            "if (!hasCascadeInstances)", "_ringBuffer.TryAllocate(", predicate, "cmd.SetPipelineState(pso);");
        Assert.DoesNotContain("GetEnvironmentVariable", replay, StringComparison.Ordinal);
    }

    [Fact]
    public void OmissionCountsEveryPositivePrefixAndPreservesAuthoritativeEmptyReplay()
    {
        var replay = Replay(RendererSource());
        var preflight = SourceContract.Extract(replay,
            "var hasCascadeInstances = false;", "var cmd = _recorder.CommandList;");
        SourceContract.AssertOrder(preflight, "ShadowCascadeSubmissionPolicy.ClampInstanceCount(",
            "if (cascadeInstances <= 0) continue;", "Fo4SplineShadowDiagnosticPolicy.ShouldOmit(",
            "LastShadowOmittedSplineDrawCount++;", "LastShadowOmittedSplineInstanceCount += cascadeInstances;",
            "continue;", "hasCascadeInstances = true;", "if (!hasCascadeInstances)",
            "LastShadowReplayCompleted = true;", "return false;");
        Assert.Equal(1, SourceContract.CountOccurrences(preflight, "break;"));
        SourceContract.AssertOrder(preflight, "hasCascadeInstances = true;",
            "if (!_omitFo4SplineShadows)", "break;");
        SourceContract.AssertOrder(replay, "LastShadowOmittedSplineDrawCount = 0;",
            "LastShadowSubmittedSplineInstanceCount = 0;", "LastShadowReplayCompleted = false;");
    }

    [Fact]
    public void SubmittedSplineTelemetryFollowsActualCommandAndRetainsCascadeCounts()
    {
        var replay = Replay(RendererSource());
        SourceContract.AssertOrder(replay,
            "cmd.DrawIndexedInstanced((uint)draw.IndexCount, (uint)cascadeInstances, 0, 0, 0);",
            "LastShadowSubmittedDrawCount++;", "LastShadowSubmittedInstanceCount += cascadeInstances;",
            "if (draw.IsFo4BendableSpline)", "LastShadowSubmittedSplineDrawCount++;",
            "LastShadowSubmittedSplineInstanceCount += cascadeInstances;");
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var capture = SourceContract.ReadAppSource("WorldView3DControl.ShadowCapture.cs");
        var profiling = SourceContract.ReadAppSource("WorldView3DControl.Profiling.cs");
        foreach (var name in new[]
                     { "OmittedSplineDraw", "OmittedSplineInstance", "SubmittedSplineDraw", "SubmittedSplineInstance" })
        {
            Assert.Contains($"Array.Clear(_lastShadow{name}sByCascade);", frame, StringComparison.Ordinal);
            Assert.Contains($"_lastShadow{name}sByCascade[i] = _references.LastShadow{name}Count;",
                frame, StringComparison.Ordinal);
            Assert.Contains($"[\"shadow{name}sByCascade\"] = _lastShadow{name}sByCascade.ToArray()",
                capture, StringComparison.Ordinal);
            Assert.Contains($"fields[\"shadow{name}sByCascade\"] = _lastShadow{name}sByCascade;",
                profiling, StringComparison.Ordinal);
        }

        Assert.Equal(2, SourceContract.CountOccurrences(capture, "[\"diagnosticOmitFo4SplineShadows\"]"));
        Assert.Contains("fields[\"diagnosticOmitFo4SplineShadows\"]", profiling, StringComparison.Ordinal);
    }

    private static string RendererSource()
    {
        return SourceContract.ReadSource("src", "BethesdaMultitool", "Core",
            "Formats", "Nif", "Rendering", "D3D12", "ReferenceRenderer12.cs");
    }

    private static string Replay(string renderer)
    {
        return SourceContract.Extract(renderer,
            "public bool RenderShadowDepth(", "public bool RenderMirrorColor(");
    }
}