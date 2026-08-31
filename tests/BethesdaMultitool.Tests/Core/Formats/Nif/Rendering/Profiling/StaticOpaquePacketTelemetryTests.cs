using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

public sealed class StaticOpaquePacketTelemetryTests
{
    [Fact]
    public void PacketTelemetrySurvivesSnapshotTraceAndReset()
    {
        var stats = new WorldRenderStats
        {
            ReferenceStaticOpaquePacketActive = true,
            ReferenceStaticOpaquePacketHit = true,
            ReferenceStaticOpaquePacketBatches = 73,
            ReferenceStaticOpaquePacketInstances = 8192,
            ReferenceStaticOpaquePacketRuns = 3,
            ReferenceStaticOpaquePacketBytes = 600_000,
            ReferenceStaticOpaquePacketBuildMilliseconds = 1.25,
            ReferenceStaticOpaquePacketSavedMatrixBytes = 524_288,
            ReferenceStaticOpaquePacketSavedConstantBytes = 18_688,
            ReferenceStaticOpaquePacketSavedArgumentBytes = 4_672,
            ReferenceStaticOpaquePacketFallbackReason = 0
        };

        var snapshot = stats.Snapshot();
        Assert.True(snapshot.ReferenceStaticOpaquePacketActive);
        Assert.True(snapshot.ReferenceStaticOpaquePacketHit);
        Assert.Equal(73, snapshot.ReferenceStaticOpaquePacketBatches);
        Assert.Equal(8192, snapshot.ReferenceStaticOpaquePacketInstances);
        Assert.Equal(3, snapshot.ReferenceStaticOpaquePacketRuns);
        Assert.Equal(600_000, snapshot.ReferenceStaticOpaquePacketBytes);
        Assert.Equal(1.25, snapshot.ReferenceStaticOpaquePacketBuildMilliseconds);
        Assert.Equal(524_288, snapshot.ReferenceStaticOpaquePacketSavedMatrixBytes);
        Assert.Equal(18_688, snapshot.ReferenceStaticOpaquePacketSavedConstantBytes);
        Assert.Equal(4_672, snapshot.ReferenceStaticOpaquePacketSavedArgumentBytes);
        Assert.Equal(0, snapshot.ReferenceStaticOpaquePacketFallbackReason);

        var fields = RendererProfilerTrace.StatsFields("refs.", snapshot);
        Assert.True(Assert.IsType<bool>(fields["refs.refStaticOpaquePacketActive"]));
        Assert.True(Assert.IsType<bool>(fields["refs.refStaticOpaquePacketHit"]));
        Assert.Equal(73, Assert.IsType<int>(fields["refs.refStaticOpaquePacketBatches"]));
        Assert.Equal(8192, Assert.IsType<int>(fields["refs.refStaticOpaquePacketInstances"]));
        Assert.Equal(3, Assert.IsType<int>(fields["refs.refStaticOpaquePacketRuns"]));
        Assert.Equal(600_000, Assert.IsType<int>(fields["refs.refStaticOpaquePacketBytes"]));
        Assert.Equal(1.25, Assert.IsType<double>(fields["refs.refStaticOpaquePacketBuildMs"]));
        Assert.Equal(524_288, Assert.IsType<int>(fields["refs.refStaticOpaquePacketSavedMatrixBytes"]));
        Assert.Equal(18_688, Assert.IsType<int>(fields["refs.refStaticOpaquePacketSavedConstantBytes"]));
        Assert.Equal(4_672, Assert.IsType<int>(fields["refs.refStaticOpaquePacketSavedArgumentBytes"]));
        Assert.Equal(0, Assert.IsType<int>(fields["refs.refStaticOpaquePacketFallbackReason"]));

        stats.Reset();
        Assert.False(stats.ReferenceStaticOpaquePacketActive);
        Assert.False(stats.ReferenceStaticOpaquePacketHit);
        Assert.Equal(0, stats.ReferenceStaticOpaquePacketBatches);
        Assert.Equal(0, stats.ReferenceStaticOpaquePacketInstances);
        Assert.Equal(0, stats.ReferenceStaticOpaquePacketRuns);
        Assert.Equal(0, stats.ReferenceStaticOpaquePacketBytes);
        Assert.Equal(0, stats.ReferenceStaticOpaquePacketBuildMilliseconds);
        Assert.Equal(0, stats.ReferenceStaticOpaquePacketSavedMatrixBytes);
        Assert.Equal(0, stats.ReferenceStaticOpaquePacketSavedConstantBytes);
        Assert.Equal(0, stats.ReferenceStaticOpaquePacketSavedArgumentBytes);
        Assert.Equal(0, stats.ReferenceStaticOpaquePacketFallbackReason);
    }

    [Fact]
    public void PacketTelemetryIsAggregatedAndHarnessedAsAnExplicitSameBinarySwitch()
    {
        var accumulator = SourceContract.ReadAppSource("FrameProfileAccumulator.cs");
        var add = SourceContract.Extract(accumulator, "internal void Add(", "internal bool TryFlush(");
        var reset = SourceContract.Extract(
            accumulator,
            "private void Reset()",
            "private static void IncrementHistogram");

        Assert.Contains("if (references.ReferenceStaticOpaquePacketActive)", add,
            StringComparison.Ordinal);
        Assert.Contains("if (references.ReferenceStaticOpaquePacketHit)", add,
            StringComparison.Ordinal);
        Assert.Contains(
            "_refStaticOpaquePacketInstances += references.ReferenceStaticOpaquePacketInstances;",
            add,
            StringComparison.Ordinal);
        Assert.Contains("[\"refsStaticOpaquePacketHitRate\"] =", accumulator,
            StringComparison.Ordinal);
        Assert.Contains("[\"refsStaticOpaquePacketSavedMatrixBytesAvg\"] =", accumulator,
            StringComparison.Ordinal);
        Assert.Contains("packet={Avg(_refStaticOpaquePacketBatches):0.0}/", accumulator,
            StringComparison.Ordinal);
        Assert.Contains("_refStaticOpaquePacketHitFrames = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_refStaticOpaquePacketFallbacks.Clear();", reset, StringComparison.Ordinal);

        var environment = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "EnvironmentVariables.cs");
        Assert.Contains("FALLOUT_VIEWER_REFERENCE_STATIC_OPAQUE_PACKET", environment,
            StringComparison.Ordinal);

        var profiler = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "Program.cs");
        Assert.Contains("[\"referenceStaticOpaquePacket\"]", profiler, StringComparison.Ordinal);

        var harness = SourceContract.ReadSource("scratchpad", "live_profiles", "run_live.ps1");
        Assert.Contains("[string]$ExpectedStaticOpaquePacket = ''", harness, StringComparison.Ordinal);
        Assert.Contains("refsStaticOpaquePacketHitRate", harness, StringComparison.Ordinal);
        Assert.Contains("FALLOUT_VIEWER_REFERENCE_STATIC_OPAQUE_PACKET", harness,
            StringComparison.Ordinal);
    }
}
