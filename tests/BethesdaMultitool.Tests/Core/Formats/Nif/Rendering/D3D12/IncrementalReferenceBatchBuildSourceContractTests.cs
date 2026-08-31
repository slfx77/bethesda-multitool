using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Integration pins for the D3D12-only reference builder. The live type requires a device and
///     command recorder, so the pure hard/soft decision is tested separately and these assertions
///     protect the render-thread wiring that cannot be instantiated headlessly.
/// </summary>
public sealed class IncrementalReferenceBatchBuildSourceContractTests
{
    private static string RendererSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
        "ReferenceRenderer12.cs");

    private static string RegistrySource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
        "OpaqueBatchRegistry12.cs");

    [Fact]
    public void Published_and_staging_aggregates_are_distinct_and_swap_only_at_publication()
    {
        var source = RendererSource();

        Assert.Contains("private ReferenceBatchSet _publishedBatchSet = new();", source, StringComparison.Ordinal);
        Assert.Contains("private ReferenceBatchSet _stagingBatchSet = new();", source, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "private void PublishBatchBuild(",
            "_publishedBatchSet = state.Target;");
        Assert.DoesNotContain("_publishedBatchSet.StartReset();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_policy_checks_all_hard_keys_instead_of_trusting_the_first_blocker()
    {
        var source = RendererSource();

        Assert.Contains("ReferenceBatchBuildPolicy.CanAmortize(", source, StringComparison.Ordinal);
        Assert.Contains("refreshOnlyBlocker: refreshOnlyBlocker || validStagedContinuation", source, StringComparison.Ordinal);
        Assert.Contains("cullEpochMatches: validStagedContinuation", source, StringComparison.Ordinal);
        Assert.Contains("|| _lastBuildCullEpoch == _cullEpoch", source, StringComparison.Ordinal);
        Assert.Contains("var meshBoundsOnlyCullRefresh = !meshBoundsCurrent && cullCacheStructureValid;",
            source, StringComparison.Ordinal);
        Assert.Contains("renderOriginMatches: _lastBuildRenderOrigin == renderOrigin", source, StringComparison.Ordinal);
        Assert.Contains("evictionGenerationMatches: _lastBuildEvictionGen == _meshCache.EvictionGeneration", source, StringComparison.Ordinal);
        Assert.Contains("streamRoutingMatches: _lastBuildStreamActive == _transparencyStreamActive", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_staged_build_pins_only_the_mesh_bounds_generation_until_publication()
    {
        var source = RendererSource();

        const string declarationMarker =
            "var stagedBuildPinsCullSnapshot = _batchBuildState is not null";
        var declarationStart = source.IndexOf(declarationMarker, StringComparison.Ordinal);
        Assert.True(declarationStart >= 0, "staged-build cull pin declaration is missing");
        var declarationEnd = source.IndexOf("var meshBoundsCurrent", declarationStart,
            StringComparison.Ordinal);
        Assert.True(declarationEnd > declarationStart,
            "staged-build cull pin must be immediately followed by meshBoundsCurrent");
        var declaration = source[declarationStart..declarationEnd];
        Assert.Contains("&& BatchBuildMatchesCurrent(", declaration, StringComparison.Ordinal);
        Assert.Contains("|| stagedBuildPinsCullSnapshot", source, StringComparison.Ordinal);
        // The pin is one input to meshBoundsCurrent, while every structural cull key remains an
        // independent requirement. It must never turn a camera/filter/ring invalidation into a hit.
        Assert.Contains("var cullCacheValid = meshBoundsCurrent && cullCacheStructureValid;", source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "var stagedBuildPinsCullSnapshot",
            "var meshBoundsCurrent",
            "var cullCacheStructureValid",
            "var cullCacheValid = meshBoundsCurrent && cullCacheStructureValid;");
    }

    [Fact]
    public void Partial_slices_keep_resolve_admission_stable_and_do_not_complete_the_skinner_pass()
    {
        var source = RendererSource();

        Assert.Contains("Dictionary<uint, BatchMeshSnapshot>", source, StringComparison.Ordinal);
        Assert.Contains("bool TexturesReady", source, StringComparison.Ordinal);
        Assert.Contains("bool MainAdmitted", source, StringComparison.Ordinal);
        Assert.Contains("resolveRan: buildPublished", source, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "private void PublishBatchBuild(",
            "_skinner.Register(mesh, distanceSquared);");
    }

    [Fact]
    public void Eviction_during_a_slice_cannot_draw_the_old_published_views()
    {
        var source = RendererSource();

        Assert.Contains("BatchBuildAdvanceResult.EvictionInvalidated", source, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "if (advance == BatchBuildAdvanceResult.EvictionInvalidated)",
            "TryCompleteSynchronousBatchBuild(");
        Assert.Contains("ClearPublishedBatchSnapshotAfterEviction();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Inactive_registry_reset_and_cascade_sort_are_both_resumable()
    {
        var renderer = RendererSource();
        var registry = RegistrySource();

        Assert.Contains("ContinueBegin(resetWork)", renderer, StringComparison.Ordinal);
        Assert.Contains("private bool SortBatchInstancesByCascade(BatchBuildState build, OpaqueBatchState batch)",
            renderer, StringComparison.Ordinal);
        Assert.Contains("const int sortWorkChunk = 512;", renderer, StringComparison.Ordinal);
        Assert.Contains("CascadeBatchSortPhase.MainClassify", renderer, StringComparison.Ordinal);
        Assert.Contains("CascadeBatchSortPhase.MainScatter", renderer, StringComparison.Ordinal);
        Assert.Contains("CascadeBatchSortPhase.MainCopy", renderer, StringComparison.Ordinal);
        Assert.Contains("public void StartBegin()", registry, StringComparison.Ordinal);
        Assert.Contains("public bool ContinueBegin(int batchBudget)", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void Slice_budget_has_a_stable_default_and_bounded_environment_override()
    {
        var source = RendererSource();

        SourceContract.AssertOrder(
            source,
            "EnvironmentVariables.Viewer.ReferenceBatchBuildMillisecondsPerFrame",
            "defaultValue: 2.0",
            "min: 0.25",
            "max: 10.0");
    }

    [Fact]
    public void First_incremental_slice_charges_staging_setup_to_the_same_budget_clock()
    {
        var source = RendererSource();
        SourceContract.AssertOrder(
            source,
            "var batchSliceStarted = Stopwatch.GetTimestamp();",
            "_batchBuildState ??= StartBatchBuild(",
            "var advance = AdvanceBatchBuild(",
            "sliceStartedTimestamp: batchSliceStarted");

        var advance = SourceContract.Extract(
            source,
            "private BatchBuildAdvanceResult AdvanceBatchBuild(",
            "private BatchMeshSnapshot ResolveBatchMesh(");
        Assert.Contains("long sliceStartedTimestamp", advance, StringComparison.Ordinal);
        Assert.Contains(
            "Stopwatch.GetElapsedTime(sliceStartedTimestamp).TotalMilliseconds",
            advance,
            StringComparison.Ordinal);
        Assert.DoesNotContain("var sliceStarted = Stopwatch.GetTimestamp();", advance,
            StringComparison.Ordinal);
        Assert.Contains("sliceStartedTimestamp: 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Settled_terminal_batches_do_not_rebuild_on_an_unconditional_frame_ceiling()
    {
        var source = RendererSource();
        var gate = SourceContract.Extract(
            source,
            "BatchReuseBlocker ResolveReuseBlocker()",
            "// True once the unresolved-mesh population");

        Assert.DoesNotContain("BatchReuseMaxFrames", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceBatchBuildPolicy.CanSkipPeriodicRefresh(", gate,
            StringComparison.Ordinal);
        Assert.Contains("if (_framesSinceBuild < int.MaxValue)", source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            gate,
            "var hasMissingMeshes",
            "return BatchReuseBlocker.MeshesMissing;",
            "ReferenceBatchBuildPolicy.CanSkipPeriodicRefresh(",
            "return BatchReuseBlocker.FrameCeiling;");
    }

    [Fact]
    public void Abandoned_uploads_and_stale_shadow_fits_cannot_publish_silently()
    {
        var source = RendererSource();

        Assert.Contains("private bool _batchContentDirtyPending;", source, StringComparison.Ordinal);
        Assert.Contains("_batchContentDirtyPending |= _meshCache.FrameGpuUploads != 0;",
            source, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "var contentChanged = _batchContentDirtyPending",
            "_batchContentDirtyPending = false;");
        Assert.Contains("state.Target.CascadeFit = SnapshotCascadeFit(state.CascadeFit);",
            source, StringComparison.Ordinal);
        Assert.Contains("CullPositionSlack * CullPositionSlack * 3.0001f", source,
            StringComparison.Ordinal);
        Assert.Contains("_cullCacheCascadeAnchorXY = _cascadeFit is", source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var useCascadePrefixes = CascadeFitsCompatible(_publishedBatchSet.CascadeFit, _cascadeFit);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Reused_blended_draws_apply_the_same_exact_reference_cull_as_opaque_instances()
    {
        var source = RendererSource();

        Assert.Contains("Vector4 ReferenceBounds", source, StringComparison.Ordinal);
        Assert.Contains("PassesExactCull(draw.ReferenceBounds)", source, StringComparison.Ordinal);
    }
}
