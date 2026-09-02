using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Wiring pins for the device-local reference-geometry experiment. The live cache needs a D3D12
///     device, so these contracts complement the pure mode/ring tests by protecting the command-list
///     and collision-only boundaries in production source.
/// </summary>
public sealed class DefaultReferenceGeometrySourceContractTests
{
    private static string CacheSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
        "ReferenceMeshCache12.cs");

    private static string RendererSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
        "ReferenceRenderer12.cs");

    private static string RecorderSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
        "GpuCommandRecorder12.cs");

    [Fact]
    public void Effective_mode_is_fail_closed_and_recorded_for_unattended_captures()
    {
        var source = CacheSource();

        Assert.Contains("GpuGeometryArenaBackingModePolicy.Parse(requestedGeometryBacking)",
            source, StringComparison.Ordinal);
        Assert.Contains("GeometryArenaDiagnostics.ValidateLevel >= 2", source,
            StringComparison.Ordinal);
        Assert.Contains("RendererProfilerTrace.Event(\"reference-geometry-backing\"", source,
            StringComparison.Ordinal);
        Assert.Contains("[\"effective\"] = geometryBackingMode.ToString()", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Render_resolution_carries_the_open_command_list_to_the_arena_copy()
    {
        var renderer = RendererSource();
        var cache = CacheSource();

        Assert.Contains("_meshCache.GetOrUpload(\n            commandList,", renderer,
            StringComparison.Ordinal);
        Assert.Contains("public CachedNifMesh12? GetOrUpload(\n        ID3D12GraphicsCommandList commandList,",
            cache, StringComparison.Ordinal);
        Assert.Contains("geometryArena.Upload(\n                    commandList ?? throw new InvalidOperationException(",
            cache, StringComparison.Ordinal);
    }

    [Fact]
    public void Collision_warmup_publishes_cpu_soup_before_every_gpu_admission_counter()
    {
        var source = CacheSource();

        SourceContract.AssertOrder(
            source,
            "if (collisionOnly)",
            "StoreCollisionMesh(modelPath, node, decoded.Mesh);",
            "if (commandList is null)",
            "if (uploadBudget <= 0)",
            "FrameGpuUploads++;",
            "StoreCollisionMesh(modelPath, node, decoded.Mesh);",
            "var materialization = UploadDecodedMesh(");
    }

    [Fact]
    public void Retryable_materialization_failure_never_becomes_resolved_null()
    {
        var source = CacheSource();

        SourceContract.AssertOrder(
            source,
            "if (materialization.Status == MeshMaterializationStatus.RetryableFailure)",
            "MarkMaterializationRetry(node);",
            "if (materialization.Status == MeshMaterializationStatus.RenderEmpty)",
            "node.ResolvedNull = true;");
        Assert.Contains("return MeshMaterializationResult.RetryableFailure;", source,
            StringComparison.Ordinal);
        Assert.Contains("return MeshMaterializationResult.RenderEmpty;", source,
            StringComparison.Ordinal);
        Assert.Contains("ReferenceMeshMaterializationRetriesPending", RendererSource(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Materialization_retry_debt_tracks_fresh_camera_demand_not_all_resident_nodes()
    {
        var cache = CacheSource();
        var renderer = RendererSource();
        const string beginGeneration =
            "_meshCache.BeginMaterializationRetryDemandGeneration();";

        Assert.Contains("private ulong _materializationRetryDemandGeneration = 1;", cache,
            StringComparison.Ordinal);
        Assert.Contains("node.MaterializationRetryDemandGeneration ==\n" +
                        "            _materializationRetryDemandGeneration", cache,
            StringComparison.Ordinal);
        Assert.Contains("node.MaterializationRetryDemandGeneration = 0;", cache,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MaterializationRetryPending", cache, StringComparison.Ordinal);

        // StartBatchBuild is the only fresh-state constructor. Valid staged continuations call
        // AdvanceBatchBuild directly, so they must not erase retry debt accumulated by prior slices.
        Assert.Equal(1, renderer.Split(beginGeneration, StringSplitOptions.None).Length - 1);
        SourceContract.AssertOrder(
            renderer,
            "private BatchBuildState StartBatchBuild(",
            beginGeneration,
            "_stagingBatchSet.StartReset();");

        // Terminal render-null resolution cannot keep even current-demand retry debt alive.
        Assert.Contains("if (node.ResolvedNull)\n        {\n" +
                        "            // Terminal render-null nodes never carry materialization work.",
            cache, StringComparison.Ordinal);
        Assert.Contains("node.ResolvedNull = true;\n            ClearMaterializationRetry(node);",
            cache, StringComparison.Ordinal);

        // A retry from an older camera demand must be counted as soon as a new render traversal
        // revisits it. Waiting until UploadDecodedMesh would lose the debt whenever an admission
        // budget defers that upload, allowing a false quiescent publication.
        SourceContract.AssertOrder(
            cache,
            "private CachedNifMesh12? ResolveExisting(",
            "if (!collisionOnly && node.MaterializationRetryDemandGeneration != 0)",
            "MarkMaterializationRetry(node, countFrameFailure: false);",
            "private bool TryResolveFromDecodedCache(",
            "if (uploadBudget <= 0)");
    }

    [Fact]
    public void Default_publication_commits_on_submit_and_rolls_back_on_abort()
    {
        var cache = CacheSource();
        var recorder = RecorderSource();

        Assert.Contains("IGpuCommandSubmissionParticipant12", cache, StringComparison.Ordinal);
        Assert.Contains("_recorder.EnlistCurrentFrame(this);", cache, StringComparison.Ordinal);
        Assert.Contains("CommitPendingPublicationsNoThrow", cache, StringComparison.Ordinal);
        Assert.Contains("RollbackPendingPublicationsNoThrow(invalidateBatches: true", cache,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            cache,
            "if (node.Mesh is not null)",
            "if (node.PendingPublication is { } pending)",
            "return pending.Mesh;");

        Assert.Contains("NotifyCurrentFrameParticipants(submitted: false);", recorder,
            StringComparison.Ordinal);
        Assert.Contains("NotifyCurrentFrameParticipants(submitted: true);", recorder,
            StringComparison.Ordinal);
        Assert.Contains("participant.OnCommandListSubmitted();", recorder,
            StringComparison.Ordinal);
        Assert.Contains("participant.OnCommandListAborted();", recorder,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Arena_sweep_tracks_actual_reclamation_not_eviction_request_time()
    {
        var source = CacheSource();

        Assert.Contains("var reclamationGeneration = _geometryArena.ReclamationGeneration;", source,
            StringComparison.Ordinal);
        Assert.Contains("reclamationGeneration == _lastArenaSweepReclamationGeneration", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_lastArenaSweepEvictionGeneration", source,
            StringComparison.Ordinal);
    }
}
