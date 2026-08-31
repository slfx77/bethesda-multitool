using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class OpaqueFrontToBackPolicyTests
{
    [Fact]
    public void NearDepthUsesNormalizedForwardAndSubtractsSphereRadius()
    {
        var view = OpaqueFrontToBackPolicy.CreateBuildView(
            requested: true,
            eye: new Vector3(10, 20, 30),
            forward: new Vector3(0, 0, 4));
        var bounds = new Vector4(12, 24, 50, 3);

        Assert.True(view.Valid);
        Assert.Equal(Vector3.UnitZ, view.Forward);
        Assert.True(OpaqueFrontToBackPolicy.TryGetNearestViewDepth(
            in view, in bounds, out var depth));
        Assert.Equal(17d, depth, 10);
    }

    [Fact]
    public void TranslatingEyeShiftsEveryKeyEquallyAndPreservesRelativeOrder()
    {
        var originView = OpaqueFrontToBackPolicy.CreateBuildView(
            true, Vector3.Zero, Vector3.UnitX);
        var shiftedView = OpaqueFrontToBackPolicy.CreateBuildView(
            true, new Vector3(25, -8, 2), Vector3.UnitX);
        var near = new Vector4(100, 0, 0, 5);
        var far = new Vector4(200, 50, 20, 10);

        Assert.True(OpaqueFrontToBackPolicy.TryGetNearestViewDepth(
            in originView, in near, out var near0));
        Assert.True(OpaqueFrontToBackPolicy.TryGetNearestViewDepth(
            in originView, in far, out var far0));
        Assert.True(OpaqueFrontToBackPolicy.TryGetNearestViewDepth(
            in shiftedView, in near, out var near1));
        Assert.True(OpaqueFrontToBackPolicy.TryGetNearestViewDepth(
            in shiftedView, in far, out var far1));

        Assert.True(near0 < far0);
        Assert.True(near1 < far1);
        Assert.Equal(-25d, near1 - near0, 10);
        Assert.Equal(-25d, far1 - far0, 10);
    }

    [Fact]
    public void DisabledInvalidViewAndMalformedBoundsAllFailClosed()
    {
        var disabled = OpaqueFrontToBackPolicy.CreateBuildView(
            false, Vector3.Zero, Vector3.UnitZ);
        Assert.False(disabled.Requested);
        Assert.False(disabled.Valid);
        Assert.Equal(OpaqueFrontToBackFallbackReason.None, disabled.FallbackReason);

        foreach (var invalidForward in new[]
                 {
                     Vector3.Zero,
                     new Vector3(float.NaN, 0, 1),
                     new Vector3(float.PositiveInfinity, 0, 1)
                 })
        {
            var invalid = OpaqueFrontToBackPolicy.CreateBuildView(
                true, Vector3.Zero, invalidForward);
            Assert.True(invalid.Requested);
            Assert.False(invalid.Valid);
            Assert.Equal(OpaqueFrontToBackFallbackReason.InvalidView, invalid.FallbackReason);
        }

        var view = OpaqueFrontToBackPolicy.CreateBuildView(
            true, Vector3.Zero, Vector3.UnitZ);
        foreach (var malformed in new[]
                 {
                     new Vector4(float.NaN, 0, 1, 1),
                     new Vector4(0, 0, 1, float.PositiveInfinity),
                     new Vector4(0, 0, 1, -1)
                 })
        {
            Assert.False(OpaqueFrontToBackPolicy.TryGetNearestViewDepth(
                in view, in malformed, out _));
        }
    }

    [Fact]
    public void RendererObservesDepthOnlyDuringStagedBucketingAndOrdersOnceAtFinalize()
    {
        var environment = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "EnvironmentVariables.cs");
        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var registry = D3D12Source("OpaqueBatchRegistry12.cs");
        var draw = SourceContract.Extract(
            renderer,
            "private void DrawOpaqueBatches(",
            "private void DrawBlended(");
        var match = SourceContract.Extract(
            renderer,
            "private bool BatchBuildMatchesCurrent(",
            "private BatchBuildAdvanceResult AdvanceBatchBuild(");

        Assert.Contains(
            "FALLOUT_VIEWER_REFERENCE_OPAQUE_FRONT_TO_BACK",
            environment,
            StringComparison.Ordinal);
        Assert.Contains("OpaqueFrontToBackPolicy.CreateBuildView(", renderer,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            renderer,
            "var referenceBounds = new Vector4(",
            "OpaqueFrontToBackPolicy.TryGetNearestViewDepth(",
            "state.Target.OpaqueBatches.GetOrCreate(",
            "ObserveFrontToBackDepth(",
            "batch.Instances.Add(relWorldMatrix);");
        SourceContract.AssertOrder(
            renderer,
            "state.Target.OpaqueBatches.OrderGrassBatchesLast();",
            "state.Target.OpaqueBatches.OrderForSubmission(in frontToBackView);",
            "SortBatchInstancesByCascade(");
        Assert.DoesNotContain("OpaqueFrontToBack", match, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderForSubmission", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("NearestViewDepth", draw, StringComparison.Ordinal);

        Assert.Contains("FrontToBackActive ? FrontToBackComparer : null", registry,
            StringComparison.Ordinal);
        Assert.Contains("SubmissionLane(batch) != OpaqueSubmissionLane.Ordinary", registry,
            StringComparison.Ordinal);
        Assert.Contains("batch.NearestViewDepth = Math.Min(", registry,
            StringComparison.Ordinal);
        Assert.Contains("x.FirstTouchOrdinal.CompareTo(y.FirstTouchOrdinal)", registry,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TelemetrySurvivesSnapshotTraceAggregateAndHarnessAdmission()
    {
        var stats = new WorldRenderStats
        {
            ReferenceOpaqueFrontToBackActive = true,
            ReferenceOpaqueFrontToBackBatches = 321,
            ReferenceOpaqueFrontToBackInstances = 6543,
            ReferenceOpaqueFrontToBackFallbackReason = 2
        };

        var snapshot = stats.Snapshot();
        Assert.True(snapshot.ReferenceOpaqueFrontToBackActive);
        Assert.Equal(321, snapshot.ReferenceOpaqueFrontToBackBatches);
        Assert.Equal(6543, snapshot.ReferenceOpaqueFrontToBackInstances);
        Assert.Equal(2, snapshot.ReferenceOpaqueFrontToBackFallbackReason);

        var fields = RendererProfilerTrace.StatsFields("refs.", snapshot);
        Assert.True(Assert.IsType<bool>(fields["refs.refOpaqueFrontToBackActive"]));
        Assert.Equal(321, Assert.IsType<int>(fields["refs.refOpaqueFrontToBackBatches"]));
        Assert.Equal(6543, Assert.IsType<int>(fields["refs.refOpaqueFrontToBackInstances"]));
        Assert.Equal(2, Assert.IsType<int>(fields["refs.refOpaqueFrontToBackFallbackReason"]));

        stats.Reset();
        Assert.False(stats.ReferenceOpaqueFrontToBackActive);
        Assert.Equal(0, stats.ReferenceOpaqueFrontToBackBatches);
        Assert.Equal(0, stats.ReferenceOpaqueFrontToBackInstances);
        Assert.Equal(0, stats.ReferenceOpaqueFrontToBackFallbackReason);

        var accumulator = SourceContract.ReadAppSource("FrameProfileAccumulator.cs");
        Assert.Contains("references.ReferenceOpaqueFrontToBackActive", accumulator,
            StringComparison.Ordinal);
        Assert.Contains("refsOpaqueFrontToBackActiveRate", accumulator,
            StringComparison.Ordinal);
        Assert.Contains("refsOpaqueFrontToBackBatchesAvg", accumulator,
            StringComparison.Ordinal);
        Assert.Contains("refsOpaqueFrontToBackInstancesAvg", accumulator,
            StringComparison.Ordinal);
        Assert.Contains("_refOpaqueFrontToBackFallbacks.Clear();", accumulator,
            StringComparison.Ordinal);

        var harness = SourceContract.ReadSource("scratchpad", "live_profiles", "run_live.ps1");
        Assert.Contains("[string]$ExpectedOpaqueFrontToBack = ''", harness,
            StringComparison.Ordinal);
        Assert.Contains("FALLOUT_VIEWER_REFERENCE_OPAQUE_FRONT_TO_BACK", harness,
            StringComparison.Ordinal);
        Assert.Contains("refsOpaqueFrontToBackActiveRate -lt 0.999", harness,
            StringComparison.Ordinal);
        Assert.Contains("refsOpaqueFrontToBackActiveRate -ne 0", harness,
            StringComparison.Ordinal);
    }

    private static string D3D12Source(string fileName) => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", fileName);
}
