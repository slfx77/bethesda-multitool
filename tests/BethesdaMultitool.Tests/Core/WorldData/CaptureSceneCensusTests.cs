using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     The capture completion gate is a FIXPOINT over per-frame censuses: clean (no pending work,
///     no truncated terrain draw) AND unchanged across consecutive samples. These pin the census
///     semantics the profiler's settle loop builds on.
/// </summary>
public sealed class CaptureSceneCensusTests
{
    private static WorldRenderStats CleanRefs() => new()
    {
        ReferenceInstances = 100, ReferenceDrawn = 90, ReferenceSubmeshDraws = 300,
        ReferenceMeshMissing = 10, WaterDraws = 5,
    };

    private static WorldRenderStats CleanTerrain() => new() { TerrainDraws = 40 };

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
        var refs = CleanRefs(); refs.ReferenceTexturePending = 3;
        var census = CaptureSceneCensus.From(refs, CleanTerrain());
        Assert.False(census.IsClean);
        Assert.Contains("TexturesWithheld=3", census.DescribeDirt(census), StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedTerrainDraw_IsDirty()
    {
        // A truncated terrain draw IS a missing cell on screen — a capture must never pass on it.
        var terrain = CleanTerrain(); terrain.TerrainDrawsTruncated = 1;
        Assert.False(CaptureSceneCensus.From(CleanRefs(), terrain).IsClean);
    }

    [Fact]
    public void ExpandingDemandSet_IsCleanButNotEqual()
    {
        // The gap between streaming waves: both frames show zero pending work, but the second frame
        // admitted more refs — the fixpoint gate must treat this as NOT settled.
        var before = CaptureSceneCensus.From(CleanRefs(), CleanTerrain());
        var moreRefs = CleanRefs(); moreRefs.ReferenceInstances = 150; moreRefs.ReferenceDrawn = 140;
        var after = CaptureSceneCensus.From(moreRefs, CleanTerrain());
        Assert.True(before.IsClean);
        Assert.True(after.IsClean);
        Assert.NotEqual(before, after);
        Assert.Contains("ReferenceInstances 100->150", after.DescribeDirt(before), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledTerrainLayer_ContributesNothing()
    {
        var census = CaptureSceneCensus.From(CleanRefs(), terrain: null);
        Assert.True(census.IsClean);
        Assert.Equal(0, census.TerrainDraws);
    }

    [Fact]
    public void PermanentlyMissingMeshes_DoNotBlockCompletion_ButMovementDoes()
    {
        // Dense regions hold thousands of permanently-unresolvable meshes; a nonzero count must not
        // block completion (that gate would deadlock) — but a CHANGING count means streaming is
        // still resolving, which must.
        var refs = CleanRefs(); refs.ReferenceMeshMissing = 20_000;
        var a = CaptureSceneCensus.From(refs, CleanTerrain());
        Assert.True(a.IsClean);
        var refsMoved = CleanRefs(); refsMoved.ReferenceMeshMissing = 20_001;
        var b = CaptureSceneCensus.From(refsMoved, CleanTerrain());
        Assert.NotEqual(a, b);
    }
}
