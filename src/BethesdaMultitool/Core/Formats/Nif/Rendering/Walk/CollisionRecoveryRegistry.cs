namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     Lightweight state retained while a reference-mesh node is resident. Collision triangle soup
///     remains exclusively owned by the byte-bounded collision LRU; this state only says how a miss
///     may be recovered and which exact decoded variant last published the plain-path entry.
/// </summary>
internal enum CollisionNodeState : byte
{
    Unknown = 0,
    Republishable = 1,
    AuthoritativeNone = 2,
    TerminalUnavailable = 3,
}

/// <summary>
///     Bounded plain-path to resident cache-key ownership for collision recovery. The owner is removed
///     with its mesh-LRU node, so this dictionary cannot outlive or outgrow the resident node set.
/// </summary>
internal sealed class CollisionRecoveryRegistry
{
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _owners.Count;

    /// <summary>Records a resident node before its first collision result is known.</summary>
    public void TrackUnknownOwner(string normalizedPath, string cacheKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedPath);
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);
        _owners.TryAdd(normalizedPath, new Owner(cacheKey, CollisionNodeState.Unknown));
    }

    /// <summary>Records the exact decoded variant that most recently published a collision entry.</summary>
    public void Publish(string normalizedPath, string cacheKey, CollisionCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedPath);
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);
        _owners[normalizedPath] = new Owner(
            cacheKey,
            entry.HasRetainedGeometry
                ? CollisionNodeState.Republishable
                : CollisionNodeState.AuthoritativeNone);
    }

    /// <summary>
    ///     Marks a null/faulted decode without converting it into authoritative no-collision. A live
    ///     collision entry from another variant remains authoritative and keeps its existing owner.
    /// </summary>
    public void MarkTerminalUnavailable(
        string normalizedPath,
        string cacheKey,
        bool hasPublishedCollisionEntry)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedPath);
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);
        if (hasPublishedCollisionEntry) return;

        // A failed alternate-material variant must not poison a different variant that previously
        // published and remains resident. This matters even after the collision-LRU entry itself has
        // gone: the current owner still names the exact decoded/disk/source payload that can restore it.
        if (_owners.TryGetValue(normalizedPath, out var owner) &&
            !string.Equals(owner.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _owners[normalizedPath] = new Owner(cacheKey, CollisionNodeState.TerminalUnavailable);
    }

    public bool TryGetOwner(string normalizedPath, out string cacheKey)
    {
        if (_owners.TryGetValue(normalizedPath, out var owner))
        {
            cacheKey = owner.CacheKey;
            return true;
        }

        cacheKey = string.Empty;
        return false;
    }

    /// <summary>Removes ownership only when the evicted node is still the current publisher.</summary>
    public void RemoveOwner(string normalizedPath, string cacheKey)
    {
        if (_owners.TryGetValue(normalizedPath, out var owner) &&
            string.Equals(owner.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            _owners.Remove(normalizedPath);
        }
    }

    /// <summary>
    ///     Resolves a collision-LRU miss from lightweight state. Terminal failure stays unresolved so
    ///     OBND remains available, but it is explicitly ineligible for another warmup offer.
    /// </summary>
    public CollisionMeshResolution ResolveCacheMiss(string normalizedPath)
    {
        if (!_owners.TryGetValue(normalizedPath, out var owner))
        {
            return CollisionMeshResolution.Unresolved;
        }

        return owner.State switch
        {
            CollisionNodeState.AuthoritativeNone =>
                CollisionMeshResolution.From(CollisionBuildResult.None),
            CollisionNodeState.TerminalUnavailable =>
                CollisionMeshResolution.TerminalUnavailable,
            _ => CollisionMeshResolution.Unresolved,
        };
    }

    private readonly record struct Owner(string CacheKey, CollisionNodeState State);
}
