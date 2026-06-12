namespace FalloutXbox360Utils.Core.Diagnostics;

/// <summary>
///     Lifetime handle for one resource registered with a <see cref="ResourceRegistry" />. Dispose
///     when the resource is disposed (idempotent). Also relays pending trim requests to
///     <see cref="TrimAffinity.OwnerThread" /> participants: the
///     <see cref="MemoryBudgetCoordinator" /> posts via <see cref="PostTrim" />; the owner thread
///     consumes via <see cref="TryConsumePendingTrim" /> in its per-frame tick, so no lock is ever
///     added to the render path.
/// </summary>
internal sealed class ResourceRegistration : IDisposable
{
    private readonly ResourceRegistry _registry;
    private int _pendingTrim; // 0 = none, otherwise (int)TrimLevel + 1
    private int _disposed;

    internal ResourceRegistration(ResourceRegistry registry, ITrackableResource resource, string displayName)
    {
        _registry = registry;
        Resource = resource;
        DisplayName = displayName;
    }

    public ITrackableResource Resource { get; }

    public string DisplayName { get; }

    /// <summary>Posts a trim request for the owner thread. A higher pending level is never downgraded.</summary>
    public void PostTrim(TrimLevel level)
    {
        var encoded = (int)level + 1;
        int current;
        do
        {
            current = Volatile.Read(ref _pendingTrim);
            if (current >= encoded)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _pendingTrim, encoded, current) != current);
    }

    /// <summary>Consumes a posted trim request, if any. Called by the owner thread's per-frame tick.</summary>
    public bool TryConsumePendingTrim(out TrimLevel level)
    {
        var encoded = Interlocked.Exchange(ref _pendingTrim, 0);
        if (encoded == 0)
        {
            level = default;
            return false;
        }

        level = (TrimLevel)(encoded - 1);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _registry.Unregister(this);
        }
    }
}
