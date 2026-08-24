namespace BethesdaMultitool.Core.Diagnostics;

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

    /// <summary>
    ///     Which physical pool <see cref="EstimatedBytes" /> occupies, for GPU-backed resources.
    ///     Defaults to <see cref="GpuMemorySegment.Unspecified" />, so every existing construction
    ///     site keeps its current meaning. Consumers that steer on VRAM pressure must filter on
    ///     <see cref="GpuMemorySegment.Local" /> — see the rationale on <see cref="GpuMemorySegment" />.
    /// </summary>
    public GpuMemorySegment Segment { get; init; }

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

/// <summary>
///     A <see cref="ResourceRegistry.Snapshot" /> result: the rows plus the per-category byte totals
///     accumulated during the same walk, so no consumer has to re-enumerate (and re-call every
///     <see cref="ITrackableResource.GetStats" />) to get a total.
/// </summary>
internal sealed class RegistrySnapshot
{
    internal static readonly int CategoryCount = Enum.GetValues<ResourceCategory>().Length;

    private readonly long[] _totalsByCategory;

    internal RegistrySnapshot(IReadOnlyList<ResourceSnapshotRecord> rows, long[] totalsByCategory)
    {
        Rows = rows;
        _totalsByCategory = totalsByCategory;
    }

    public IReadOnlyList<ResourceSnapshotRecord> Rows { get; }

    /// <summary>
    ///     Bytes registered under <paramref name="category" />.
    ///     <para>
    ///         Categories do not overlap, which is what keeps this safe:
    ///         <see cref="ResourceCategory.GpuAttributed" /> deliberately sits outside
    ///         <see cref="ResourceCategory.GpuResident" /> so asking for resident GPU bytes can never
    ///         double-count a pool that also reports its own attributed size.
    ///     </para>
    /// </summary>
    public long TotalBytes(ResourceCategory category) => _totalsByCategory[(int)category];

    /// <summary>
    ///     Bytes under <paramref name="category" /> restricted to one physical GPU pool. This is the
    ///     form a VRAM governor must use — see <see cref="GpuMemorySegment" /> for why an
    ///     unrestricted GPU total is not a meaningful number on a discrete adapter.
    /// </summary>
    public long TotalBytes(ResourceCategory category, GpuMemorySegment segment)
    {
        long total = 0;
        foreach (var row in Rows)
        {
            if (row.Category == category && row.Stats.Segment == segment)
            {
                total += row.Stats.EstimatedBytes;
            }
        }

        return total;
    }
}
