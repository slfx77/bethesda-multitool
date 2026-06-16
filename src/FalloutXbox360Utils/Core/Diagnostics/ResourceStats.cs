namespace FalloutXbox360Utils.Core.Diagnostics;

/// <summary>
///     Point-in-time counters reported by an <see cref="ITrackableResource" />. Cache-shaped
///     resources populate the byte/entry/hit/miss/eviction fields; queue-shaped resources populate
///     depth/in-flight/processed. Fields that do not apply stay at their defaults and diagnostics
///     surfaces blank them by category.
/// </summary>
internal readonly record struct ResourceStats
{
    /// <summary>Estimated resident bytes. Disk caches report on-disk bytes; mapped files report the view length.</summary>
    public long EstimatedBytes { get; init; }

    /// <summary>Entries currently held (cache entries, registered items).</summary>
    public long EntryCount { get; init; }

    public long Hits { get; init; }

    public long Misses { get; init; }

    public long Evictions { get; init; }

    /// <summary>Work items queued but not yet started (queue-shaped resources).</summary>
    public int QueueDepth { get; init; }

    /// <summary>Work items currently executing (queue-shaped resources).</summary>
    public int InFlight { get; init; }

    /// <summary>Work items completed over the resource lifetime (queue-shaped resources).</summary>
    public long Processed { get; init; }

    /// <summary>Work items that threw (queue-shaped resources).</summary>
    public long Failures { get; init; }

    /// <summary>Message of the most recent work-item failure, when one has occurred.</summary>
    public string? LastError { get; init; }

    /// <summary>Hit rate in [0, 1], or null when no lookups have been recorded.</summary>
    public double? HitRate
    {
        get
        {
            var total = Hits + Misses;
            return total == 0 ? null : Hits / (double)total;
        }
    }
}

/// <summary>
///     One row of a <see cref="ResourceRegistry.GetSnapshot" /> result. In the retired list,
///     repeated runs under the same name (e.g. a <c>ParallelWork</c> loop invoked per item) collapse
///     into one row whose throughput counters accumulate; <paramref name="RunCount" /> is how many
///     lifetimes the row aggregates.
/// </summary>
internal sealed record ResourceSnapshotRecord(
    string DisplayName,
    ResourceCategory Category,
    ResourceStats Stats,
    int RunCount = 1);
