namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     LRU-evicting cache keyed by exterior cell grid coordinate. Pure CPU / managed —
///     no D3D dependency — so it can be unit-tested without a GPU device. The generic
///     entry type lets the v3 Phase 2a <see cref="TerrainRenderer" /> cache its terrain
///     meshes here and the eventual Phase 3 REFR mesh cache reuse the same plumbing.
///     <para>
///         Capacity caps memory growth as the user pans across huge worldspaces
///         (WastelandNV has ~4000 exterior cells). When the cache exceeds capacity,
///         <see cref="Insert" /> evicts the least-recently-touched entry and disposes
///         it — releasing its GPU buffers when <typeparamref name="TEntry" /> is a
///         buffer-holder.
///     </para>
///     <para>
///         <see cref="TryGet" /> auto-bumps a hit to most-recently-used, so steady-state
///         visible cells stay resident even when the cache is full. Not thread-safe; all
///         calls happen on the WinUI 3 UI thread alongside the D3D11 immediate context.
///     </para>
/// </summary>
internal sealed class CellMeshLruCache<TEntry> : IDisposable where TEntry : IDisposable
{
    private readonly Dictionary<(int gx, int gy), Node> _entries = new();
    private readonly LinkedList<(int gx, int gy)> _order = new();

    public CellMeshLruCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _entries.Count;

    /// <summary>Tests whether the cache currently holds an entry for <paramref name="key" /> without touching recency.</summary>
    public bool ContainsKey((int gx, int gy) key) => _entries.ContainsKey(key);

    /// <summary>
    ///     Fetches an entry by grid coordinate. On a hit, the entry is bumped to the front of
    ///     the eviction queue so subsequent <see cref="Insert" /> calls evict colder entries first.
    /// </summary>
    public bool TryGet((int gx, int gy) key, out TEntry entry)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            _order.Remove(node.OrderNode);
            _order.AddFirst(node.OrderNode);
            entry = node.Entry;
            return true;
        }

        entry = default!;
        return false;
    }

    /// <summary>
    ///     Inserts an entry. If <paramref name="key" /> already maps to an entry, the existing
    ///     entry is disposed first. After insert, evicts the least-recently-used entry until
    ///     the cache is at or below capacity.
    /// </summary>
    public void Insert((int gx, int gy) key, TEntry entry)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            _order.Remove(existing.OrderNode);
            existing.Entry.Dispose();
        }

        var orderNode = new LinkedListNode<(int gx, int gy)>(key);
        _order.AddFirst(orderNode);
        _entries[key] = new Node(entry, orderNode);

        while (_entries.Count > Capacity)
        {
            var tail = _order.Last;
            if (tail is null) break;
            _order.RemoveLast();
            if (_entries.Remove(tail.Value, out var evicted))
            {
                evicted.Entry.Dispose();
            }
        }
    }

    /// <summary>Disposes every entry and resets the cache to empty.</summary>
    public void Clear()
    {
        foreach (var node in _entries.Values) node.Entry.Dispose();
        _entries.Clear();
        _order.Clear();
    }

    public void Dispose() => Clear();

    private readonly record struct Node(TEntry Entry, LinkedListNode<(int gx, int gy)> OrderNode);
}
