#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     CPU-side registry of instanced opaque draw batches for <see cref="ReferenceRenderer12" />.
///     Batches are keyed by cached submesh plus independent grass-distance and FNV TallGrass-wind
///     identities; each frame the renderer calls
///     <see cref="Begin" /> to reset per-frame instance lists (and periodically prune cold
///     batches), then <see cref="GetOrCreate" /> per visible submesh to append a world matrix.
///     Pure bookkeeping — it records no command-list work. At publication it establishes the safe
///     PSO/lane submission order consumed by later reuse frames.
/// </summary>
internal sealed class OpaqueBatchRegistry12
{
    private const int StaleFrameWindow = 120;
    private const int PruneIntervalFrames = 30;

    private readonly Dictionary<OpaqueBatchKey, OpaqueBatchState> _batches = new();
    private readonly List<OpaqueBatchState> _activeBatches = new(256);
    private readonly List<OpaqueBatchKey> _staleKeys = new(64);
    private readonly List<OpaqueBatchState> _partitionScratch = new(32);
    private readonly List<OpaqueBatchState> _submissionOrderScratch = new(256);
    private readonly List<ID3D12PipelineState> _submissionPsoScratch = new(8);
    private static readonly IComparer<OpaqueBatchState> FrontToBackComparer =
        new OpaqueBatchFrontToBackComparer();
    private int _frameId;
    private int _beginCursor;
    private bool _beginInProgress;

    /// <summary>The batches touched in the current build. Initially first-touch order, then frozen
    /// into publication order before the renderer records instanced opaque draws.</summary>
    public IReadOnlyList<OpaqueBatchState> ActiveBatches => _activeBatches;

    /// <summary>Frozen publication census for the optional specialized shader. Calculated once in
    /// <see cref="OrderForSubmission" />, never by the per-frame draw loop.</summary>
    public int ModernStandardBatchCount { get; private set; }
    public int ModernStandardInstanceCount { get; private set; }

    /// <summary>Frozen census/verdict for the optional within-PSO depth order.</summary>
    public bool FrontToBackActive { get; private set; }
    public int FrontToBackBatchCount { get; private set; }
    public int FrontToBackInstanceCount { get; private set; }
    public OpaqueFrontToBackFallbackReason FrontToBackFallbackReason { get; private set; }

    /// <summary>
    ///     Stable-partitions the active set so grass batches are issued LAST within the instanced
    ///     pass, for two independent reasons.
    ///     <para>
    ///         CORRECTNESS: TES4 grass batches carry an alpha-BLENDED, depth-WRITING pipeline. A blade
    ///         drawn before an opaque reference standing behind it would blend its soft edge texels
    ///         against the terrain, write depth, and then depth-reject that reference — leaving a
    ///         one-texel fringe of terrain colour where the object should show through. Draining grass
    ///         after every opaque batch removes the whole class, and restores the relative order grass
    ///         had when it was a per-draw depth-writing blend.
    ///     </para>
    ///     <para>
    ///         PERFORMANCE: it also lets every grass pixel be early-Z rejected against the full opaque
    ///         depth buffer, which is the cheap half of moving grass onto this path at all.
    ///     </para>
    ///     Grass identity is <see cref="OpaqueBatchState.UsesGrassDistanceEnvelope" /> — already part
    ///     of the batch key, and set only by the per-game grass profiles. FO3/FNV cutout grass is
    ///     swept along, which is harmless (an opaque cutout is order-independent) and gains the same
    ///     early-Z benefit. Called once per BUILD, not per frame; reuse frames inherit the order.
    /// </summary>
    public void OrderGrassBatchesLast()
    {
        var count = _activeBatches.Count;
        if (count < 2) return;

        var firstGrass = -1;
        for (var i = 0; i < count; i++)
        {
            if (_activeBatches[i].UsesGrassDistanceEnvelope)
            {
                firstGrass = i;
                break;
            }
        }

        // Already partitioned (the common case, including "no grass at all") — touch nothing.
        if (firstGrass < 0) return;

        var anyOpaqueAfter = false;
        for (var i = firstGrass + 1; i < count; i++)
        {
            if (!_activeBatches[i].UsesGrassDistanceEnvelope)
            {
                anyOpaqueAfter = true;
                break;
            }
        }

        if (!anyOpaqueAfter) return;

        // Stable two-pass compaction: opaque batches keep their first-touch order, then grass does.
        _partitionScratch.Clear();
        for (var i = firstGrass; i < count; i++)
        {
            if (_activeBatches[i].UsesGrassDistanceEnvelope) _partitionScratch.Add(_activeBatches[i]);
        }

        var write = firstGrass;
        for (var i = firstGrass; i < count; i++)
        {
            if (!_activeBatches[i].UsesGrassDistanceEnvelope) _activeBatches[write++] = _activeBatches[i];
        }

        foreach (var grass in _partitionScratch) _activeBatches[write++] = grass;
        _partitionScratch.Clear();
    }

    /// <summary>
    ///     Establishes the immutable publication order used by every reuse frame. Ordinary
    ///     opaque/cutout batches are stable-grouped by PSO; decals retain authored overlay order;
    ///     grass and depth-writing grass retain the existing grass-last correctness boundary.
    /// </summary>
    public void OrderForSubmission(in OpaqueFrontToBackBuildView frontToBackView)
    {
        FrontToBackActive = false;
        FrontToBackBatchCount = 0;
        FrontToBackInstanceCount = 0;
        FrontToBackFallbackReason = frontToBackView.Requested
            ? frontToBackView.FallbackReason
            : OpaqueFrontToBackFallbackReason.None;

        if (frontToBackView.Requested && frontToBackView.Valid)
        {
            foreach (var batch in _activeBatches)
            {
                if (SubmissionLane(batch) != OpaqueSubmissionLane.Ordinary ||
                    batch.Instances.Count == 0)
                {
                    continue;
                }

                FrontToBackBatchCount++;
                FrontToBackInstanceCount += batch.Instances.Count;
            }

            if (FrontToBackBatchCount > 0)
            {
                FrontToBackActive = true;
                FrontToBackFallbackReason = OpaqueFrontToBackFallbackReason.None;
            }
            else
            {
                FrontToBackFallbackReason = OpaqueFrontToBackFallbackReason.NoEligibleBatches;
            }
        }

        OpaqueSubmissionOrderPolicy.Order(
            _activeBatches,
            _submissionOrderScratch,
            _submissionPsoScratch,
            static batch => SubmissionLane(batch),
            static batch => batch.Pso,
            ReferenceEqualityComparer.Instance,
            FrontToBackActive ? FrontToBackComparer : null);

        ModernStandardBatchCount = 0;
        ModernStandardInstanceCount = 0;
        foreach (var batch in _activeBatches)
        {
            if (!batch.UsesModernStandardShader || batch.Instances.Count == 0)
            {
                continue;
            }

            ModernStandardBatchCount++;
            ModernStandardInstanceCount += batch.Instances.Count;
        }
    }

    private static OpaqueSubmissionLane SubmissionLane(OpaqueBatchState batch)
    {
        // Late-lane precedence is load-bearing: should malformed/novel content ever mark a grass
        // shape as a decal too, it must still honor the depth-writing grass-last boundary.
        if (batch.Submesh.DepthWritingBlend || batch.UsesGrassDistanceEnvelope)
        {
            return OpaqueSubmissionLane.Grass;
        }

        return batch.Submesh.IsDecal
            ? OpaqueSubmissionLane.Decal
            : OpaqueSubmissionLane.Ordinary;
    }

    /// <summary>
    ///     Starts a new frame: advances the frame id, clears each active batch's per-frame
    ///     instance list, empties the active set, and prunes batches that have gone cold on a
    ///     fixed interval.
    /// </summary>
    public void Begin()
    {
        StartBegin();
        if (!ContinueBegin(int.MaxValue))
        {
            throw new InvalidOperationException("An unbounded opaque-batch reset did not complete.");
        }
    }

    /// <summary>
    ///     Starts the destructive half of <see cref="Begin" /> without walking the old active set.
    ///     The renderer uses this for the INACTIVE registry so clearing a large previous snapshot is
    ///     charged to the same incremental budget as rebuilding it. The registry must not be queried
    ///     with <see cref="GetOrCreate" /> until <see cref="ContinueBegin" /> returns true.
    /// </summary>
    public void StartBegin()
    {
        unchecked
        {
            _frameId++;
        }

        _beginCursor = 0;
        _beginInProgress = true;
        ModernStandardBatchCount = 0;
        ModernStandardInstanceCount = 0;
        FrontToBackActive = false;
        FrontToBackBatchCount = 0;
        FrontToBackInstanceCount = 0;
        FrontToBackFallbackReason = OpaqueFrontToBackFallbackReason.None;
    }

    /// <summary>
    ///     Clears at most <paramref name="batchBudget" /> old active batches. Returns true once the
    ///     active set has been reset and the registry is ready to accept the next snapshot.
    /// </summary>
    public bool ContinueBegin(int batchBudget)
    {
        if (!_beginInProgress)
        {
            return true;
        }

        var remaining = _activeBatches.Count - _beginCursor;
        var stop = _beginCursor + Math.Min(remaining, Math.Max(batchBudget, 1));
        while (_beginCursor < stop)
        {
            var batch = _activeBatches[_beginCursor++];
            batch.Instances.Clear();
            batch.InstanceBounds.Clear();
            batch.PhysicsLiteSeeds.Clear();
            batch.HeatmapFormIds.Clear();
            batch.ShadowOnlyInstances.Clear();
            batch.ShadowOnlyPhysicsLiteSeeds.Clear();
            Array.Clear(batch.CascadePrefix);
            Array.Clear(batch.ShadowOnlyCascadePrefix);
            Array.Clear(batch.FrameCascadeCount);
            Array.Clear(batch.FrameShadowOnlyCascadeCount);
        }

        if (_beginCursor < _activeBatches.Count)
        {
            return false;
        }

        _activeBatches.Clear();
        _beginCursor = 0;
        _beginInProgress = false;

        if (_frameId % PruneIntervalFrames == 0)
        {
            PruneStaleBatches();
        }

        return true;
    }

    /// <summary>
    ///     Returns the batch for <paramref name="submesh" /> (creating it on first sight), adding
    ///     it to the active set the first time it is touched this frame so it participates in the
    ///     instanced draw pass.
    /// </summary>
    public OpaqueBatchState GetOrCreate(
        CachedSubmesh12 submesh,
        ID3D12PipelineState pso,
        bool usesGrassDistanceEnvelope,
        bool usesTallGrassWind,
        float grassWaveMultiplier,
        bool usesModernStandardShader)
    {
        if (_beginInProgress)
        {
            throw new InvalidOperationException("Opaque batch registry reset is incomplete.");
        }

        // A shared grass NIF may be referenced by multiple GRAS records with different authored
        // phase multipliers. Keep those instances in distinct material batches so the 64-byte
        // matrix-only instance stream remains unchanged and the multiplier can stay per batch.
        // Wind eligibility is supplied explicitly by the renderer's FNV capability gate; a
        // distance envelope alone must never opt another game into GRASS2000 deformation.
        var effectiveTallGrassWind = usesTallGrassWind && submesh.IsTallGrass;
        var effectiveWaveMultiplier = effectiveTallGrassWind
            ? FnvTallGrassWind.SanitizeWaveMultiplier(grassWaveMultiplier)
            : 0f;
        var key = new OpaqueBatchKey(
            submesh, usesGrassDistanceEnvelope, effectiveTallGrassWind, effectiveWaveMultiplier, pso,
            usesModernStandardShader);
        if (!_batches.TryGetValue(key, out var batch))
        {
            batch = new OpaqueBatchState(
                submesh, pso, usesGrassDistanceEnvelope, effectiveTallGrassWind, effectiveWaveMultiplier,
                usesModernStandardShader);
            _batches.Add(key, batch);
        }

        if (batch.LastTouchedFrame != _frameId)
        {
            batch.LastTouchedFrame = _frameId;
            batch.FirstTouchOrdinal = _activeBatches.Count;
            batch.NearestViewDepth = double.PositiveInfinity;
            _activeBatches.Add(batch);
        }

        return batch;
    }

    private void PruneStaleBatches()
    {
        var staleBeforeFrame = _frameId - StaleFrameWindow;
        foreach (var (key, batch) in _batches)
        {
            if (batch.LastTouchedFrame < staleBeforeFrame)
            {
                _staleKeys.Add(key);
            }
        }

        foreach (var key in _staleKeys)
        {
            _batches.Remove(key);
        }
        _staleKeys.Clear();
    }

    /// <summary>
    ///     Observes one placement's already-computed near depth for an ordinary batch. Special
    ///     correctness lanes deliberately ignore malformed depth data because they are never sorted.
    ///     False means an ordinary batch lacked a valid key and the whole publication must preserve
    ///     the established stable order.
    /// </summary>
    public bool ObserveFrontToBackDepth(
        OpaqueBatchState batch,
        bool depthValid,
        double nearestViewDepth)
    {
        if (SubmissionLane(batch) != OpaqueSubmissionLane.Ordinary)
        {
            return true;
        }

        if (!depthValid || !double.IsFinite(nearestViewDepth))
        {
            return false;
        }

        batch.NearestViewDepth = Math.Min(batch.NearestViewDepth, nearestViewDepth);
        return true;
    }

    private sealed class OpaqueBatchFrontToBackComparer : IComparer<OpaqueBatchState>
    {
        public int Compare(OpaqueBatchState? x, OpaqueBatchState? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;

            var depth = x.NearestViewDepth.CompareTo(y.NearestViewDepth);
            return depth != 0 ? depth : x.FirstTouchOrdinal.CompareTo(y.FirstTouchOrdinal);
        }
    }

    /// <summary>
    ///     A mesh can be placed both as FNV grass covered by the recovered envelope and as an
    ///     ordinary reference. Keeping those placements separate preserves the hard end during
    ///     exact filtering of frozen batches. The wind flag is independently keyed so acquiring a
    ///     distance envelope cannot silently enable FNV's GRASS2000 constants in another game.
    ///     The pipeline state joins the key because it is no longer derivable from the submesh
    ///     alone — grass cutouts reroute to the alpha-to-coverage PSO variants, and a grass and a
    ///     non-grass placement of the same submesh must not alias one batch (reference identity
    ///     equality is correct: factory PSOs are process-lifetime singletons).
    /// </summary>
    private readonly record struct OpaqueBatchKey(
        CachedSubmesh12 Submesh,
        bool UsesGrassDistanceEnvelope,
        bool UsesTallGrassWind,
        float GrassWaveMultiplier,
        ID3D12PipelineState Pso,
        bool UsesModernStandardShader);
}

internal sealed class OpaqueBatchState(
    CachedSubmesh12 submesh,
    ID3D12PipelineState pso,
    bool usesGrassDistanceEnvelope,
    bool usesTallGrassWind,
    float grassWaveMultiplier,
    bool usesModernStandardShader)
{
    public CachedSubmesh12 Submesh { get; } = submesh;
    public ID3D12PipelineState Pso { get; } = pso;
    /// <summary>True only for instances covered by an enabled game-specific grass envelope.</summary>
    public bool UsesGrassDistanceEnvelope { get; } = usesGrassDistanceEnvelope;

    /// <summary>
    ///     Raw positive finite FNV GRAS wave multiplier for this TallGrass batch; zero for every
    ///     non-FNV/non-grass/non-TallGrass batch.
    /// </summary>
    public float GrassWaveMultiplier { get; } = grassWaveMultiplier;

    /// <summary>True only when the renderer's explicit FNV capability gate admitted this batch.</summary>
    public bool UsesTallGrassWind { get; } = usesTallGrassWind;

    /// <summary>True only when this batch's PSO was selected by the fail-closed modern-standard
    /// material classifier. Immutable with the PSO/batch key.</summary>
    public bool UsesModernStandardShader { get; } = usesModernStandardShader;

    /// <summary>Per-instance world matrices for this batch (the only per-instance GPU data;
    /// material/texture state is per-batch and lives in the InstanceDraw CBV at draw time).</summary>
    public List<Matrix4x4> Instances { get; } = new(16);

    /// <summary>
    ///     Per-instance ABSOLUTE cull sphere (xyz = world center, w = radius), parallel to
    ///     <see cref="Instances" />. The tolerant cull batches a WIDENED survivor superset; the draw
    ///     pass re-tests each instance against the current frame's exact bounds using these — which
    ///     is also what lets a frozen (reused) batch stay exact while the camera drifts.
    /// </summary>
    public List<Vector4> InstanceBounds { get; } = new(16);

    /// <summary>
    ///     Number of leading <see cref="Instances" /> that can cast into cascade i, after the build
    ///     sorts them by smallest containing cascade. Monotonically non-decreasing in i because the
    ///     cascade volumes are nested, which is what makes a per-cascade PREFIX well-defined.
    ///     <para>
    ///         The shadow replay can only truncate a draw's instance count from the tail — its start
    ///         offset lives in an immutable captured constant buffer — so prefix ordering is the only
    ///         shape that lets one captured draw serve every cascade at its own instance count.
    ///     </para>
    /// </summary>
    public int[] CascadePrefix { get; } = new int[ShadowMapRenderer12.CascadeCount];

    /// <summary>Same, for the shadow-only casters appended after the main range.</summary>
    public int[] ShadowOnlyCascadePrefix { get; } = new int[ShadowMapRenderer12.CascadeCount];

    /// <summary>Post-compaction mirror of <see cref="CascadePrefix" /> for the CURRENT frame: the copy
    /// pass drops instances (exact cull, grass distance, SpeedTree LOD), so the prefixes have to be
    /// re-derived against what was actually written.</summary>
    public int[] FrameCascadeCount { get; } = new int[ShadowMapRenderer12.CascadeCount];

    /// <summary>Post-compaction mirror of <see cref="ShadowOnlyCascadePrefix" />.</summary>
    public int[] FrameShadowOnlyCascadeCount { get; } = new int[ShadowMapRenderer12.CascadeCount];

    /// <summary>
    ///     Placed REFR FormIDs parallel to <see cref="Instances" /> only when this batch's submesh
    ///     carries FNV physics-lite data. Empty for the common static path, preserving its compact
    ///     matrix-only CPU storage and bulk-copy behavior.
    /// </summary>
    public List<uint> PhysicsLiteSeeds { get; } = new(8);

    /// <summary>
    ///     Placed REFR FormIDs parallel to <see cref="Instances" /> only while the renderer's
    ///     FormID-heatmap debug overlay is enabled (its enable setter forces a batch rebuild, so a
    ///     frozen batch can never be half-filled). Grass instances record 0 — their matrices'
    ///     w-lanes carry the FNV grass lighting payload and must never take the heatmap tint.
    ///     Empty in the common heatmap-off state, preserving the compact matrix-only storage.
    /// </summary>
    public List<uint> HeatmapFormIds { get; } = new(8);

    /// <summary>Instances that passed this frame's exact cull in the shared-block copy pass
    /// (== Instances.Count when the refilter is inactive). Set by DrawOpaqueBatches.</summary>
    public int FrameDrawCount { get; set; }

    /// <summary>
    ///     SHADOW-ONLY instances: placements inside the shadow caster ring that failed the CAMERA
    ///     frustum test — off-screen, but their shadows can land on-screen, so the sun-shadow
    ///     replay must still draw them (a caster leaving the frustum must not pop its shadow).
    ///     The copy pass appends these AFTER <see cref="Instances" /> in the batch's instance
    ///     block; the main draw's instance count excludes them, the shadow replay's includes them.
    /// </summary>
    public List<Matrix4x4> ShadowOnlyInstances { get; } = new(8);

    /// <summary>Physics-lite phase seeds parallel to <see cref="ShadowOnlyInstances" /> for sway batches.</summary>
    public List<uint> ShadowOnlyPhysicsLiteSeeds { get; } = new(4);

    /// <summary>Shadow-only instances uploaded this frame (0 when the shadow capture is unarmed).
    /// Set by DrawOpaqueBatches alongside <see cref="FrameDrawCount" />.</summary>
    public int FrameShadowOnlyCount { get; set; }

    /// <summary>Original active-list ordinal and publication-time nearest depth. These are rebuilt
    /// on first touch and are never consulted by the per-frame draw loop.</summary>
    public int FirstTouchOrdinal { get; set; }
    public double NearestViewDepth { get; set; } = double.PositiveInfinity;

    public int LastTouchedFrame { get; set; }
}
#endif
