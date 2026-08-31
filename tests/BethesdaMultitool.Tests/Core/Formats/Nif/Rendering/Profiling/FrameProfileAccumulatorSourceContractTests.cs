using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>
///     Pins the profiler aggregate that lives under App/** and is therefore excluded from the
///     platform-neutral test target. These assertions read the production file rather than copying
///     its arithmetic into a test-only implementation.
/// </summary>
public sealed class FrameProfileAccumulatorSourceContractTests
{
    private static string Source() => SourceContract.ReadAppSource("FrameProfileAccumulator.cs");

    [Theory]
    [InlineData("[\"refsSampleFrames\"] = _refSampleFrames,")]
    [InlineData("[\"refsBatchBuildProcessedAvg\"] = Avg(_refBatchBuildProcessed),")]
    [InlineData("[\"refsBatchBuildTotalAvg\"] = Avg(_refBatchBuildTotal),")]
    [InlineData("[\"refsBatchBuildBudgetAvgMs\"] = Avg(_refBatchBuildBudget),")]
    [InlineData("[\"refsShadowMeshMissingAvg\"] = Avg(_refShadowMeshMissing),")]
    public void Aggregate_emits_the_incremental_build_fields_needed_for_unbiased_analysis(string mapping)
    {
        Assert.Contains(mapping, Source(), StringComparison.Ordinal);
    }

    [Fact]
    public void Historical_mesh_upload_key_aliases_the_renamed_whole_batch_pass()
    {
        var source = Source();

        Assert.Contains("[\"refsBatchBuildAvgMs\"] = Avg(_refBatchBuild),", source, StringComparison.Ordinal);
        Assert.Contains("[\"refsMeshUploadAvgMs\"] = Avg(_refBatchBuild),", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Fps_uses_wall_pacing_while_render_body_remains_separately_observable()
    {
        var source = Source();
        var add = SourceContract.Extract(source, "internal void Add(", "internal bool TryFlush(");

        Assert.Contains("_frameSamples.Add(sample.TotalMilliseconds);", add, StringComparison.Ordinal);
        Assert.Contains("_renderBody += sample.RenderBodyMilliseconds;", add, StringComparison.Ordinal);
        Assert.Contains("[\"renderBodyAvgMs\"] = Avg(_renderBody),", source, StringComparison.Ordinal);

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        Assert.Contains("var frameCompletedTimestamp = Stopwatch.GetTimestamp();", frame,
            StringComparison.Ordinal);
        Assert.Contains("_lastFrameCompletionTimestamp, frameCompletedTimestamp", frame,
            StringComparison.Ordinal);
        Assert.Contains("_lastFrameCompletionTimestamp = frameCompletedTimestamp;", frame,
            StringComparison.Ordinal);
        Assert.Contains("TotalMilliseconds: wallFrameMs,", frame, StringComparison.Ordinal);
        Assert.Contains("RenderBodyMilliseconds: renderBodyMs,", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_end_shadow_cpu_work_is_timed_outside_the_main_reference_stage()
    {
        var aggregate = Source();
        Assert.Contains("_shadowFrame += sample.ShadowMilliseconds;", aggregate,
            StringComparison.Ordinal);
        Assert.Contains("[\"shadowCpuAvgMs\"] = Avg(_shadowFrame),", aggregate,
            StringComparison.Ordinal);
        Assert.Contains("_shadowFrame = 0;", aggregate, StringComparison.Ordinal);

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var shadowBlock = SourceContract.Extract(
            frame,
            "var shadowMs = 0.0;",
            "segmentStarted = StartProfileTimestamp();\n        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ResolveStart);");
        SourceContract.AssertOrder(
            shadowBlock,
            "segmentStarted = StartProfileTimestamp();",
            "RecordSunShadowPass(cmd, referenceRenderOrigin, _camera.Position);",
            "shadowMs = ElapsedMilliseconds(segmentStarted);");
        Assert.Contains("ShadowMilliseconds: shadowMs,", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Scene_load_reseeds_all_pacing_windows_after_world_selection_finishes()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.xaml.cs");
        var load = SourceContract.Extract(source, "internal void LoadData(WorldViewData data)",
            "internal void SelectObject(");

        SourceContract.AssertOrder(load,
            "WorldspaceComboBox.SelectedIndex = SelectInitialWorldspaceIndex(data);",
            "ReseedFrameClocks();",
            "_frameDeltaFilter.Reset();",
            "_performanceSampler.Reset();",
            "_profileAccumulator.ResetForSceneLoad();");
    }

    [Fact]
    public void Tile_light_ab_fields_are_aggregated_and_reset_with_the_profile_window()
    {
        var source = Source();
        var add = SourceContract.Extract(source, "internal void Add(", "internal bool TryFlush(");
        var reset = SourceContract.Extract(source, "private void Reset()", "private static void IncrementHistogram");

        Assert.Contains("_refLightTileBuild += references.ReferencePlacedLightTileBuildMilliseconds;",
            add, StringComparison.Ordinal);
        Assert.Contains("[\"refsPlacedLightsAvg\"] = Avg(_refPlacedLights),", source,
            StringComparison.Ordinal);
        Assert.Contains("[\"refsLightTileFallbackHistogram\"] = SnapshotStringHistogram(_refLightTileFallbacks),",
            source, StringComparison.Ordinal);
        Assert.Contains("_refLightTileFallbacks.Clear();", reset, StringComparison.Ordinal);
    }

    [Fact]
    public void Incremental_aggregate_fields_are_accumulated_and_cleared_after_flush()
    {
        var source = Source();
        var add = SourceContract.Extract(source, "internal void Add(", "internal bool TryFlush(");
        var reset = SourceContract.Extract(source, "private void Reset()", "private static void IncrementHistogram");

        Assert.Contains("_refBatchBuildProcessed += references.ReferenceBatchBuildProcessed;", add,
            StringComparison.Ordinal);
        Assert.Contains("_refBatchBuildTotal += references.ReferenceBatchBuildTotal;", add,
            StringComparison.Ordinal);
        Assert.Contains("_refBatchBuildBudget += references.ReferenceBatchBuildBudgetMilliseconds;", add,
            StringComparison.Ordinal);
        Assert.Contains("_refShadowMeshMissing += references.ReferenceShadowMeshMissing;", add,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (references.ReferenceBatchBuildIncrementalAdvanced) _refIncrementalBuildFrames++;",
            add,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (references.ReferenceBatchBuildInProgress) _refIncrementalBuildFrames++;",
            add,
            StringComparison.Ordinal);

        Assert.Contains("_refBatchBuildProcessed = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_refBatchBuildTotal = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_refBatchBuildBudget = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_refShadowMeshMissing = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_refReuseBlockers.Clear();", reset, StringComparison.Ordinal);
        Assert.Contains("_refBuildTriggers.Clear();", reset, StringComparison.Ordinal);
        Assert.Contains("_refBuildPhases.Clear();", reset, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_blockers_are_emitted_as_interval_maxima_and_reset_after_flush()
    {
        var source = Source();
        var add = SourceContract.Extract(source, "internal void Add(", "internal bool TryFlush(");
        var reset = SourceContract.Extract(source, "private void Reset()", "private static void IncrementHistogram");

        Assert.Contains(
            "_refCullRefreshPending |= references.ReferenceCullRefreshPending;",
            add,
            StringComparison.Ordinal);
        Assert.Contains(
            "references.ReferenceMeshMaterializationRetriesPending);",
            add,
            StringComparison.Ordinal);
        Assert.Contains("[\"refCullRefreshPending\"] = _refCullRefreshPending,", source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"refMeshMaterializationPending\"] = _refMeshMaterializationPending,",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_refTextureActiveResolvesMax = Math.Max(",
            add,
            StringComparison.Ordinal);
        Assert.Contains(
            "_terrainTextureActiveResolvesMax = Math.Max(",
            add,
            StringComparison.Ordinal);
        Assert.Contains("[\"refTextureActiveResolvesMax\"] = _refTextureActiveResolvesMax,",
            source, StringComparison.Ordinal);
        Assert.Contains("[\"terrainTextureActiveResolvesMax\"] = _terrainTextureActiveResolvesMax,",
            source, StringComparison.Ordinal);
        Assert.Contains("var sceneCensus = CaptureSceneCensus.From(references, terrain);",
            add, StringComparison.Ordinal);
        Assert.Contains("if (!sceneCensus.IsClean)", add, StringComparison.Ordinal);
        Assert.Contains("_sceneCensusDirtyFrames++;", add, StringComparison.Ordinal);
        Assert.Contains("[\"sceneCensusDirtyFrames\"] = _sceneCensusDirtyFrames,",
            source, StringComparison.Ordinal);
        Assert.Contains("if (!sceneCensus.IsCleanOrFrameCeilingMaintenance(",
            add, StringComparison.Ordinal);
        Assert.Contains("references?.ReferenceBatchBuildTrigger ?? 0))",
            add, StringComparison.Ordinal);
        Assert.Contains("_sceneNonMaintenanceDirtyFrames++;", add, StringComparison.Ordinal);
        Assert.Contains(
            "[\"sceneNonMaintenanceDirtyFrames\"] = _sceneNonMaintenanceDirtyFrames,",
            source, StringComparison.Ordinal);
        Assert.Contains("_refCullRefreshPending = false;", reset, StringComparison.Ordinal);
        Assert.Contains("_refMeshMaterializationPending = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_refTextureActiveResolvesMax = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_terrainTextureActiveResolvesMax = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_sceneCensusDirtyFrames = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_sceneNonMaintenanceDirtyFrames = 0;", reset, StringComparison.Ordinal);
    }

    [Fact]
    public void Terrain_frustum_fields_reach_every_normal_aggregate_and_reset_after_flush()
    {
        var source = Source();
        var add = SourceContract.Extract(source, "internal void Add(", "internal bool TryFlush(");
        var reset = SourceContract.Extract(source, "private void Reset()", "private static void IncrementHistogram");

        Assert.Contains("_terrainSampleFrames++;", add, StringComparison.Ordinal);
        Assert.Contains(
            "if (terrain.TerrainFrustumCullingActive) _terrainFrustumActiveFrames++;",
            add,
            StringComparison.Ordinal);
        Assert.Contains("_terrainFrustumRejected += terrain.TerrainFrustumRejected;", add,
            StringComparison.Ordinal);

        // Unconditional dictionary members: hitch-free intervals still expose proof that the cull
        // was active and how many resident candidates it rejected on an average sampled frame.
        Assert.Contains(
            "[\"terrainFrustumActiveRate\"] = Rate(_terrainFrustumActiveFrames, _terrainSampleFrames),",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"terrainFrustumRejectedAvg\"] = TerrainAvg(_terrainFrustumRejected),",
            source,
            StringComparison.Ordinal);
        Assert.Contains("frustum={Rate(_terrainFrustumActiveFrames, _terrainSampleFrames):0.00}",
            source, StringComparison.Ordinal);
        Assert.Contains("reject={TerrainAvg(_terrainFrustumRejected):0.0}", source,
            StringComparison.Ordinal);

        Assert.Contains("_terrainSampleFrames = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_terrainFrustumActiveFrames = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_terrainFrustumRejected = 0;", reset, StringComparison.Ordinal);
    }

    [Fact]
    public void Terrain_frustum_rejection_doc_includes_cached_draw_hysteresis_candidates()
    {
        var stats = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "WorldData", "WorldRenderStats.cs");

        Assert.Contains("including cached cells in its", stats, StringComparison.Ordinal);
        Assert.Contains("one-cell draw-only hysteresis margin", stats, StringComparison.Ordinal);
    }
}
