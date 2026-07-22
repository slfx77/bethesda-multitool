namespace BethesdaMultitool.Core.Orchestration;

/// <summary>
///     A de-duplicating priority key queue — the shared shape behind nearest-first streaming decode
///     scheduling (priority = squared distance to the view point) and strict-FIFO build queues
///     (constant priority). Lower priority values dequeue first; equal priorities dequeue in
///     enqueue order (a monotonic sequence number breaks ties, so FIFO stability is guaranteed
///     rather than incidental).
///     <para>
///         Re-enqueueing a queued key with a LOWER priority promotes it (lazy decrease-key: the
///         old entry stays in the heap and is skipped on dequeue). This keeps distances live as
///         the camera moves — a mesh first sighted at the far edge is re-offered nearer every
///         frame it stays visible, so it stops waiting behind the whole far-field backlog.
///         Re-enqueueing at an equal/higher priority is a no-op (the queue never demotes).
///         Not thread-safe — owner-thread use only, like the queues it replaces.
///     </para>
/// </summary>
internal sealed class PrioritizedKeyQueue<TKey>
    where TKey : notnull
{
    private readonly PriorityQueue<TKey, (float Priority, long Sequence)> _queue = new();
    private readonly Dictionary<TKey, float> _bestPriority;
    private long _sequence;

    public PrioritizedKeyQueue(IEqualityComparer<TKey>? comparer = null)
    {
        _bestPriority = comparer is null ? [] : new Dictionary<TKey, float>(comparer);
    }

    public int Count => _bestPriority.Count;

    /// <summary>
    ///     Enqueues <paramref name="key" /> at <paramref name="priority" /> (lower dequeues first).
    ///     A queued key is promoted when the new priority is strictly lower, otherwise unchanged.
    ///     Returns true only when the key was NEWLY enqueued (not on promotion).
    /// </summary>
    public bool Enqueue(TKey key, float priority = 0f)
    {
        if (_bestPriority.TryGetValue(key, out var existing))
        {
            if (priority >= existing)
            {
                return false;
            }

            // Lazy decrease-key: record the better priority and push a second heap entry; the
            // superseded entry is skipped at dequeue time (its priority no longer matches).
            _bestPriority[key] = priority;
            _queue.Enqueue(key, (priority, _sequence++));
            return false;
        }

        _bestPriority[key] = priority;
        _queue.Enqueue(key, (priority, _sequence++));
        return true;
    }

    /// <summary>Dequeues the lowest-priority key (FIFO among equals), skipping superseded entries.</summary>
    public bool TryDequeue(out TKey key)
    {
        while (_queue.TryDequeue(out key!, out var entry))
        {
            // Only the entry carrying the key's CURRENT best priority is live; entries left behind
            // by a promotion (or by a full dequeue + later re-enqueue cycle) are stale duplicates.
            // Bitwise identity, not tolerance: the live entry's priority was assigned from the very
            // same float stored in _bestPriority, so exact-bits matching is the intended check.
            if (_bestPriority.TryGetValue(key, out var best) &&
                BitConverter.SingleToInt32Bits(best) == BitConverter.SingleToInt32Bits(entry.Priority))
            {
                _bestPriority.Remove(key);
                return true;
            }
        }

        key = default!;
        return false;
    }

    public bool Contains(TKey key) => _bestPriority.ContainsKey(key);

    public void Clear()
    {
        _queue.Clear();
        _bestPriority.Clear();
    }
}
