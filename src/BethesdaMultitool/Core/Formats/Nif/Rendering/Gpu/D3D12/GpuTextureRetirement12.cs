using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Transfers an evicted texture and its descriptor as one retryable deferred-release bundle.</summary>
/// <remarks>The cache retains this token until transfer succeeds. Shared owns child release progress,
/// including synchronous partial failure after the deletion queue has shut down. The texture must
/// release before its descriptor can be reused; this type establishes no GPU completion proof.</remarks>
internal sealed class GpuTextureRetirement12 : IDisposable
{
    private readonly RetiredResourceDisposal _resources = new();
    private readonly GpuDeletionQueue12? _queue;
    private bool _transferring;

    /// <summary>Prepares all release actions before the caller detaches any cache ownership.</summary>
    /// <param name="texture">Owned resident texture, or null for a placeholder borrowing a pinned fallback.</param>
    /// <param name="releaseSlot">Returns the exact evicted entry's descriptor after its texture releases.</param>
    /// <param name="queue">Fence-synchronized deferred owner, or null when the caller proves immediate release is safe.</param>
    /// <param name="residentBytes">Cache-resident allocation bytes to remove only after transfer succeeds.</param>
    internal GpuTextureRetirement12(IDisposable? texture, Action releaseSlot, GpuDeletionQueue12? queue, long residentBytes)
    {
        ArgumentNullException.ThrowIfNull(releaseSlot);
        ArgumentOutOfRangeException.ThrowIfNegative(residentBytes);
        _resources.Add(texture, "evicted texture", 0);
        _resources.Add(releaseSlot, "evicted texture descriptor", 1);
        _queue = queue;
        ResidentBytes = residentBytes;
    }

    /// <summary>Gets the original cache charge, retained while queue admission or synchronous cleanup fails.</summary>
    internal long ResidentBytes { get; }

    /// <summary>Gets whether the queue accepted the bundle or immediate cleanup completed.</summary>
    internal bool IsTransferred { get; private set; }

    /// <summary>Disposes this ownership token by transferring its retained children to the established retirement queue.</summary>
    /// <remarks>Failed transfer remains caller-owned and retryable, as with <see cref="Transfer"/>.</remarks>
    public void Dispose() => Transfer();

    /// <summary>Transfers once; a rejected transfer or failed synchronous release remains safe to retry.</summary>
    /// <exception cref="InvalidOperationException">A release callback recursively tries to transfer this bundle.</exception>
    internal void Transfer()
    {
        if (IsTransferred) { return; }
        if (_transferring) { throw new InvalidOperationException("Texture retirement cannot reenter its ownership transfer."); }
        _transferring = true;
        try
        {
            if (_queue is null) { _resources.Dispose(); }
            else { _queue.EnqueueDispose(_resources); }
            IsTransferred = true;
        }
        finally { _transferring = false; }
    }
}
