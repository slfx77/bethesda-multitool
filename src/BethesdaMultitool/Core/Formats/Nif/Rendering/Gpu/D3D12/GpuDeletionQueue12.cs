using Slfx77.Multitool.Core.Lifetime;
using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Adapts Bethesda's fence-synchronized frame delay to Shared retired-resource cleanup.
/// </summary>
/// <remarks>Tick only after the recorder completes the reused frame slot's fence wait. The
/// counter itself proves no GPU completion. The creating thread owns mutation and release;
/// diagnostics may read approximate counts. Resources remain owned until cleanup succeeds.</remarks>
internal sealed class GpuDeletionQueue12 : ITrackableResource, IDisposable
{
    private readonly int _framesToHold;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Queue<PendingDeletion> _pending = new();
    private ulong _frameOrdinal;
    private int _readyResourceCount;
    private bool _disposed;
    private bool _releasing;
    private readonly RetiredResourceDisposal _retiredResources = new();
    private ResourceRegistration? _registration;

    /// <summary>Creates an owner on the current thread with an application-proven frame delay.</summary>
    /// <param name="framesToHold">Positive count of fence-synchronized ticks before release.</param>
    /// <exception cref="ArgumentOutOfRangeException">The delay is not positive.</exception>
    public GpuDeletionQueue12(int framesToHold)
    {
        if (framesToHold <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesToHold), "Must be > 0.");
        _framesToHold = framesToHold;
    }

    /// <summary>
    ///     The tick counter, exposed so geometry diagnostics can correlate free timing with
    ///     the fence-synced frame clock.
    /// </summary>
    public uint CurrentFrame => unchecked((uint)_frameOrdinal);

    /// <summary>
    ///     Drains all remaining pending resources synchronously. Caller must ensure
    ///     the GPU is idle (e.g. via <c>WaitForGpuIdle</c>) before calling.
    /// </summary>
    /// <exception cref="AggregateException">Failed releases remain owned for a later disposal attempt.</exception>
    /// <exception cref="InvalidOperationException">Access is on another thread or a release callback reenters shutdown.</exception>
    public void Dispose()
    {
        VerifyMutation();
        if (!_disposed)
        {
            _disposed = true;
        }
        while (_pending.TryPeek(out var pending))
        {
            TransferToRetired(pending);
        }
        ReleaseReadyResources();
        // Retain the live diagnostic registration until every owned release succeeds. Its final
        // snapshot must describe an empty queue, not resources still waiting for a disposal retry.
        _registration?.Dispose();
        _registration = null;
    }

    public string ResourceName => nameof(GpuDeletionQueue12);

    public ResourceCategory Category => ResourceCategory.GpuMeta;

    /// <summary>Reports both frame-delayed resources and failed releases retained by Shared.</summary>
    public ResourceStats GetStats()
    {
        return new ResourceStats { QueueDepth = checked(_pending.Count + _readyResourceCount) };
    }

    /// <summary>
    ///     Registers the queue with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the queue for fluent construction.
    /// </summary>
    public GpuDeletionQueue12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        VerifyMutation();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    /// <summary>
    ///     Retains a resource until the configured count of fence-synchronized ticks has elapsed.
    /// </summary>
    /// <param name="resource">Ownership transfers on successful enqueue; rejection leaves the caller responsible.</param>
    /// <remarks>After shutdown, preserves the existing synchronous release behavior. The caller must
    /// have proved complete GPU retirement; a failed synchronous release remains caller-owned.</remarks>
    /// <exception cref="ArgumentNullException">The resource is absent.</exception>
    /// <exception cref="InvalidOperationException">Access is outside the creating thread.</exception>
    /// <exception cref="OverflowException">The monotonic clock cannot represent the retirement delay.</exception>
    public void EnqueueDispose(IDisposable resource)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(resource);
        if (_disposed)
        {
            resource.Dispose();
            return;
        }

        _pending.Enqueue(new PendingDeletion(resource, checked(_frameOrdinal + (uint)_framesToHold)));
    }

    /// <summary>
    ///     Advances the frame counter and releases any pending resources whose hold has
    ///     elapsed. Call once at <see cref="GpuCommandRecorder12.BeginFrame" /> after the
    ///     fence wait (so the prior submission is GPU-complete).
    /// </summary>
    /// <exception cref="AggregateException">Failed releases remain owned and independent siblings are still attempted.</exception>
    /// <exception cref="InvalidOperationException">Access is on another thread or a release callback tries to advance the clock.</exception>
    /// <exception cref="ObjectDisposedException">Shutdown has begun.</exception>
    /// <exception cref="OverflowException">The monotonic clock is exhausted; no new frame becomes eligible.</exception>
    public void Tick()
    {
        VerifyMutation();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frameOrdinal = checked(_frameOrdinal + 1);
        while (_pending.TryPeek(out var head) && head.SafeFrame <= _frameOrdinal)
        {
            TransferToRetired(head);
        }
        ReleaseReadyResources();
    }

    /// <summary>Transfers the head only after Shared accepts ownership; failed admission leaves it queued.</summary>
    /// <param name="pending">The current head, already proven ready or covered by shutdown retirement.</param>
    private void TransferToRetired(PendingDeletion pending)
    {
        var nextReadyCount = checked(_readyResourceCount + 1);
        _retiredResources.Add(() =>
        {
            pending.Resource.Dispose();
            _readyResourceCount--;
        }, "retired GPU resource");
        _readyResourceCount = nextReadyCount;
        _pending.Dequeue();
    }

    /// <summary>Lets Shared retry independent releases while preventing nested clock advancement or shutdown.</summary>
    private void ReleaseReadyResources()
    {
        _releasing = true;
        try { _retiredResources.Dispose(); }
        finally { _releasing = false; }
    }

    /// <summary>Rejects mutation that could retire another frame while a release callback is still executing.</summary>
    private void VerifyMutation()
    {
        VerifyAccess();
        if (_releasing) throw new InvalidOperationException("Deferred resource cleanup cannot reenter frame advancement or shutdown.");
    }

    /// <summary>Rejects native-resource ownership changes outside the creating thread.</summary>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Deferred GPU resources belong to their creating thread.");
    }

    /// <summary>Retains one resource and its monotonic eligible frame without interpreting diagnostic counter wrap.</summary>
    private readonly record struct PendingDeletion(IDisposable Resource, ulong SafeFrame);
}
