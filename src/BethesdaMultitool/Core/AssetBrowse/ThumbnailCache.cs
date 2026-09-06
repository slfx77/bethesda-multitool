using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Resources;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Identity of one cached thumbnail. The cell size is part of the key because the same asset is
///     legitimately cached at more than one size — a gallery cell and a larger preview strip — and a
///     key without it would serve the wrong-sized bitmap to whichever asked second.
/// </summary>
internal readonly record struct ThumbnailKey(string SourceLabel, string VirtualPath, int CellPixels);

/// <summary>
///     Bounded cache of decoded gallery thumbnails, composed over the existing
///     <see cref="LruCache{TKey,TValue}" /> rather than hand-rolled, so it inherits the resource
///     registry's accounting and trimming for free.
///     <para>
///         Decoding is the expensive half — a classic sprite may pull a palette out of a 339 MB
///         archive — so caching is what makes scrolling a retail install viable at all. Entries are
///         plain managed byte arrays, hence <see cref="ResourceCategory.CpuCache" />; they are
///         rebuildable, so trimming under pressure costs only time.
///     </para>
///     <para>
///         A NEGATIVE result (the asset has no picture, or this build cannot decode it yet) is
///         cached too, as an empty thumbnail. Over a retail install that case is common, and
///         re-attempting a failing decode on every scroll would be the single worst thing this
///         cache could do.
///     </para>
/// </summary>
internal sealed class ThumbnailCache : IDisposable
{
    private readonly LruCache<ThumbnailKey, RgbaThumbnail> _cache;

    public ThumbnailCache(int maxEntries = 2048, long maxBytes = 64L * 1024 * 1024)
    {
        _cache = new LruCache<ThumbnailKey, RgbaThumbnail>(
            "AssetBrowserThumbnails",
            ResourceCategory.CpuCache,
            maxEntries,
            maxBytes,
            sizeOf: static (_, value) => value.Rgba?.LongLength ?? 1);
    }

    /// <summary>Entries currently held.</summary>
    public int Count => _cache.Count;

    /// <summary>Registers with the resource registry so the Diagnostics tab can see and trim this.</summary>
    public ThumbnailCache RegisterWith(ResourceRegistry registry)
    {
        _cache.RegisterWith(registry);
        return this;
    }

    public void Dispose()
    {
        _cache.Dispose();
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
            return cached.Rgba is null ? null : cached;
        }

        var produced = render();

        // An empty array is the sentinel for "asked, and there is no picture" — distinct from an
        // absent key, which still means "not tried".
        _cache.Set(key, produced ?? new RgbaThumbnail(0, 0, null!));
        return produced;
    }

    /// <summary>True when this key has already been decoded, successfully or not.</summary>
    public bool Contains(ThumbnailKey key) => _cache.ContainsKey(key);
}
