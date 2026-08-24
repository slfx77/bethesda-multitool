namespace BethesdaMultitool.Core.Diagnostics;

/// <summary>
///     Central registry of every tracked cache, buffer, queue, and session scope in the process.
///     Diagnostics surfaces (the GUI Diagnostics tab, the CLI <c>--resource-stats</c> reporter) pull
///     immutable snapshots from here; the <see cref="MemoryBudgetCoordinator" /> walks it to find
///     <see cref="IMemoryPressureParticipant" />s.
///     <para>
///         Registration returns a handle that MUST be disposed when the resource is disposed —
///         unregistration is explicit, not weak-reference based, so a registration that outlives its
///         resource's intended lifetime shows up in the diagnostics panel as a leak report.
///         Process-lifetime resources (disk caches, static palettes) simply never dispose their
///         handle; that is correct, not a leak.
///     </para>
///     <para>
///         The registry is touched only at construction, disposal, and snapshot time — never on a
///         cache hot path. Registration order is preserved in snapshots.
///     </para>
/// </summary>
internal sealed class ResourceRegistry
{
    /// <summary>Cap on retained final-stats records of disposed resources (FIFO beyond this).</summary>
    private const int MaxRetiredRecords = 256;

    private static ResourceRegistry? _instance;
    private static readonly Lock InstanceLock = new();

    private readonly Lock _gate = new();
    private readonly List<ResourceRegistration> _registrations = [];
    private readonly List<ResourceSnapshotRecord> _retired = [];

    /// <summary>Process-wide registry. Tests construct their own instances instead.</summary>
    public static ResourceRegistry Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (InstanceLock)
                {
                    _instance ??= new ResourceRegistry();
                }
            }

            return _instance;
        }
    }

    /// <summary>
    ///     Registers a resource and returns its lifetime handle. <paramref name="instanceTag" />
    ///     disambiguates multiple instances of the same resource type (e.g. the "terrain" vs
    ///     "reference" texture caches); the display name becomes <c>Name[tag]</c>.
    /// </summary>
    public ResourceRegistration Register(ITrackableResource resource, string? instanceTag = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var baseName = string.IsNullOrEmpty(instanceTag)
            ? resource.ResourceName
            : $"{resource.ResourceName}[{instanceTag}]";
        lock (_gate)
        {
            var registration = new ResourceRegistration(this, resource, MakeUniqueName(baseName));
            _registrations.Add(registration);
            return registration;
        }
    }

    /// <summary>
    ///     Appends <c>#2</c>, <c>#3</c>, … when <paramref name="baseName" /> is already live, so
    ///     concurrently-registered instances of one type stay distinguishable.
    ///     <para>
    ///         Not cosmetic: diagnostics surfaces key their rows by display name, so duplicates used
    ///         to COLLAPSE into a single row showing whichever instance happened to be enumerated
    ///         last. Several types register untagged from many construction sites (the NIF texture
    ///         resolvers at ~16 and ~2 sites, both decoded-blob disk caches), and each was silently
    ///         hiding every instance but one. Tagging those call sites is the real fix; this makes a
    ///         future missed tag degrade to an ugly name rather than a lost row.
    ///     </para>
    ///     Caller must hold <see cref="_gate" />.
    /// </summary>
    private string MakeUniqueName(string baseName)
    {
        if (!IsNameLive(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{baseName}#{suffix}";
            if (!IsNameLive(candidate))
            {
                return candidate;
            }
        }

        return baseName;
    }

    private bool IsNameLive(string name)
    {
        foreach (var existing in _registrations)
        {
            if (string.Equals(existing.DisplayName, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Immutable point-in-time snapshot for diagnostics. The registration list is copied under
    ///     the lock; <see cref="ITrackableResource.GetStats" /> runs outside it. A stats source that
    ///     throws yields a row whose <see cref="ResourceStats.LastError" /> carries the message
    ///     instead of poisoning the whole snapshot.
    /// </summary>
    public IReadOnlyList<ResourceSnapshotRecord> GetSnapshot() => Snapshot().Rows;

    /// <summary>
    ///     One walk of the registry producing both the rows and the per-category byte totals.
    ///     <para>
    ///         Every consumer needs both, and each used to take them separately: the Diagnostics tab
    ///         called <c>GetSnapshot</c> then <c>TotalTrackedBytes</c> (which snapshotted again) at 1 Hz
    ///         — two full walks per second — and the CLI reporter took three. Each walk calls every
    ///         <see cref="ITrackableResource.GetStats" /> in the process, so a resource whose stats are
    ///         expensive paid that cost N times per tick.
    ///     </para>
    /// </summary>
    public RegistrySnapshot Snapshot()
    {
        ResourceRegistration[] registrations;
        lock (_gate)
        {
            registrations = [.. _registrations];
        }

        var rows = new List<ResourceSnapshotRecord>(registrations.Length);
        var totals = new long[RegistrySnapshot.CategoryCount];
        foreach (var registration in registrations)
        {
            ResourceStats stats;
            try
            {
                stats = registration.Resource.GetStats();
            }
            catch (Exception ex)
            {
                stats = new ResourceStats { LastError = $"GetStats threw: {ex.Message}" };
            }

            var category = registration.Resource.Category;
            rows.Add(new ResourceSnapshotRecord(registration.DisplayName, category, stats));
            totals[(int)category] += stats.EstimatedBytes;
        }

        return new RegistrySnapshot(rows, totals);
    }

    /// <summary>Sum of <see cref="ResourceStats.EstimatedBytes" /> across resources in <paramref name="category" />.</summary>
    public long TotalTrackedBytes(ResourceCategory category) => Snapshot().TotalBytes(category);

    /// <summary>Logs a one-line-per-resource snapshot via <see cref="Logger.Instance" />.</summary>
    public void LogSnapshot()
    {
        var snapshot = GetSnapshot();
        if (snapshot.Count == 0)
        {
            return;
        }

        var log = Logger.Instance;
        log.Info("Resource snapshot ({0} resources):", snapshot.Count);
        foreach (var row in snapshot)
        {
            var s = row.Stats;
            log.Info(
                "  {0} [{1}]: {2:N0} bytes, {3:N0} entries, {4:N0}/{5:N0} hit/miss, {6:N0} evictions, depth {7}, in-flight {8}, processed {9:N0}",
                row.DisplayName, row.Category, s.EstimatedBytes, s.EntryCount,
                s.Hits, s.Misses, s.Evictions, s.QueueDepth, s.InFlight, s.Processed);
        }
    }

    /// <summary>
    ///     Final stats of resources that have unregistered, newest last (capped). End-of-run
    ///     reporting uses this so a CLI command's session-scoped caches still show what they did —
    ///     a well-behaved cache disposes (and unregisters) before the process-exit report runs.
    ///     Repeated lifetimes under one display name (hot-loop <c>ParallelWork</c> runs, per-session
    ///     caches) aggregate into a single row instead of flooding the cap.
    /// </summary>
    public IReadOnlyList<ResourceSnapshotRecord> GetRetiredSnapshot()
    {
        lock (_gate)
        {
            return [.. _retired];
        }
    }

    /// <summary>Registrations in registration order. Used by the <see cref="MemoryBudgetCoordinator" />.</summary>
    internal IReadOnlyList<ResourceRegistration> GetRegistrations()
    {
        lock (_gate)
        {
            return [.. _registrations];
        }
    }

    internal void Unregister(ResourceRegistration registration)
    {
        ResourceStats finalStats;
        try
        {
            finalStats = registration.Resource.GetStats();
        }
        catch (Exception ex)
        {
            finalStats = new ResourceStats { LastError = $"GetStats threw at dispose: {ex.Message}" };
        }

        lock (_gate)
        {
            if (!_registrations.Remove(registration))
            {
                return;
            }

            var index = _retired.FindIndex(r =>
                r.Category == registration.Resource.Category &&
                string.Equals(r.DisplayName, registration.DisplayName, StringComparison.Ordinal));
            var record = index >= 0
                ? MergeRetired(_retired[index], finalStats)
                : new ResourceSnapshotRecord(
                    registration.DisplayName, registration.Resource.Category, finalStats);
            if (index >= 0)
            {
                _retired.RemoveAt(index);
            }

            _retired.Add(record);
            while (_retired.Count > MaxRetiredRecords)
            {
                _retired.RemoveAt(0);
            }
        }
    }

    /// <summary>
    ///     Folds another lifetime's final stats into an existing retired row: throughput counters
    ///     accumulate, point-in-time gauges (bytes, entries, depth) take the newest lifetime's value.
    /// </summary>
    private static ResourceSnapshotRecord MergeRetired(ResourceSnapshotRecord existing, ResourceStats latest)
    {
        var merged = latest with
        {
            Hits = existing.Stats.Hits + latest.Hits,
            Misses = existing.Stats.Misses + latest.Misses,
            Evictions = existing.Stats.Evictions + latest.Evictions,
            Processed = existing.Stats.Processed + latest.Processed,
            Failures = existing.Stats.Failures + latest.Failures,
            LastError = latest.LastError ?? existing.Stats.LastError
        };
        return existing with { Stats = merged, RunCount = existing.RunCount + 1 };
    }
}
