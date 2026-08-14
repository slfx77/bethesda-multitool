using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Pins the visibility boundary shared by live references, walk collision, and the collision
///     diagnostic. These are source contracts because the WinUI/D3D12 host is excluded from the
///     cross-platform test assembly; ordering matters here because a late filter would still warm
///     hidden collision meshes.
/// </summary>
public sealed class WorldViewCollisionVisibilitySourceContractTests
{
    [Fact]
    public void WalkCandidatesApplyParentAndCategoryGatesBeforeEveryWarmOrColdPath()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.GroundRaycast.cs");
        var build = SourceContract.Extract(
            source,
            "private void BuildRaycastCandidates(",
            "private static void TryAddWarmRaycastCandidate(");

        SourceContract.AssertOrder(
            build,
            "into.Clear();",
            "_coldGroundCandidates.Clear();",
            "_walkCollisionWarmupCandidates.Clear();",
            "if (!_showReferences) return;",
            "var category = _data?.CategoryIndex.GetValueOrDefault(",
            "if (_hiddenCategories.Contains(category)) continue;",
            "var resolution = _referenceMeshCache12?.ResolveCollisionMesh(",
            "_coldGroundCandidates.Add(",
            "_walkCollisionWarmupCandidates.Add(",
            "_walkCollisionWarmupResolver.WarmNearest(",
            "foreach (var cold in _coldGroundCandidates)");
    }

    [Fact]
    public void TerrainGroundingRemainsIndependentOfTheTerrainPresentationToggle()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.GroundRaycast.cs");
        var capsule = SourceContract.Extract(
            source,
            "private float? SampleGroundHeightCapsule(",
            "private float? SampleGroundAt(");
        var sample = SourceContract.Extract(
            source,
            "private float? SampleGroundAt(",
            "private void BuildRaycastCandidates(");

        Assert.Contains("BuildRaycastCandidates(", capsule, StringComparison.Ordinal);
        Assert.Contains("SampleGroundAt(", capsule, StringComparison.Ordinal);
        Assert.DoesNotContain("_showTerrain", capsule, StringComparison.Ordinal);
        Assert.Contains("TerrainHeightSampler.Sample(", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("_showTerrain", sample, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalCollisionFailureRetainsBoundsFallbackButIsNotOfferedForWarmup()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.GroundRaycast.cs");
        var build = SourceContract.Extract(
            source,
            "private void BuildRaycastCandidates(",
            "private static void TryAddWarmRaycastCandidate(");

        Assert.Contains(
            "var allowsBoundsFallback = !resolution.IsResolved &&",
            build,
            StringComparison.Ordinal);
        Assert.Contains(
            "var allowsWarmup = _referenceMeshCache12 is not null && resolution.ShouldOfferWarmup;",
            build,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            build,
            "var allowsBoundsFallback = !resolution.IsResolved &&",
            "var allowsWarmup = _referenceMeshCache12 is not null && resolution.ShouldOfferWarmup;",
            "_coldGroundCandidates.Add(",
            "if (allowsWarmup)",
            "_walkCollisionWarmupCandidates.Add(");
    }

    [Fact]
    public void CollisionOverlayFiltersBeforePriorityResolutionAndColdWarmup()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "CollisionDebugRenderer12.cs");
        var render = SourceContract.Extract(
            renderer,
            "public int Render(",
            "private ID3D12Resource UploadGeometry(");

        SourceContract.AssertOrder(
            render,
            "bool showReferences = true",
            "IReadOnlyCollection<PlacedObjectCategory>? hiddenCategories = null",
            "if (!showReferences || _disposed",
            "var category = _categoryIndex?.GetValueOrDefault(",
            "if (hiddenCategories?.Contains(category) == true) continue;",
            "_candidateScratch.Add(",
            "_priorityResolver.Resolve(");
    }

    [Fact]
    public void LiveAndExportOverlayCallsPassTheirOwnReferenceVisibilityPolicy()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var live = SourceContract.Extract(
            frame,
            "var wantsSelectionOverlay",
            "// Export framing preview");
        Assert.Contains(
            "var wantsCollisionOverlay = _showReferences && _showCollision && _collisionDebug is not null;",
            live,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (wantsCollisionOverlay && _collisionDebug is not null)",
            live,
            StringComparison.Ordinal);
        Assert.Contains("showReferences: _showReferences", live, StringComparison.Ordinal);
        Assert.Contains("hiddenCategories: _hiddenCategories", live, StringComparison.Ordinal);

        var export = SourceContract.ReadAppSource("WorldView3DControl.Export3D.cs");
        var exportOverlay = SourceContract.Extract(
            export,
            "// An offscreen export has no XAML composition scale",
            "if (opts.ShowGrid)");
        Assert.Contains(
            "if (opts.ShowReferences && opts.ShowCollision)",
            exportOverlay,
            StringComparison.Ordinal);
        Assert.Contains("showReferences: opts.ShowReferences", exportOverlay, StringComparison.Ordinal);
        Assert.Contains("hiddenCategories: opts.HiddenCategories", exportOverlay, StringComparison.Ordinal);
    }
}
