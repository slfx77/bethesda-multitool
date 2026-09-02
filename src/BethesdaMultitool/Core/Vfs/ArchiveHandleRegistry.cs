using BethesdaMultitool.Core.Formats.Bsa.Index;

namespace BethesdaMultitool.Core.Vfs;

/// <summary>
///     Refcounted process-wide cache of shared <see cref="ArchiveReader" /> handles, so the many
///     subsystems that open the same archive (2D map palette, 3D viewer resolvers, asset packing,
///     string tables) share one parse + one memory map instead of each paying their own.
///     <para>
///         Lifecycle: <see cref="Acquire" /> hands out an <see cref="ArchiveLease" />; the reader
///         is disposed when the last lease releases (dispose-at-zero, so Windows file locks are
///         released promptly for the repack/validate flows and concurrent sessions), unless a
///         <see cref="KeepWarm" /> scope is active, in which case zero-refcount handles are parked
///         until the last scope closes. Every acquire re-stats the file and reopens when
///         (length, mtime) changed; leases on the detached stale handle stay valid until released.
///     </para>
///     <para>
///         Shared handles are read-only: the raw byte extraction path is lock-free thread-safe,
///         and <c>BsaExtractor</c> conversion toggles throw on registry-owned instances (they are
///         per-instance state). Sites that need conversion-enabled extraction must open private
///         extractors, not registry leases.
///     </para>
///     <para>
///         Known identity limits: a same-length rewrite within filesystem timestamp granularity is
///         not detected, and symlink/subst aliases of one file are cached as distinct entries.
///     </para>
/// </summary>
public sealed class ArchiveHandleRegistry
{
    // Windows paths are case-insensitive; the CLI TFM also runs on Linux where distinct paths can
    // differ only by case, so the comparer is OS-conditional.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, Entry> _entries;

    // _gate guards the map + keep-warm counter. Lock order: an entry's Lock may be held when
    // taking _gate, never the reverse — Acquire looks the entry up under _gate, releases it,
    // then locks the entry.
    private readonly object _gate = new();
    private int _keepWarmScopes;

    public ArchiveHandleRegistry()
    {
        _entries = new Dictionary<string, Entry>(PathComparer);
    }

    /// <summary>The app-wide instance. Tests should construct their own registry.</summary>
    public static ArchiveHandleRegistry Shared { get; } = new();

    /// <summary>Number of archives currently held open (leased or parked). Diagnostics/tests.</summary>
    public int OpenHandleCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    ///     Opens (or joins) the shared handle for <paramref name="archivePath" />. Open failures
    ///     propagate the underlying exception and are never cached — the next acquire retries.
    /// </summary>
    public ArchiveLease Acquire(string archivePath)
    {
        var key = Path.GetFullPath(archivePath);
        while (true)
        {
            Entry entry;
            var creator = false;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new Entry(key);
                    creator = true;
                    // Take the entry lock BEFORE publication. This cannot block (the entry is
                    // still unpublished, so no other thread can hold it) and it closes the
                    // window where a concurrent acquirer could win the lock first and observe
                    // an entry with no reader and unset identity. Non-creators below block on
                    // this lock until the open completes — the single-flight guarantee.
                    Monitor.Enter(entry.Lock);
                    _entries[key] = entry;
                }
            }

            if (creator)
            {
                try
                {
                    OpenEntry(entry);
                    entry.RefCount = 1;
                    return new ArchiveLease(this, entry);
                }
                catch
                {
                    entry.Detached = true;
                    RemoveIfCurrent(key, entry);
                    throw;
                }
                finally
                {
                    Monitor.Exit(entry.Lock);
                }
            }

            lock (entry.Lock)
            {
                if (entry.Detached)
                {
                    // Evicted, replaced, or failed-open between the map lookup and taking the
                    // entry lock.
                    continue;
                }

                if (IsStale(entry))
                {
                    // Detach: current leaseholders keep the old reader alive until their last
                    // release; a parked (refcount 0) stale handle has no holders and dies here.
                    entry.Detached = true;
                    RemoveIfCurrent(key, entry);
                    if (entry.RefCount == 0)
                    {
                        entry.Reader!.Dispose();
                    }

                    continue;
                }

                entry.RefCount++;
                return new ArchiveLease(this, entry);
            }
        }
    }

    /// <summary>
    ///     Opens a keep-warm scope: while at least one scope is active, handles whose last lease
    ///     releases are parked (kept open) instead of disposed, so multi-phase flows over the same
    ///     archives (e.g. <c>dmp to-esm</c> rename→pack, back-to-back plugin loads) reuse the
    ///     parse. Disposing the last scope sweeps every parked handle.
    /// </summary>
    public IDisposable KeepWarm()
    {
        lock (_gate)
        {
            _keepWarmScopes++;
        }

        return new KeepWarmScope(this);
    }

    internal void Release(Entry entry)
    {
        ArchiveReader? toDispose = null;
        lock (entry.Lock)
        {
            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                if (entry.Detached)
                {
                    toDispose = entry.Reader;
                }
                else
                {
                    lock (_gate)
                    {
                        if (_keepWarmScopes == 0)
                        {
                            entry.Detached = true;
                            RemoveIfCurrentUnderGate(entry.Key, entry);
                            toDispose = entry.Reader;
                        }

                        // else: parked — stays in the map at refcount 0 until revived or swept.
                    }
                }
            }
        }

        toDispose?.Dispose();
    }

    private void ReleaseKeepWarm()
    {
        List<Entry> candidates;
        lock (_gate)
        {
            if (--_keepWarmScopes > 0)
            {
                return;
            }

            candidates = [.. _entries.Values];
        }

        foreach (var entry in candidates)
        {
            ArchiveReader? toDispose = null;
            lock (entry.Lock)
            {
                if (!entry.Detached && entry.RefCount == 0)
                {
                    lock (_gate)
                    {
                        // Re-check: a new keep-warm scope may have opened since the sweep began.
                        if (_keepWarmScopes == 0)
                        {
                            entry.Detached = true;
                            RemoveIfCurrentUnderGate(entry.Key, entry);
                            toDispose = entry.Reader;
                        }
                    }
                }
            }

            toDispose?.Dispose();
        }
    }

    private static void OpenEntry(Entry entry)
    {
        // Identity is captured before the open: if the file is rewritten mid-open, the next
        // acquire's staleness stat sees the mismatch and replaces the handle.
        var info = new FileInfo(entry.Key);
        entry.Length = info.Length;
        entry.LastWriteTimeUtc = info.LastWriteTimeUtc;
        entry.Reader = ArchiveReader.Open(entry.Key);
        // Shared handles are visible to every lease holder, so per-instance mutable state (the
        // legacy BSA conversion toggles) must be locked out. The backend default is a no-op —
        // every non-BSA backend is immutable after open and must stay that way.
        entry.Reader.Backend.MarkShared();
    }

    private static bool IsStale(Entry entry)
    {
        var info = new FileInfo(entry.Key);
        return !info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc != entry.LastWriteTimeUtc;
    }

    private void RemoveIfCurrent(string key, Entry entry)
    {
        lock (_gate)
        {
            RemoveIfCurrentUnderGate(key, entry);
        }
    }

    private void RemoveIfCurrentUnderGate(string key, Entry entry)
    {
        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(key);
        }
    }

    internal sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public object Lock { get; } = new();
        public ArchiveReader? Reader { get; set; }
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public int RefCount { get; set; }

        /// <summary>Removed from the map (stale, evicted, or failed); dispose at refcount zero.</summary>
        public bool Detached { get; set; }
    }

    private sealed class KeepWarmScope(ArchiveHandleRegistry registry) : IDisposable
    {
        private ArchiveHandleRegistry? _registry = registry;

        public void Dispose()
        {
            Interlocked.Exchange(ref _registry, null)?.ReleaseKeepWarm();
        }
    }
}

/// <summary>
///     A refcount hold on a shared archive handle from <see cref="ArchiveHandleRegistry" />.
///     Dispose releases the hold (idempotent); the underlying reader is disposed when the last
///     lease of its handle releases. Treat <see cref="Reader" /> as read-only shared state —
///     never dispose it directly or enable conversion toggles on it.
/// </summary>
public sealed class ArchiveLease : IDisposable
{
    private readonly ArchiveHandleRegistry _registry;
    private ArchiveHandleRegistry.Entry? _entry;

    internal ArchiveLease(ArchiveHandleRegistry registry, ArchiveHandleRegistry.Entry entry)
    {
        _registry = registry;
        _entry = entry;
        Reader = entry.Reader!;
        ArchivePath = entry.Key;
        Length = entry.Length;
        LastWriteTimeUtc = entry.LastWriteTimeUtc;
    }

    /// <summary>The shared reader. Read-only: extraction is thread-safe; mutation is forbidden.</summary>
    public ArchiveReader Reader { get; }

    /// <summary>Full path of the archive on disk (registry key).</summary>
    public string ArchivePath { get; }

    /// <summary>On-disk length captured at open — cache-identity input (path|length|mtime).</summary>
    public long Length { get; }

    /// <summary>Last write time (UTC) captured at open — cache-identity input.</summary>
    public DateTime LastWriteTimeUtc { get; }

    public void Dispose()
    {
        var entry = Interlocked.Exchange(ref _entry, null);
        if (entry is not null)
        {
            _registry.Release(entry);
        }
    }
}
