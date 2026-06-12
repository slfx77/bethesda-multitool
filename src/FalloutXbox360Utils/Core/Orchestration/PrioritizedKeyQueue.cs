namespace FalloutXbox360Utils.Core.Orchestration;

/// <summary>
///     A de-duplicating priority key queue — the shared shape behind nearest-first streaming decode
///     scheduling (priority = squared distance to the view point) and strict-FIFO build queues
///     (constant priority). Lower priority values dequeue first; equal priorities dequeue in
///     enqueue order (a monotonic sequence number breaks ties, so FIFO stability is guaranteed
///     rather than incidental).
///     <para>
///         A key already queued is not re-enqueued and keeps its first-enqueued priority. Not
///         thread-safe — owner-thread use only, like the queues it replaces.
///     </para>
/// </summary>
internal sealed class PrioritizedKeyQueue<TKey>
    where TKey : notnull
{
    private readonly PriorityQueue<TKey, (float Priority, long Sequence)> _queue = new();
    private readonly HashSet<TKey> _queued;
    private long _sequence;

    public PrioritizedKeyQueue(IEqualityComparer<TKey>? comparer = null)
    {
        _queued = comparer is null ? [] : new HashSet<TKey>(comparer);
    }

    public int Count => _queued.Count;

    /// <summary>
    ///     Enqueues <paramref name="key" /> at <paramref name="priority" /> (lower dequeues first)
    ///     unless it is already queued — the first-enqueued priority wins. Returns true when newly
    ///     enqueued.
    /// </summary>
    public bool Enqueue(TKey key, float priority = 0f)
    {
        if (!_queued.Add(key))
        {
            return false;
        }

        _queue.Enqueue(key, (priority, _sequence++));
        return true;
    }

    /// <summary>Dequeues the lowest-priority key (FIFO among equals).</summary>
    public bool TryDequeue(out TKey key)
    {
        if (_queue.TryDequeue(out key!, out _))
        {
            _queued.Remove(key);
            return true;
        }

        key = default!;
        return false;
    }

    public bool Contains(TKey key) => _queued.Contains(key);

    public void Clear()
    {
        _queue.Clear();
        _queued.Clear();
    }
}
