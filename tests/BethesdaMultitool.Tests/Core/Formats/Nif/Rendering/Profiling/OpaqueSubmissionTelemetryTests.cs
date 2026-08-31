using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>Contracts for the low-overhead counters used to size a future opaque indirect path.</summary>
public sealed class OpaqueSubmissionTelemetryTests
{
    [Fact]
    public void WorldRenderStats_SnapshotResetAndTracePreserveOpaqueSubmissionShape()
    {
        var stats = new WorldRenderStats
        {
            ReferenceOpaqueActiveDraws = 101,
            ReferenceOpaqueSurvivingDraws = 97,
            ReferenceOpaquePsoTransitions = 23,
            ReferenceOpaqueUniquePsos = 7,
            ReferenceOpaqueOrdinaryDraws = 80,
            ReferenceOpaqueDecalDraws = 5,
            ReferenceOpaqueGrassCutoutDraws = 9,
            ReferenceOpaqueGrassDepthWriteDraws = 3,
            ReferenceOpaqueIndirectActive = true,
            ReferenceOpaqueDirectDraws = 17,
            ReferenceOpaqueIndirectDraws = 80,
            ReferenceOpaqueIndirectExecuteCalls = 4,
            ReferenceOpaqueIndirectArgumentBytes = 5120,
            ReferenceOpaqueIndirectFallbackReason = 0,
            ReferenceOpaqueSubmissionMilliseconds = 12.5,
            ReferenceBlendedSubmissionMilliseconds = 3.25
        };

        var snapshot = stats.Snapshot();

        Assert.Equal(101, snapshot.ReferenceOpaqueActiveDraws);
        Assert.Equal(97, snapshot.ReferenceOpaqueSurvivingDraws);
        Assert.Equal(23, snapshot.ReferenceOpaquePsoTransitions);
        Assert.Equal(7, snapshot.ReferenceOpaqueUniquePsos);
        Assert.Equal(80, snapshot.ReferenceOpaqueOrdinaryDraws);
        Assert.Equal(5, snapshot.ReferenceOpaqueDecalDraws);
        Assert.Equal(9, snapshot.ReferenceOpaqueGrassCutoutDraws);
        Assert.Equal(3, snapshot.ReferenceOpaqueGrassDepthWriteDraws);
        Assert.True(snapshot.ReferenceOpaqueIndirectActive);
        Assert.Equal(17, snapshot.ReferenceOpaqueDirectDraws);
        Assert.Equal(80, snapshot.ReferenceOpaqueIndirectDraws);
        Assert.Equal(4, snapshot.ReferenceOpaqueIndirectExecuteCalls);
        Assert.Equal(5120, snapshot.ReferenceOpaqueIndirectArgumentBytes);
        Assert.Equal(0, snapshot.ReferenceOpaqueIndirectFallbackReason);
        Assert.Equal(12.5, snapshot.ReferenceOpaqueSubmissionMilliseconds);
        Assert.Equal(3.25, snapshot.ReferenceBlendedSubmissionMilliseconds);

        var fields = RendererProfilerTrace.StatsFields("refs.", snapshot);
        Assert.Equal(101, Assert.IsType<int>(fields["refs.refOpaqueActiveDraws"]));
        Assert.Equal(97, Assert.IsType<int>(fields["refs.refOpaqueSurvivingDraws"]));
        Assert.Equal(23, Assert.IsType<int>(fields["refs.refOpaquePsoTransitions"]));
        Assert.Equal(7, Assert.IsType<int>(fields["refs.refOpaqueUniquePsos"]));
        Assert.Equal(80, Assert.IsType<int>(fields["refs.refOpaqueOrdinaryDraws"]));
        Assert.Equal(5, Assert.IsType<int>(fields["refs.refOpaqueDecalDraws"]));
        Assert.Equal(9, Assert.IsType<int>(fields["refs.refOpaqueGrassCutoutDraws"]));
        Assert.Equal(3, Assert.IsType<int>(fields["refs.refOpaqueGrassDepthWriteDraws"]));
        Assert.True(Assert.IsType<bool>(fields["refs.refOpaqueIndirectActive"]));
        Assert.Equal(17, Assert.IsType<int>(fields["refs.refOpaqueDirectDraws"]));
        Assert.Equal(80, Assert.IsType<int>(fields["refs.refOpaqueIndirectDraws"]));
        Assert.Equal(4, Assert.IsType<int>(fields["refs.refOpaqueIndirectExecuteCalls"]));
        Assert.Equal(5120, Assert.IsType<int>(fields["refs.refOpaqueIndirectArgumentBytes"]));
        Assert.Equal(0, Assert.IsType<int>(fields["refs.refOpaqueIndirectFallbackReason"]));
        Assert.Equal(12.5, Assert.IsType<double>(fields["refs.refOpaqueSubmissionMs"]));
        Assert.Equal(3.25, Assert.IsType<double>(fields["refs.refBlendedSubmissionMs"]));

        stats.Reset();

        Assert.Equal(0, stats.ReferenceOpaqueActiveDraws);
        Assert.Equal(0, stats.ReferenceOpaqueSurvivingDraws);
        Assert.Equal(0, stats.ReferenceOpaquePsoTransitions);
        Assert.Equal(0, stats.ReferenceOpaqueUniquePsos);
        Assert.Equal(0, stats.ReferenceOpaqueOrdinaryDraws);
        Assert.Equal(0, stats.ReferenceOpaqueDecalDraws);
        Assert.Equal(0, stats.ReferenceOpaqueGrassCutoutDraws);
        Assert.Equal(0, stats.ReferenceOpaqueGrassDepthWriteDraws);
        Assert.False(stats.ReferenceOpaqueIndirectActive);
        Assert.Equal(0, stats.ReferenceOpaqueDirectDraws);
        Assert.Equal(0, stats.ReferenceOpaqueIndirectDraws);
        Assert.Equal(0, stats.ReferenceOpaqueIndirectExecuteCalls);
        Assert.Equal(0, stats.ReferenceOpaqueIndirectArgumentBytes);
        Assert.Equal(0, stats.ReferenceOpaqueIndirectFallbackReason);
        Assert.Equal(0, stats.ReferenceOpaqueSubmissionMilliseconds);
        Assert.Equal(0, stats.ReferenceBlendedSubmissionMilliseconds);
    }

    [Fact]
    public void DenseSubmissionLoopsUseCoarseTimersWithoutPerDrawStopwatchReads()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var opaque = SourceContract.Extract(renderer, "private void DrawOpaqueBatches(", "private void DrawBlended(");
        var blendedDraw = SourceContract.Extract(renderer, "private void DrawBlendedSubmesh(",
            "private void UpdateLiveParticleFrames(");

        Assert.DoesNotContain("var cbStarted = StartTiming();", opaque, StringComparison.Ordinal);
        Assert.DoesNotContain("var drawStarted = StartTiming();", opaque, StringComparison.Ordinal);
        Assert.DoesNotContain("Array.Copy(", opaque, StringComparison.Ordinal);
        Assert.DoesNotContain("StartTiming()", blendedDraw, StringComparison.Ordinal);
        Assert.DoesNotContain("ElapsedMilliseconds(", blendedDraw, StringComparison.Ordinal);
        Assert.Contains("LastStats.ReferenceOpaqueSubmissionMilliseconds =", renderer,
            StringComparison.Ordinal);
        Assert.Contains("LastStats.ReferenceBlendedSubmissionMilliseconds +=", renderer,
            StringComparison.Ordinal);

        var aggregate = SourceContract.ReadAppSource("FrameProfileAccumulator.cs");
        Assert.Contains("_refMainCpu += references.CpuFrameMilliseconds;", aggregate,
            StringComparison.Ordinal);
        Assert.Contains("[\"refsCpuAvgMs\"] = Avg(_refMainCpu),", aggregate,
            StringComparison.Ordinal);
        Assert.Contains("[\"refsOpaqueSubmitAvgMs\"] = Avg(_refOpaqueSubmission),", aggregate,
            StringComparison.Ordinal);
        Assert.Contains("[\"refsBlendedSubmitAvgMs\"] = Avg(_refBlendedSubmission),", aggregate,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RendererCountsPostFilterDrawsAndPsoShapeWithoutPerFrameSetAllocation()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var loop = SourceContract.Extract(renderer, "private void DrawOpaqueBatches(", "private void DrawBlended(");

        Assert.Contains(
            "private readonly HashSet<ID3D12PipelineState> _opaqueSubmissionPsos =",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains("_opaqueSubmissionPsos.Clear();", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("new HashSet", loop, StringComparison.Ordinal);
        Assert.Contains("if (batchState.Instances.Count != 0)", loop, StringComparison.Ordinal);
        Assert.Contains("if (drawCount > 0)", loop, StringComparison.Ordinal);
        Assert.Contains("LastStats.ReferenceOpaqueSurvivingDraws++;", loop, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(previousOpaquePso, batchState.Pso)", loop, StringComparison.Ordinal);
        Assert.Contains("LastStats.ReferenceOpaqueUniquePsos = _opaqueSubmissionPsos.Count;", loop,
            StringComparison.Ordinal);

        SourceContract.AssertOrder(
            loop,
            "if (drawCount > 0)",
            "var submitIndirect = useOpaqueIndirect && drawCount > 0 && ordinaryLane;",
            "if (!submitIndirect && drawCount > 0 && !ReferenceEquals(currentPso, batchState.Pso))",
            "cmd.DrawIndexedInstanced((uint)batchState.Submesh.IndexCount, (uint)drawCount, 0, 0, 0);");
    }

    [Fact]
    public void RendererUsesMutuallyExclusiveClearlyClassifiedOrderLanes()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var loop = SourceContract.Extract(renderer, "private void DrawOpaqueBatches(", "private void DrawBlended(");

        Assert.Contains("if (submesh.DepthWritingBlend)", loop, StringComparison.Ordinal);
        Assert.Contains("else if (batchState.UsesGrassDistanceEnvelope)", loop, StringComparison.Ordinal);
        Assert.Contains("else if (submesh.IsDecal)", loop, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            loop,
            "LastStats.ReferenceOpaqueGrassDepthWriteDraws++;",
            "LastStats.ReferenceOpaqueGrassCutoutDraws++;",
            "LastStats.ReferenceOpaqueDecalDraws++;",
            "LastStats.ReferenceOpaqueOrdinaryDraws++;");
    }

    [Fact]
    public void FrameAggregateAccumulatesEmitsLogsAndResetsOpaqueSubmissionShape()
    {
        var source = SourceContract.ReadAppSource("FrameProfileAccumulator.cs");
        var add = SourceContract.Extract(source, "internal void Add(", "internal bool TryFlush(");
        var reset = SourceContract.Extract(source, "private void Reset()", "private static void IncrementHistogram");

        foreach (var name in new[]
                 {
                     "ActiveDraws", "SurvivingDraws", "PsoTransitions", "UniquePsos",
                     "OrdinaryDraws", "DecalDraws", "GrassCutoutDraws", "GrassDepthWriteDraws"
                 })
        {
            Assert.Contains($"_refOpaque{name} += references.ReferenceOpaque{name};", add,
                StringComparison.Ordinal);
            Assert.Contains($"[\"refsOpaque{name}Avg\"] = Avg(_refOpaque{name}),", source,
                StringComparison.Ordinal);
            Assert.Contains($"_refOpaque{name} = 0;", reset, StringComparison.Ordinal);
        }

        Assert.Contains("opaque={Avg(_refOpaqueActiveDraws):0.0}/{Avg(_refOpaqueSurvivingDraws):0.0}",
            source, StringComparison.Ordinal);
        Assert.Contains("pso={Avg(_refOpaqueUniquePsos):0.0}/{Avg(_refOpaquePsoTransitions):0.0}",
            source, StringComparison.Ordinal);
        Assert.Contains("lanes={Avg(_refOpaqueOrdinaryDraws):0.0}/{Avg(_refOpaqueDecalDraws):0.0}/",
            source, StringComparison.Ordinal);
        Assert.Contains("_refOpaqueIndirectDraws += references.ReferenceOpaqueIndirectDraws;", add,
            StringComparison.Ordinal);
        Assert.Contains("_refOpaqueIndirectExecuteCalls += references.ReferenceOpaqueIndirectExecuteCalls;", add,
            StringComparison.Ordinal);
        Assert.Contains("[\"refsOpaqueIndirectDrawsAvg\"] = Avg(_refOpaqueIndirectDraws),", source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"refsOpaqueIndirectExecuteCallsAvg\"] = Avg(_refOpaqueIndirectExecuteCalls),",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_refOpaqueIndirectDraws = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_refOpaqueIndirectExecuteCalls = 0;", reset, StringComparison.Ordinal);
    }
}
