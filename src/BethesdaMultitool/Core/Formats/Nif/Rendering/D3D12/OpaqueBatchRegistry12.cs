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
///     Pure bookkeeping — it records no command-list work, so it has no effect on draw ordering.
/// </summary>
internal sealed class OpaqueBatchRegistry12
{
    private const int StaleFrameWindow = 120;
    private const int PruneIntervalFrames = 30;

    private readonly Dictionary<OpaqueBatchKey, OpaqueBatchState> _batches = new();
    private readonly List<OpaqueBatchState> _activeBatches = new(256);
    private readonly List<OpaqueBatchKey> _staleKeys = new(64);
    private readonly List<OpaqueBatchState> _partitionScratch = new(32);
    private int _frameId;

    /// <summary>The batches touched in the current frame, in first-touch order — what the
    /// renderer iterates to record the instanced opaque draws.</summary>
    public IReadOnlyList<OpaqueBatchState> ActiveBatches => _activeBatches;

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
    ///     Starts a new frame: advances the frame id, clears each active batch's per-frame
    ///     instance list, empties the active set, and prunes batches that have gone cold on a
    ///     fixed interval.
    /// </summary>
    public void Begin()
    {
        unchecked
        {
            _frameId++;
        }

        foreach (var batch in _activeBatches)
        {
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
        _activeBatches.Clear();

        if (_frameId % PruneIntervalFrames == 0)
        {
            PruneStaleBatches();
        }
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
        float grassWaveMultiplier)
    {
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
            submesh, usesGrassDistanceEnvelope, effectiveTallGrassWind, effectiveWaveMultiplier, pso);
        if (!_batches.TryGetValue(key, out var batch))
        {
            batch = new OpaqueBatchState(
                submesh, pso, usesGrassDistanceEnvelope, effectiveTallGrassWind, effectiveWaveMultiplier);
            _batches.Add(key, batch);
        }

        if (batch.LastTouchedFrame != _frameId)
        {
            batch.LastTouchedFrame = _frameId;
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
        ID3D12PipelineState Pso);
}

internal sealed class OpaqueBatchState(
    CachedSubmesh12 submesh,
    ID3D12PipelineState pso,
    bool usesGrassDistanceEnvelope,
    bool usesTallGrassWind,
    float grassWaveMultiplier)
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

    public int LastTouchedFrame { get; set; }
}
#endif
