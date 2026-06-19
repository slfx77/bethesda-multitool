using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core.Resources;

/// <summary>
///     Single-threaded LRU cache with optional entry-count and byte budgets — the shared core behind
///     the render-thread cell mesh caches and the byte-budgeted decoded-payload caches.
///     <para>
///         <b>Deliberately not thread-safe</b>: the render-thread caches must pay zero
///         synchronization on their hot path. Concurrent callers wrap calls in their own lock (the
///         decoded CPU mesh cache does exactly that today). <see cref="GetStats" /> may be called
///         from other threads and tolerates slightly stale plain-field reads (x64 aligned 64-bit
///         reads are atomic).
///     </para>
///     <para>
///         <see cref="TryGet" /> bumps a hit to most-recently-used; <see cref="ContainsKey" /> does
///         not touch recency. <see cref="Set" /> evicts the existing value for the key (via
///         <c>onEvicted</c>), then evicts least-recently-used entries until both budgets are
///         satisfied. <see cref="Remove" /> hands ownership back to the caller and does NOT invoke
///         <c>onEvicted</c>.
///     </para>
/// </summary>
internal sealed class LruCache<TKey, TValue> : ITrackableResource, IDisposable
    where TKey : notnull
{
    private readonly Dictionary<TKey, Node> _entries;
    private readonly LinkedList<TKey> _order = new();
    private int? _maxEntries;
    private readonly long? _maxBytes;
    private readonly Func<TKey, TValue, long>? _sizeOf;
    private readonly Action<TKey, TValue>? _onEvicted;
    private ResourceRegistration? _registration;

    private long _estimatedBytes;
    private long _hits;
    private long _misses;
    private long _evictions;

    /// <summary>Creates an LRU cache with the given budgets and hooks.</summary>
    /// <param name="resourceName">Stable name for diagnostics (e.g. <c>"TerrainCellMeshes"</c>).</param>
    /// <param name="category">Diagnostics category of the cached values.</param>
    /// <param name="maxEntries">Optional entry-count cap.</param>
    /// <param name="maxBytes">Optional byte cap; requires <paramref name="sizeOf" /> to be meaningful.</param>
    /// <param name="sizeOf">Computes a value's footprint once at <see cref="Set" /> time. Defaults to 1 byte/entry.</param>
    /// <param name="onEvicted">
    ///     Invoked for values displaced by <see cref="Set" />, evicted over budget, trimmed, or
    ///     cleared — the dispose hook for GPU buffer holders. NOT invoked by <see cref="Remove" />.
    /// </param>
    /// <param name="comparer">Key comparer (e.g. OrdinalIgnoreCase for path keys).</param>
    public LruCache(
        string resourceName,
        ResourceCategory category,
        int? maxEntries = null,
        long? maxBytes = null,
        Func<TKey, TValue, long>? sizeOf = null,
        Action<TKey, TValue>? onEvicted = null,
        IEqualityComparer<TKey>? comparer = null)
    {
        if (maxEntries is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Capacity must be > 0.");
        }

        if (maxBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Byte budget must be > 0.");
        }

        ResourceName = resourceName;
        Category = category;
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _sizeOf = sizeOf;
        _onEvicted = onEvicted;
        _entries = new Dictionary<TKey, Node>(comparer);
    }

    /// <summary>
    ///     Registers the cache with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the cache for fluent construction.
    /// </summary>
    public LruCache<TKey, TValue> RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    public string ResourceName { get; }

    public ResourceCategory Category { get; }

    public int Count => _entries.Count;

    public long EstimatedBytes => _estimatedBytes;

    /// <summary>Tests for an entry without touching recency or hit/miss counters.</summary>
    public bool ContainsKey(TKey key) => _entries.ContainsKey(key);

    /// <summary>Fetches an entry, bumping it to most-recently-used on a hit.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            _order.Remove(node.OrderNode);
            _order.AddFirst(node.OrderNode);
            value = node.Value;
            _hits++;
            return true;
        }

        value = default!;
        _misses++;
        return false;
    }

    /// <summary>
    ///     Fetches an entry WITHOUT touching recency or hit/miss counters — for guard probes that
    ///     must not perturb eviction order or stats (e.g. the reference mesh cache validating a
    ///     de-queued decode key: a stale queue entry shouldn't MRU-bump a node nobody asked for).
    /// </summary>
    public bool TryPeek(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            value = node.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    ///     Inserts or replaces an entry (the replaced value goes through <c>onEvicted</c>), then
    ///     evicts least-recently-used entries until the count and byte budgets are satisfied.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            _order.Remove(existing.OrderNode);
            _entries.Remove(key);
            _estimatedBytes -= existing.Size;
            _onEvicted?.Invoke(key, existing.Value);
        }

        var size = _sizeOf?.Invoke(key, value) ?? 1;
        var orderNode = new LinkedListNode<TKey>(key);
        _order.AddFirst(orderNode);
        _entries[key] = new Node(value, size, orderNode);
        _estimatedBytes += size;

        // The just-inserted entry always survives its own Set (Count > 1 guard) — evicting it
        // would run onEvicted (typically Dispose) on a value the caller just handed over and may
        // still be using. A single oversized value simply resides alone, over budget, until the
        // next insert or an explicit trim displaces it.
        EvictWhile(() =>
            _entries.Count > 1 &&
            ((_maxEntries.HasValue && _entries.Count > _maxEntries.Value) ||
             (_maxBytes.HasValue && _estimatedBytes > _maxBytes.Value)));
    }

    /// <summary>
    ///     Removes an entry WITHOUT invoking <c>onEvicted</c> — the caller takes ownership of the
    ///     value (and is responsible for disposing it, where applicable).
    /// </summary>
    public bool Remove(TKey key)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            return false;
        }

        _order.Remove(node.OrderNode);
        _entries.Remove(key);
        _estimatedBytes -= node.Size;
        return true;
    }

    /// <summary>Evicts least-recently-used entries until at most <paramref name="targetBytes" /> remain. Returns bytes released.</summary>
    public long TrimToBytes(long targetBytes)
    {
        var before = _estimatedBytes;
        EvictWhile(() => _estimatedBytes > targetBytes && _entries.Count > 0);
        return before - _estimatedBytes;
    }

    /// <summary>Evicts least-recently-used entries until at most <paramref name="targetCount" /> remain. Returns entries evicted.</summary>
    public int TrimToCount(int targetCount)
    {
        var before = _entries.Count;
        EvictWhile(() => _entries.Count > Math.Max(0, targetCount));
        return before - _entries.Count;
    }

    /// <summary>
    ///     Adjusts the entry-count cap, then evicts least-recently-used entries (each through
    ///     <c>onEvicted</c>) down to the new cap. Raising the cap is a no-op eviction-wise; lowering
    ///     fires the same cascade as <see cref="Set" /> (reusing its exact predicate, including the
    ///     byte-budget term and the just-one-survivor guard). Single-threaded contract: call on the
    ///     owning thread (the same one that calls <see cref="Set" /> / <see cref="TryGet" />).
    /// </summary>
    public void SetMaxEntries(int newMax)
    {
        if (newMax <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newMax), "Capacity must be greater than zero.");
        }

        _maxEntries = newMax;
        EvictWhile(() =>
            _entries.Count > 1 &&
            ((_maxEntries.HasValue && _entries.Count > _maxEntries.Value) ||
             (_maxBytes.HasValue && _estimatedBytes > _maxBytes.Value)));
    }

    /// <summary>Evicts every entry (each through <c>onEvicted</c>) and resets the cache to empty.</summary>
    public void Clear()
    {
        // Walk the order list so eviction order stays LRU-last even in Clear.
        EvictWhile(() => _entries.Count > 0);
    }

    public ResourceStats GetStats() => new()
    {
        EstimatedBytes = Volatile.Read(ref _estimatedBytes),
        EntryCount = _entries.Count,
        Hits = Volatile.Read(ref _hits),
        Misses = Volatile.Read(ref _misses),
        Evictions = Volatile.Read(ref _evictions),
    };

    public void Dispose()
    {
        // Unregister first so the retired-stats record captures the cache as it was at teardown
        // (entries/bytes still resident) rather than post-Clear zeros.
        _registration?.Dispose();
        _registration = null;
        Clear();
    }

    private void EvictWhile(Func<bool> condition)
    {
        while (condition())
        {
            var tail = _order.Last;
            if (tail is null)
            {
                break;
            }

            _order.RemoveLast();
            if (_entries.Remove(tail.Value, out var evicted))
            {
                _estimatedBytes -= evicted.Size;
                _evictions++;
                _onEvicted?.Invoke(tail.Value, evicted.Value);
            }
        }
    }

    private readonly record struct Node(TValue Value, long Size, LinkedListNode<TKey> OrderNode);
}
