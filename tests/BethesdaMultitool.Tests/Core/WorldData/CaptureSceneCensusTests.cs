using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     The capture completion gate is a FIXPOINT over per-frame censuses: clean (no pending work,
///     no truncated terrain draw) AND unchanged across consecutive samples. These pin the census
///     semantics the profiler's settle loop builds on.
/// </summary>
public sealed class CaptureSceneCensusTests
{
    private static WorldRenderStats CleanRefs()
    {
        return new WorldRenderStats
        {
            ReferenceInstances = 100, ReferenceDrawn = 90, ReferenceSubmeshDraws = 300,
            ReferenceMeshMissing = 10, WaterDraws = 5
        };
    }

    private static WorldRenderStats CleanTerrain()
    {
        return new WorldRenderStats { TerrainDraws = 40 };
    }

    [Fact]
    public void CleanStableScene_IsCleanAndEqual()
    {
        var a = CaptureSceneCensus.From(CleanRefs(), CleanTerrain());
        var b = CaptureSceneCensus.From(CleanRefs(), CleanTerrain());
        Assert.True(a.IsClean);
        Assert.Equal(a, b);
        Assert.Equal(string.Empty, b.DescribeDirt(a));
    }

    [Fact]
    public void PendingWork_MakesCensusDirty_AndIsNamed()
    {
        var refs = CleanRefs();
        refs.ReferenceTexturePending = 3;
        var census = CaptureSceneCensus.From(refs, CleanTerrain());
        Assert.False(census.IsClean);
        Assert.Contains("TexturesWithheld=3", census.DescribeDirt(census), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Active_texture_resolve_makes_census_dirty_and_is_named(bool terrainResolve)
    {
        var refs = CleanRefs();
        var terrain = CleanTerrain();
        if (terrainResolve)
        {
            terrain.TextureActiveResolves = 2;
        }
        else
        {
            refs.ReferenceTextureActiveResolves = 3;
        }

        var census = CaptureSceneCensus.From(refs, terrain);

        Assert.False(census.IsClean);
        Assert.NotEqual(CaptureSceneCensus.From(CleanRefs(), CleanTerrain()), census);
        Assert.Contains(
            terrainResolve
                ? "TerrainTextureActiveResolves=2"
                : "TextureActiveResolves=3",
            census.DescribeDirt(census),
            StringComparison.Ordinal);
    }

    [Fact]
    public void In_progress_reference_batch_build_is_pending_work_and_is_named()
    {
        var refs = CleanRefs();
        refs.ReferenceBatchBuildInProgress = true;

        var census = CaptureSceneCensus.From(refs, CleanTerrain());

        Assert.False(census.IsClean);
        Assert.Contains("ReferenceBatchBuildInProgress=true", census.DescribeDirt(census),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_ceiling_build_is_allowed_when_it_is_the_only_pending_work()
    {
        var refs = CleanRefs();
        refs.ReferenceBatchBuildInProgress = true;
        refs.ReferenceBatchBuildTrigger = CaptureSceneCensus.FrameCeilingBatchBuildTriggerCode;

        var census = CaptureSceneCensus.From(refs, CleanTerrain());

        Assert.False(census.IsClean);
        Assert.True(census.IsCleanOrFrameCeilingMaintenance(refs.ReferenceBatchBuildTrigger));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void In_progress_build_with_any_other_trigger_is_not_maintenance(int trigger)
    {
        var refs = CleanRefs();
        refs.ReferenceBatchBuildInProgress = true;
        refs.ReferenceBatchBuildTrigger = trigger;

        var census = CaptureSceneCensus.From(refs, CleanTerrain());

        Assert.False(census.IsCleanOrFrameCeilingMaintenance(refs.ReferenceBatchBuildTrigger));
    }

    [Fact]
    public void Frame_ceiling_build_does_not_hide_other_pending_work()
    {
        var refs = CleanRefs();
        refs.ReferenceBatchBuildInProgress = true;
        refs.ReferenceBatchBuildTrigger = CaptureSceneCensus.FrameCeilingBatchBuildTriggerCode;
        refs.ReferenceQueuedDecodes = 1;

        var census = CaptureSceneCensus.From(refs, CleanTerrain());

        Assert.False(census.IsCleanOrFrameCeilingMaintenance(refs.ReferenceBatchBuildTrigger));
    }

    [Fact]
    public void Required_cull_refresh_is_pending_work_and_is_named()
    {
        var refs = CleanRefs();
        refs.ReferenceCullRefreshPending = true;

        var census = CaptureSceneCensus.From(refs, CleanTerrain());

        Assert.False(census.IsClean);
        Assert.Contains("ReferenceCullRefreshPending=true", census.DescribeDirt(census),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retryable_mesh_materialization_is_pending_work_and_is_named(bool persistent)
    {
        var refs = CleanRefs();
        if (persistent)
        {
            refs.ReferenceMeshMaterializationRetriesPending = 1;
        }
        else
        {
            refs.ReferenceMeshMaterializationFailures = 1;
        }

        var census = CaptureSceneCensus.From(refs, CleanTerrain());

        Assert.False(census.IsClean);
        Assert.Contains(persistent
                ? "MeshMaterializationRetriesPending=1"
                : "MeshMaterializationFailures=1",
            census.DescribeDirt(census), StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedTerrainDraw_IsDirty()
    {
        // A truncated terrain draw IS a missing cell on screen — a capture must never pass on it.
        var terrain = CleanTerrain();
        terrain.TerrainDrawsTruncated = 1;
        Assert.False(CaptureSceneCensus.From(CleanRefs(), terrain).IsClean);
    }

    [Fact]
    public void ExpandingDemandSet_IsCleanButNotEqual()
    {
        // The gap between streaming waves: both frames show zero pending work, but the second frame
        // admitted more refs — the fixpoint gate must treat this as NOT settled.
        var before = CaptureSceneCensus.From(CleanRefs(), CleanTerrain());
        var moreRefs = CleanRefs();
        moreRefs.ReferenceInstances = 150;
        moreRefs.ReferenceDrawn = 140;
        var after = CaptureSceneCensus.From(moreRefs, CleanTerrain());
        Assert.True(before.IsClean);
        Assert.True(after.IsClean);
        Assert.NotEqual(before, after);
        Assert.Contains("ReferenceInstances 100->150", after.DescribeDirt(before), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledTerrainLayer_ContributesNothing()
    {
        var census = CaptureSceneCensus.From(CleanRefs(), null);
        Assert.True(census.IsClean);
        Assert.Equal(0, census.TerrainDraws);
    }

    [Fact]
    public void DisabledReferenceLayer_ContributesNothingEvenWhenRetainedStatsAreDirty()
    {
        var retained = CleanRefs();
        retained.ReferenceQueuedDecodes = 7;
        retained.ReferenceTexturePending = 11;
        Assert.False(CaptureSceneCensus.From(retained, CleanTerrain()).IsClean);

        // The capture call site maps a disabled Meshes parent to null. Verify that contract erases
        // both pending-work and demand-set values rather than waiting on the renderer's prior frame.
        var census = CaptureSceneCensus.From(null, CleanTerrain());

        Assert.True(census.IsClean);
        Assert.False(census.ReferenceBatchBuildInProgress);
        Assert.False(census.ReferenceCullRefreshPending);
        Assert.Equal(0, census.QueuedDecodes);
        Assert.Equal(0, census.MeshMaterializationRetriesPending);
        Assert.Equal(0, census.TextureActiveResolves);
        Assert.Equal(0, census.TexturesWithheld);
        Assert.Equal(0, census.ReferenceInstances);
        Assert.Equal(0, census.ReferenceDrawn);
        Assert.Equal(0, census.ReferenceSubmeshDraws);
        Assert.Equal(0, census.ReferenceMeshMissing);
    }

    [Fact]
    public void PermanentlyMissingMeshes_DoNotBlockCompletion_ButMovementDoes()
    {
        // Dense regions hold thousands of permanently-unresolvable meshes; a nonzero count must not
        // block completion (that gate would deadlock) — but a CHANGING count means streaming is
        // still resolving, which must.
        var refs = CleanRefs();
        refs.ReferenceMeshMissing = 20_000;
        var a = CaptureSceneCensus.From(refs, CleanTerrain());
        Assert.True(a.IsClean);
        var refsMoved = CleanRefs();
        refsMoved.ReferenceMeshMissing = 20_001;
        var b = CaptureSceneCensus.From(refsMoved, CleanTerrain());
        Assert.NotEqual(a, b);
    }
}
