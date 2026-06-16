using System.Collections.Concurrent;
using FalloutXbox360Utils.Core.Diagnostics;

namespace FalloutXbox360Utils.Core.Resources;

/// <summary>
///     Thread-safe get-or-create cache built on <c>ConcurrentDictionary&lt;TKey, Lazy&lt;TValue?&gt;&gt;</c>
///     — the shared skeleton behind the texture resolvers. The factory runs at most once per key
///     (<see cref="LazyThreadSafetyMode.ExecutionAndPublication" />); a factory that throws has its
///     entry removed so the next request retries; a factory that returns null caches the negative so
///     a missing asset is not re-searched.
///     <para>
///         There is no recency order, so <see cref="Trim" /> at <see cref="TrimLevel.Gentle" /> is a
///         no-op and <see cref="TrimLevel.Aggressive" /> clears realized positive entries — every
///         factory-backed consumer rebuilds transparently through the factory. Negative entries are
///         kept (they are tiny and prevent re-searching archives for assets that do not exist), and
///         so are <see cref="Inject" />ed entries: a value supplied from outside has no factory
///         rebuild path, so trimming it would lose it permanently (the consumer would re-request the
///         key and the factory would return null for a synthetic, disk-unbacked asset). Injected
///         entries are released only by an explicit <see cref="Evict" /> / <see cref="Release" />.
///     </para>
/// </summary>
internal sealed class ConcurrentLazyCache<TKey, TValue> : IMemoryPressureParticipant, IDisposable
    where TKey : notnull
    where TValue : class
{
    private readonly ConcurrentDictionary<TKey, Lazy<TValue?>> _cache;

    // Keys whose current entry was supplied via Inject (no factory rebuild path). Trim skips these;
    // they leave the cache only through an explicit Evict/Release. Shares the cache's key comparer.
    private readonly ConcurrentDictionary<TKey, byte> _injectedKeys;

    private readonly Func<TKey, TValue?> _factory;
    private readonly Func<TValue, long>? _sizeOf;
    private ResourceRegistration? _registration;

    private long _estimatedBytes;
    private long _hits;
    private long _misses;
    private long _evictions;

    public ConcurrentLazyCache(
        string resourceName,
        ResourceCategory category,
        Func<TKey, TValue?> factory,
        Func<TValue, long>? sizeOf = null,
        IEqualityComparer<TKey>? comparer = null,
        int trimPriority = 10)
    {
        ResourceName = resourceName;
        Category = category;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _sizeOf = sizeOf;
        TrimPriority = trimPriority;
        _cache = comparer is null ? new() : new(comparer);
        _injectedKeys = comparer is null ? new() : new(comparer);
    }

    /// <summary>
    ///     Registers the cache with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the cache for fluent construction.
    /// </summary>
    public ConcurrentLazyCache<TKey, TValue> RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    public string ResourceName { get; }

    public ResourceCategory Category { get; }

    public int TrimPriority { get; }

    public TrimAffinity TrimAffinity => TrimAffinity.AnyThread;

    public int Count => _cache.Count;

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Returns the cached value for <paramref name="key" />, invoking the factory on first request.</summary>
    public TValue? GetOrCreate(TKey key)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _hits);
            return GetValueOrRemoveFaulted(key, cached);
        }

        var created = CreateEntry(key);
        var actual = _cache.GetOrAdd(key, created);
        if (!ReferenceEquals(actual, created))
        {
            Interlocked.Increment(ref _hits);
        }

        return GetValueOrRemoveFaulted(key, actual);
    }

    /// <summary>
    ///     Injects a pre-built value, replacing any existing entry for the key. The key is pinned
    ///     against <see cref="Trim" /> until an explicit <see cref="Evict" /> / <see cref="Release" />,
    ///     because an injected value has no factory rebuild path.
    /// </summary>
    public void Inject(TKey key, TValue value)
    {
        var entry = new Lazy<TValue?>(() => value, LazyThreadSafetyMode.ExecutionAndPublication);
        // Realize immediately so byte accounting and Release see a created value.
        _ = entry.Value;
        AddBytes(value);
        // Pin before publishing the entry so a concurrent Trim can never observe it as trimmable.
        _injectedKeys[key] = 0;
        if (_cache.TryGetValue(key, out var previous) &&
            _cache.TryUpdate(key, entry, previous))
        {
            SubtractRealizedBytes(previous);
            return;
        }

        var existing = _cache.GetOrAdd(key, entry);
        if (!ReferenceEquals(existing, entry))
        {
            // Lost the race; fall back to an unconditional swap.
            _cache[key] = entry;
            SubtractRealizedBytes(existing);
        }
    }

    /// <summary>Removes the entry for <paramref name="key" /> regardless of state.</summary>
    public bool Evict(TKey key)
    {
        if (!_cache.TryRemove(key, out var removed))
        {
            return false;
        }

        _injectedKeys.TryRemove(key, out _);
        SubtractRealizedBytes(removed);
        Interlocked.Increment(ref _evictions);
        return true;
    }

    /// <summary>
    ///     Drops the realized value for <paramref name="key" /> after its consumer no longer needs
    ///     the CPU copy (e.g. the payload has been uploaded to the GPU). Removes only if the entry is
    ///     still the same one observed (a concurrent re-decode is never clobbered). When
    ///     <paramref name="keepNegative" /> is true, a realized null (not-found) entry is left in
    ///     place so the miss is not re-searched.
    /// </summary>
    public bool Release(TKey key, bool keepNegative = true)
    {
        if (!_cache.TryGetValue(key, out var existing) || !existing.IsValueCreated)
        {
            return false;
        }

        TValue? value;
        try
        {
            value = existing.Value;
        }
        catch
        {
            if (_cache.TryRemove(new KeyValuePair<TKey, Lazy<TValue?>>(key, existing)))
            {
                _injectedKeys.TryRemove(key, out _);
            }

            throw;
        }

        if (value is null && keepNegative)
        {
            return false;
        }

        if (_cache.TryRemove(new KeyValuePair<TKey, Lazy<TValue?>>(key, existing)))
        {
            _injectedKeys.TryRemove(key, out _);
            if (value is not null)
            {
                SubtractBytes(value);
            }

            Interlocked.Increment(ref _evictions);
            return true;
        }

        return false;
    }

    /// <summary>Records a cache hit observed by a caller outside this cache (parity with the resolvers' RecordCacheHit).</summary>
    public void RecordExternalHit() => Interlocked.Increment(ref _hits);

    /// <summary>
    ///     <see cref="TrimLevel.Aggressive" />: removes every realized positive entry that has a
    ///     factory rebuild path (it rebuilds transparently on next request); negatives, in-flight
    ///     entries, and <see cref="Inject" />ed entries stay (an injected value cannot be rebuilt, so
    ///     trimming it would lose it permanently). <see cref="TrimLevel.Gentle" />: no-op — there is
    ///     no recency order to shed cold entries by.
    /// </summary>
    public long Trim(TrimLevel level)
    {
        if (level != TrimLevel.Aggressive)
        {
            return 0;
        }

        long released = 0;
        foreach (var pair in _cache)
        {
            if (_injectedKeys.ContainsKey(pair.Key))
            {
                continue;
            }

            if (!pair.Value.IsValueCreated)
            {
                continue;
            }

            TValue? value;
            try
            {
                value = pair.Value.Value;
            }
            catch
            {
                value = null;
            }

            if (value is null)
            {
                continue;
            }

            if (_cache.TryRemove(pair))
            {
                released += _sizeOf?.Invoke(value) ?? 0;
                SubtractBytes(value);
                Interlocked.Increment(ref _evictions);
            }
        }

        return released;
    }

    public ResourceStats GetStats() => new()
    {
        EstimatedBytes = Interlocked.Read(ref _estimatedBytes),
        EntryCount = _cache.Count,
        Hits = Interlocked.Read(ref _hits),
        Misses = Interlocked.Read(ref _misses),
        Evictions = Interlocked.Read(ref _evictions),
    };

    public void Dispose()
    {
        _registration?.Dispose();
    }

    private Lazy<TValue?> CreateEntry(TKey key) =>
        new(() =>
        {
            Interlocked.Increment(ref _misses);
            var value = _factory(key);
            if (value is not null)
            {
                AddBytes(value);
            }

            return value;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

    private TValue? GetValueOrRemoveFaulted(TKey key, Lazy<TValue?> entry)
    {
        try
        {
            return entry.Value;
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<TKey, Lazy<TValue?>>(key, entry));
            throw;
        }
    }

    private void AddBytes(TValue value)
    {
        if (_sizeOf is not null)
        {
            Interlocked.Add(ref _estimatedBytes, _sizeOf(value));
        }
    }

    private void SubtractBytes(TValue value)
    {
        if (_sizeOf is not null)
        {
            Interlocked.Add(ref _estimatedBytes, -_sizeOf(value));
        }
    }

    private void SubtractRealizedBytes(Lazy<TValue?> entry)
    {
        if (!entry.IsValueCreated)
        {
            return;
        }

        TValue? value;
        try
        {
            value = entry.Value;
        }
        catch
        {
            return;
        }

        if (value is not null)
        {
            SubtractBytes(value);
        }
    }
}
