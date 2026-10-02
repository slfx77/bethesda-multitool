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
    private static string CacheSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshCache12.cs");
    }

    private static string RendererSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
    }

    private static string RecorderSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuCommandRecorder12.cs");
    }

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

        // GetOrUpload sits in the false arm of a ternary now (bendable splines take the
        // generated-mesh route), so it is one indent deeper — the open command list is still the
        // first argument, which is what carries it into the arena copy.
        Assert.Contains("_meshCache.GetOrUpload(\n                commandList,", renderer,
            StringComparison.Ordinal);
        Assert.Contains("public CachedNifMesh12? GetOrUpload(\n        ID3D12GraphicsCommandList commandList,",
            cache, StringComparison.Ordinal);
        Assert.Matches(
            @"geometryArena\.Upload\(\s+commandList \?\? throw new InvalidOperationException\(",
            cache);
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
            "if (materialization.Status != MeshMaterializationStatus.Success)",
            "RetireNode(node, queueRetirement: false);",
            "if (materialization.Status == MeshMaterializationStatus.RenderEmpty)",
            "node.ResolvedNull = true;",
            "else MarkMaterializationRetry(node);");
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
        Assert.Equal(1, renderer.Split(beginGeneration).Length - 1);
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
    public void Default_publication_uses_exact_shared_outcomes_and_retained_initialization()
    {
        var cache = CacheSource();
        var recorder = RecorderSource();

        Assert.Contains("IDisposable, ISubmissionParticipant", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("IGpuCommandSubmissionParticipant12", cache, StringComparison.Ordinal);
        Assert.Contains("_recorder.EnlistCurrentFrame(this);", cache, StringComparison.Ordinal);
        Assert.Contains("entry.OnSubmissionOutcome(outcome);", cache, StringComparison.Ordinal);
        Assert.Contains("entry.PublishPrepared();", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingMeshPublication", cache, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            cache,
            "_recorder.EnqueueDisposeAfterCurrentFrame(new GpuMeshCandidateRetirement12(entry, _retiredMeshResources));",
            "entry.Attach(resources);",
            "var materialization = UploadDecodedMesh(",
            "entry.MarkPrepared();",
            "var pin = entry.AcquirePreparedPin();",
            "mesh.AdoptResidencyPin(pin);");

        Assert.Contains("_native.EnlistCurrentFrame(participant);", recorder, StringComparison.Ordinal);
        Assert.Contains("_native.AbortFrame();", recorder, StringComparison.Ordinal);
        Assert.Contains("_native.EndFrame(retainIfUnfenced);", recorder, StringComparison.Ordinal);
        var nativeRecorder = SourceContract.ReadSource(
            "shared", "Multitool.Shared", "src", "Slfx77.Multitool.WinUI.Direct3D12.Shaders",
            "NativeFrameRecorder.cs");
        SourceContract.AssertOrder(
            nativeRecorder,
            "_queue.Signal(_fence, signal).CheckError();",
            "LastSubmittedFenceValue = signal;",
            "NotifyOutcome(SubmissionOutcome.Submitted);",
            "_submissions.AssociateUnfenced(signal);");
        var participant = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "IGpuCommandSubmissionParticipant12.cs");
        Assert.Contains("IGpuCommandSubmissionParticipant12 : ISubmissionParticipant", participant,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            participant,
            "outcome == SubmissionOutcome.DefinitelyAbandoned",
            "OnCommandListAborted();",
            "else",
            "OnCommandListSubmitted();");
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
