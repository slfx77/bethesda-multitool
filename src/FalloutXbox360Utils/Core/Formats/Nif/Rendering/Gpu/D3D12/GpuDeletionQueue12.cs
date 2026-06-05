namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     v3 Pass 4 Step 2c — deferred-release queue for D3D12 resources. D3D11 auto-tracks
///     resource lifetime against the GPU, so disposing a vertex buffer while the GPU is
///     still reading from it is safe (the runtime delays the actual free). D3D12 does NOT —
///     <c>ID3D12Resource.Release</c> is immediate; reads from a freed GPU virtual address
///     access-violate. Any per-frame eviction path (mesh LRU, opacity LRU, texture cache)
///     must route disposals through this queue rather than calling <c>Dispose</c> directly.
///     <para>
///         Pattern: enqueue a resource with the current frame ordinal. After
///         <c>framesToHold</c> frames pass (= <see cref="GpuCommandRecorder12.FramesInFlight" />),
///         the GPU has finished consuming the prior submission and the resource is safe to
///         release. Frame-counter based — simpler than tracking fence values and equally
///         correct since the recorder already enforces N-frames-in-flight via its fence.
///     </para>
/// </summary>
internal sealed class GpuDeletionQueue12 : IDisposable
{
    private readonly int _framesToHold;
    private readonly Queue<PendingDeletion> _pending = new();
    private uint _currentFrame;
    private bool _disposed;

    public GpuDeletionQueue12(int framesToHold)
    {
        if (framesToHold <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesToHold), "Must be > 0.");
        _framesToHold = framesToHold;
    }

    /// <summary>
    ///     Defers <paramref name="resource" />'s disposal until at least
    ///     <see cref="GpuCommandRecorder12.FramesInFlight" /> frames have passed —
    ///     enough for the GPU to drain the previous submission referencing it.
    /// </summary>
    public void EnqueueDispose(IDisposable resource)
    {
        if (_disposed)
        {
            resource.Dispose();
            return;
        }
        _pending.Enqueue(new PendingDeletion(resource, _currentFrame + (uint)_framesToHold));
    }

    /// <summary>
    ///     Advances the frame counter and releases any pending resources whose hold has
    ///     elapsed. Call once at <see cref="GpuCommandRecorder12.BeginFrame" /> after the
    ///     fence wait (so the prior submission is GPU-complete).
    /// </summary>
    public void Tick()
    {
        _currentFrame++;
        while (_pending.TryPeek(out var head) && head.SafeFrame <= _currentFrame)
        {
            _pending.Dequeue();
            head.Resource.Dispose();
        }
    }

    /// <summary>Drains all remaining pending resources synchronously. Caller must ensure
    /// the GPU is idle (e.g. via <c>WaitForGpuIdle</c>) before calling.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        while (_pending.TryDequeue(out var p))
        {
            p.Resource.Dispose();
        }
    }

    private readonly record struct PendingDeletion(IDisposable Resource, uint SafeFrame);
}
