using BethesdaMultitool.Core.Diagnostics;
using Slfx77.Multitool.Core.Caching;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>Maps one browser's real Shared thumbnail caches into its stable diagnostics and pressure participant.</summary>
/// <remarks>Owns only observation delegates and scalar completed-source counters, never a cache or decoded pixels.
/// Source attachment and successful retirement run on the browser thread. Queries and trimming may run on any thread.
/// The caller must drain Shared's loader before completing an observation; failed drains remain observable.</remarks>
internal sealed class ThumbnailCacheObservation : IMemoryPressureParticipant
{
    private readonly Lock _gate = new();
    private ResourceRegistration? _registration;
    private ObservedSource? _source;
    private CacheStatistics _history;
    private bool _disposed;

    /// <summary>Registers one resource for the entire browser lifetime, including source changes.</summary>
    /// <param name="registry">The existing application registry, or a separate registry for behavioral tests.</param>
    internal ThumbnailCacheObservation(ResourceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registration = registry.Register(this);
    }

    /// <inheritdoc />
    public string ResourceName => "AssetBrowserThumbnails";
    /// <inheritdoc />
    public ResourceCategory Category => ResourceCategory.CpuCache;
    /// <inheritdoc />
    public int TrimPriority => 20;
    /// <inheritdoc />
    public TrimAffinity TrimAffinity => TrimAffinity.AnyThread;

    /// <summary>Observes one loader before work starts; an undrained predecessor cannot be replaced.</summary>
    /// <param name="identity">The exact application loader instance, independent of names and source generations.</param>
    /// <param name="statistics">Shared's any-thread real resident totals and lifetime counters.</param>
    /// <param name="trimToBytes">Shared's any-thread trim returning actual bytes released.</param>
    internal void Attach(object identity, Func<CacheStatistics> statistics, Func<long, long> trimToBytes)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(trimToBytes);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_source is not null) throw new InvalidOperationException("The preceding thumbnail source has not retired.");
            _source = new ObservedSource(identity, statistics, trimToBytes);
        }
    }

    /// <summary>Moves final real counters into scalar history only after successful loader drain and cache clearing.</summary>
    /// <param name="identity">The exact loader whose DisposeAsync completed successfully.</param>
    /// <exception cref="InvalidOperationException">The source still reports resident data and cannot be retired.</exception>
    internal void CompleteRetirement(object identity)
    {
        ObservedSource? source;
        lock (_gate) source = _source;
        if (source is null || !ReferenceEquals(source.Identity, identity)) return;
        var final = source.Statistics();
        if (final.Count != 0 || final.Bytes != 0)
            throw new InvalidOperationException("A thumbnail source still has resident cache data after retirement.");
        lock (_gate)
        {
            if (!ReferenceEquals(_source, source)) return;
            _history = new CacheStatistics(0, 0, checked(_history.Hits + final.Hits),
                checked(_history.Misses + final.Misses), checked(_history.Evictions + final.Evictions));
            _source = null;
        }
    }

    /// <summary>Reports actual resident memory and cumulative source counters without holding a gate during callbacks.</summary>
    /// <returns>Individually atomic cache observations; concurrent decoding is not a transactional snapshot.</returns>
    public ResourceStats GetStats()
    {
        ObservedSource? source;
        CacheStatistics history;
        lock (_gate) { source = _source; history = _history; }
        var current = source?.Statistics() ?? default;
        return new ResourceStats
        {
            EstimatedBytes = current.Bytes, EntryCount = current.Count,
            Hits = checked(history.Hits + current.Hits), Misses = checked(history.Misses + current.Misses),
            Evictions = checked(history.Evictions + current.Evictions)
        };
    }

    /// <summary>Requests the established half/zero pressure target from the real observed cache.</summary>
    /// <param name="level">Ordinary pressure halves observed bytes; aggressive pressure requests zero.</param>
    /// <returns>Actual estimated bytes removed, allowing concurrent producers to refill afterward.</returns>
    public long Trim(TrimLevel level)
    {
        ObservedSource? source;
        lock (_gate) source = _source;
        return source?.TrimToBytes(level == TrimLevel.Aggressive ? 0 : source.Statistics().Bytes / 2) ?? 0;
    }

    /// <summary>Completes browser observation only after the source owner has proved retirement.</summary>
    /// <remarks>This explicit completion keeps an undrained source registered and rejects premature retirement.</remarks>
    /// <exception cref="InvalidOperationException">An attached or failed source must remain observable.</exception>
    internal void CompleteBrowserRetirement()
    {
        ResourceRegistration? registration;
        lock (_gate)
        {
            if (_disposed) return;
            if (_source is not null) throw new InvalidOperationException("Thumbnail source retirement is still required.");
            _disposed = true;
            registration = _registration;
            _registration = null;
        }
        registration?.Dispose();
    }

    /// <summary>Retains only callbacks for the current real loader; successful retirement discards this entire object.</summary>
    private sealed record ObservedSource(object Identity, Func<CacheStatistics> Statistics, Func<long, long> TrimToBytes);
}
