using BethesdaMultitool.Core.Diagnostics;
using Slfx77.Multitool.Core.Caching;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Identity of one cached thumbnail. The cell size is part of the key because the same asset is
///     legitimately cached at more than one size — a gallery cell and a larger preview strip — and a
///     key without it would serve the wrong-sized bitmap to whichever asked second.
/// </summary>
internal readonly record struct ThumbnailKey(Guid SourceId, string VirtualPath, int CellPixels);

/// <summary>
///     Bounded cache of decoded gallery thumbnails, with shared storage and an app-specific
///     resource registry adapter. A fresh source identity prevents same-path reloads from reusing
///     thumbnails produced before the source changed.
///     <para>
///         Decoding is the expensive half — a classic sprite may pull a palette out of a 339 MB
///         archive — so caching is what makes scrolling a retail install viable at all. Entries are
///         plain managed byte arrays, hence <see cref="ResourceCategory.CpuCache" />; they are
///         rebuildable, so trimming under pressure costs only time.
///     </para>
///     <para>
///         A NEGATIVE result (the asset has no picture, or this build cannot decode it yet) is
///         cached too, as a null value. Over a retail install that case is common, and
///         re-attempting a failing decode on every scroll would be the single worst thing this
///         cache could do.
///     </para>
/// </summary>
internal sealed class ThumbnailCache : IDisposable, IMemoryPressureParticipant
{
    private readonly BoundedLruCache<ThumbnailKey, RgbaThumbnail?> _cache;
    private ResourceRegistration? _registration;

    /// <summary>Creates strict entry and estimated-byte limits, charging unreadable thumbnail results one byte each.</summary>
    public ThumbnailCache(int maxEntries = 2048, long maxBytes = 64L * 1024 * 1024)
    {
        _cache = new BoundedLruCache<ThumbnailKey, RgbaThumbnail?>(
            maxEntries,
            maxBytes,
            static value => value?.Rgba?.LongLength ?? 1);
    }

    /// <summary>Entries currently held.</summary>
    public int Count => _cache.Statistics.Count;

    /// <summary>Returns the stable diagnostics identity for gallery thumbnail memory.</summary>
    public string ResourceName => "AssetBrowserThumbnails";

    /// <summary>Classifies retained decoded bytes as rebuildable CPU cache memory.</summary>
    public ResourceCategory Category => ResourceCategory.CpuCache;

    /// <summary>Returns the resource registry's trim ordering priority for thumbnails.</summary>
    public int TrimPriority => 20;

    /// <summary>Allows synchronized cache trimming from any thread.</summary>
    public TrimAffinity TrimAffinity => TrimAffinity.AnyThread;

    /// <summary>Maps shared cache counts, estimated bytes, hits, misses, and evictions into native resource diagnostics.</summary>
    public ResourceStats GetStats()
    {
        var stats = _cache.Statistics;
        return new ResourceStats
        {
            EstimatedBytes = stats.Bytes, EntryCount = stats.Count, Hits = stats.Hits,
            Misses = stats.Misses, Evictions = stats.Evictions
        };
    }

    /// <summary>Evicts least-recently-used payloads to half the current byte estimate, or clears every entry for aggressive pressure.</summary>
    /// <returns>The estimated number of bytes released.</returns>
    public long Trim(TrimLevel level) => _cache.TrimToBytes(level == TrimLevel.Aggressive ? 0 : _cache.Statistics.Bytes / 2);

    /// <summary>Releases retired source payloads after the thumbnail worker has drained.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>Retires diagnostic registration before clearing payloads, preserving final counters for the resource registry.</summary>
    public void Dispose()
    {
        // Capture the final diagnostic counters before releasing the retained payloads.
        _registration?.Dispose();
        _registration = null;
        _cache.Clear();
    }

    /// <summary>Registers with the resource registry so the Diagnostics tab can see and trim this.</summary>
    public ThumbnailCache RegisterWith(ResourceRegistry registry)
    {
        _registration?.Dispose();
        _registration = registry.Register(this);
        return this;
    }

    /// <summary>
    ///     Returns the cached thumbnail for <paramref name="key" />, producing it with
    ///     <paramref name="render" /> on a miss. A null render result is remembered as "nothing to
    ///     show" and reported back as null.
    /// </summary>
    public RgbaThumbnail? GetOrAdd(ThumbnailKey key, Func<RgbaThumbnail?> render)
    {
        ArgumentNullException.ThrowIfNull(render);

        if (_cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var produced = render();

        _cache.Set(key, produced);
        return produced;
    }

    /// <summary>True when this key has already been decoded, successfully or not.</summary>
    public bool Contains(ThumbnailKey key)
    {
        return _cache.Contains(key);
    }
}
