#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     CPU-side registry of instanced opaque draw batches for <see cref="ReferenceRenderer12" />.
///     Batches are keyed by cached submesh (the batch key); each frame the renderer calls
///     <see cref="Begin" /> to reset per-frame instance lists (and periodically prune cold
///     batches), then <see cref="GetOrCreate" /> per visible submesh to append a world matrix.
///     Pure bookkeeping — it records no command-list work, so it has no effect on draw ordering.
/// </summary>
internal sealed class OpaqueBatchRegistry12
{
    private const int StaleFrameWindow = 120;
    private const int PruneIntervalFrames = 30;

    private readonly Dictionary<CachedSubmesh12, OpaqueBatchState> _batches = new();
    private readonly List<OpaqueBatchState> _activeBatches = new(256);
    private readonly List<CachedSubmesh12> _staleKeys = new(64);
    private int _frameId;

    /// <summary>The batches touched in the current frame, in first-touch order — what the
    /// renderer iterates to record the instanced opaque draws.</summary>
    public IReadOnlyList<OpaqueBatchState> ActiveBatches => _activeBatches;

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
            batch.ShadowOnlyInstances.Clear();
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
    public OpaqueBatchState GetOrCreate(CachedSubmesh12 submesh, ID3D12PipelineState pso)
    {
        if (!_batches.TryGetValue(submesh, out var batch))
        {
            batch = new OpaqueBatchState(submesh, pso);
            _batches.Add(submesh, batch);
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
}

internal sealed class OpaqueBatchState(CachedSubmesh12 submesh, ID3D12PipelineState pso)
{
    public CachedSubmesh12 Submesh { get; } = submesh;
    public ID3D12PipelineState Pso { get; } = pso;

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

    /// <summary>Shadow-only instances uploaded this frame (0 when the shadow capture is unarmed).
    /// Set by DrawOpaqueBatches alongside <see cref="FrameDrawCount" />.</summary>
    public int FrameShadowOnlyCount { get; set; }

    public int LastTouchedFrame { get; set; }
}
#endif
