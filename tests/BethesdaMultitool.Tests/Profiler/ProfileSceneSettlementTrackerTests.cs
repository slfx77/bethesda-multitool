using BethesdaMultitool.Core.WorldData;
using BethesdaRendererProfiler;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class ProfileSceneSettlementTrackerTests
{
    [Fact]
    public void Observe_DefaultRequirement_AdmitsAfterFourCleanStableComparisons()
    {
        var tracker = new ProfileSceneSettlementTracker();
        var clean = Census();

        Assert.False(tracker.Observe(clean)); // baseline
        Assert.False(tracker.Observe(clean));
        Assert.False(tracker.Observe(clean));
        Assert.False(tracker.Observe(clean));
        Assert.True(tracker.Observe(clean));

        Assert.Equal(4, tracker.Consecutive);
        Assert.Equal(string.Empty, tracker.LastDirt);
    }

    [Fact]
    public void Observe_DirtyCensus_ResetsConsecutiveAndNamesPendingWork()
    {
        var tracker = new ProfileSceneSettlementTracker(2);
        var clean = Census();
        var dirty = Census(queuedDecodes: 3);

        Assert.False(tracker.Observe(clean));
        Assert.False(tracker.Observe(clean));
        Assert.Equal(1, tracker.Consecutive);

        Assert.False(tracker.Observe(dirty));

        Assert.Equal(0, tracker.Consecutive);
        Assert.Contains("QueuedDecodes=3", tracker.LastDirt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observe_RepeatedActiveTextureResolve_NeverAdmits(bool terrainResolve)
    {
        var tracker = new ProfileSceneSettlementTracker(2);
        var active = Census(activeReferenceTextureResolves: terrainResolve ? 0 : 1,
            activeTerrainTextureResolves: terrainResolve ? 1 : 0);

        Assert.False(tracker.Observe(active));
        Assert.False(tracker.Observe(active));
        Assert.False(tracker.Observe(active));

        Assert.Equal(0, tracker.Consecutive);
        Assert.Contains(
            terrainResolve ? "TerrainTextureActiveResolves=1" : "TextureActiveResolves=1",
            tracker.LastDirt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_CleanDemandSetChange_ResetsConsecutiveAndNamesMovement()
    {
        var tracker = new ProfileSceneSettlementTracker(2);
        var before = Census(100);
        var after = Census(101);

        Assert.False(tracker.Observe(before));
        Assert.False(tracker.Observe(before));
        Assert.Equal(1, tracker.Consecutive);

        Assert.False(tracker.Observe(after));

        Assert.Equal(0, tracker.Consecutive);
        Assert.Contains("ReferenceInstances 100->101", tracker.LastDirt, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_RecoveryRetainsLastNonEmptyDirtUntilAdmission()
    {
        var tracker = new ProfileSceneSettlementTracker(2);
        var dirty = Census(queuedDecodes: 1);
        var clean = Census();

        Assert.False(tracker.Observe(dirty));
        Assert.Contains("QueuedDecodes=1", tracker.LastDirt, StringComparison.Ordinal);

        Assert.False(tracker.Observe(clean)); // changed from dirty: new baseline
        Assert.False(tracker.Observe(clean));
        Assert.True(tracker.Observe(clean));

        Assert.Equal(2, tracker.Consecutive);
        Assert.Contains("QueuedDecodes=1", tracker.LastDirt, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserveForCapture_NormalizesOnlyFrameCeilingMaintenanceAcrossStableDemand()
    {
        var tracker = new ProfileSceneSettlementTracker(2);
        var clean = Census();
        var maintenance = Census(referenceBatchBuildInProgress: true);
        var trigger = CaptureSceneCensus.FrameCeilingBatchBuildTriggerCode;

        Assert.False(tracker.ObserveForCapture(clean, trigger));
        Assert.False(tracker.ObserveForCapture(maintenance, trigger));
        Assert.True(tracker.ObserveForCapture(clean, trigger));

        Assert.Equal(2, tracker.Consecutive);
        Assert.Equal(string.Empty, tracker.LastDirt);
    }

    [Fact]
    public void ObserveForCapture_FrameCeilingNeverExcusesOtherPendingWork()
    {
        var tracker = new ProfileSceneSettlementTracker(2);
        var dirty = Census(
            activeReferenceTextureResolves: 1,
            referenceBatchBuildInProgress: true);
        var trigger = CaptureSceneCensus.FrameCeilingBatchBuildTriggerCode;

        Assert.False(tracker.ObserveForCapture(dirty, trigger));
        Assert.False(tracker.ObserveForCapture(dirty, trigger));
        Assert.False(tracker.ObserveForCapture(dirty, trigger));

        Assert.Equal(0, tracker.Consecutive);
        Assert.Contains("ReferenceBatchBuildInProgress=true", tracker.LastDirt, StringComparison.Ordinal);
        Assert.Contains("TextureActiveResolves=1", tracker.LastDirt, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_StrictProfileGateStillRejectsFrameCeilingMaintenance()
    {
        var tracker = new ProfileSceneSettlementTracker(1);
        var maintenance = Census(referenceBatchBuildInProgress: true);

        Assert.False(tracker.Observe(maintenance));
        Assert.False(tracker.Observe(maintenance));

        Assert.Equal(0, tracker.Consecutive);
        Assert.Contains("ReferenceBatchBuildInProgress=true", tracker.LastDirt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveRequirement(int requiredConsecutive)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProfileSceneSettlementTracker(requiredConsecutive));
    }

    private static CaptureSceneCensus Census(
        int referenceInstances = 100,
        int queuedDecodes = 0,
        int activeReferenceTextureResolves = 0,
        int activeTerrainTextureResolves = 0,
        bool referenceBatchBuildInProgress = false)
    {
        var references = new WorldRenderStats
        {
            ReferenceInstances = referenceInstances,
            ReferenceDrawn = 90,
            ReferenceSubmeshDraws = 300,
            ReferenceMeshMissing = 10,
            ReferenceQueuedDecodes = queuedDecodes,
            ReferenceTextureActiveResolves = activeReferenceTextureResolves,
            ReferenceBatchBuildInProgress = referenceBatchBuildInProgress,
            WaterDraws = 5
        };
        var terrain = new WorldRenderStats
        {
            TerrainDraws = 40,
            TextureActiveResolves = activeTerrainTextureResolves
        };
        return CaptureSceneCensus.From(references, terrain);
    }
}