using System.Collections.Concurrent;

namespace BethesdaMultitool.Core.Resources;

/// <summary>
///     A cache of <b>derived, rebuildable</b> values with a byte ceiling and oldest-first eviction.
///     Keyed by reference identity, safe to read from any thread.
///     <para>
///         Written for the two per-cell caches in <c>WorldRenderCache</c>, which were unbounded
///         <c>ConcurrentDictionary</c>s with <c>GetOrAdd</c> and no <c>Remove</c> — they only ever
///         grew, for the life of the session. Invisible on a Fallout 3 worldspace and ruinous on
///         Fallout 76's, where a cell's terrain texture set is about 1 MB and Appalachia has 41,219
///         cells: fly far enough and every one of them ends up cached. The bulk pre-warm was already
///         refused at that scale, but nothing stopped the same total accumulating one streamed cell
///         at a time.
///     </para>
///     <para>
///         <b>Eviction is only safe because of what is stored here.</b> The values are immutable and
///         derived from worldspace-static inputs, and callers keep their own reference after a hit,
///         so an evicted entry is <i>rebuilt</i> on the next miss rather than lost. Do not use this
///         for anything that owns unmanaged state or whose identity matters.
///     </para>
///     <para>
///         FIFO rather than LRU, deliberately: reads happen on the render thread every frame, and
///         maintaining recency there would cost more than the occasional rebuild it saves.
///     </para>
/// </summary>
internal sealed class BoundedDerivedCache<TKey, TValue>
    where TKey : class
    where TValue : class
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries;
    private readonly ConcurrentQueue<TKey> _insertionOrder = new();
    private long _bytes;

    /// <summary>Creates a cache that evicts in insertion order once it exceeds a byte ceiling.</summary>
    /// <param name="maxBytes">
    ///     Ceiling for the whole cache. A non-positive value means unbounded — useful in tests that
    ///     want the cache's behaviour without its eviction.
    /// </param>
    public BoundedDerivedCache(long maxBytes)
    {
        MaxBytes = maxBytes;
        _entries = new ConcurrentDictionary<TKey, Entry>(ReferenceEqualityComparer.Instance);
    }

    /// <summary>Ceiling for the whole cache; non-positive means unbounded.</summary>
    public long MaxBytes { get; }

    /// <summary>Live entries.</summary>
    public int Count => _entries.Count;

    /// <summary>Bytes currently held.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Entries dropped to stay inside the ceiling, since construction.</summary>
    public long EvictionCount => Interlocked.Read(ref _evictionCount);

    private long _evictionCount;

    /// <summary>
    ///     Looks up <paramref name="key" />. Returns true when an entry is present <i>even if its
    ///     value is null</i> — a null result is a real, cached answer for these caches ("this cell
    ///     has no texture data"), and conflating it with a miss would rebuild it forever.
    /// </summary>
    public bool TryGet(TKey key, out TValue? value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            value = entry.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    ///     Installs <paramref name="value" />, then evicts oldest-first until the total is back
    ///     inside <see cref="MaxBytes" />. Returns false when another thread installed this key
    ///     first, in which case nothing was added and nothing evicted.
    /// </summary>
    /// <param name="evictedBytes">Bytes dropped by this call, for the caller's own accounting.</param>
    public bool TryAdd(TKey key, TValue? value, long bytes, out long evictedBytes)
    {
        evictedBytes = 0;
        if (!_entries.TryAdd(key, new Entry(value, bytes)))
        {
            return false;
        }

        _insertionOrder.Enqueue(key);
        Interlocked.Add(ref _bytes, bytes);
        if (MaxBytes <= 0)
        {
            return true;
        }

        while (Interlocked.Read(ref _bytes) > MaxBytes && _insertionOrder.TryDequeue(out var oldest))
        {
            // Never evict the entry just installed. The caller is about to return it, and a single
            // value larger than the entire budget would otherwise make this spin until the queue
            // emptied — throwing away the whole cache to make room for something still too big.
            if (ReferenceEquals(oldest, key))
            {
                _insertionOrder.Enqueue(oldest);
                break;
            }

            if (!_entries.TryRemove(oldest, out var removed))
            {
                continue; // already gone; its bytes were subtracted by whoever removed it
            }

            Interlocked.Add(ref _bytes, -removed.Bytes);
            Interlocked.Increment(ref _evictionCount);
            evictedBytes += removed.Bytes;
        }

        return true;
    }

    private readonly record struct Entry(TValue? Value, long Bytes);
}
