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

    /// <summary>Per-instance world matrices for this batch (the only per-instance data;
    /// material/texture state is per-batch and lives in the InstanceDraw CBV at draw time).</summary>
    public List<Matrix4x4> Instances { get; } = new(16);
    public int LastTouchedFrame { get; set; }
}
#endif
