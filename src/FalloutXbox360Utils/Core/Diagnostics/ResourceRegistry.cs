namespace FalloutXbox360Utils.Core.Diagnostics;

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
    private static ResourceRegistry? _instance;
    private static readonly Lock InstanceLock = new();

    /// <summary>Cap on retained final-stats records of disposed resources (FIFO beyond this).</summary>
    private const int MaxRetiredRecords = 256;

    private readonly Lock _gate = new();
    private readonly List<ResourceRegistration> _registrations = [];
    private readonly Queue<ResourceSnapshotRecord> _retired = new();

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
        var displayName = string.IsNullOrEmpty(instanceTag)
            ? resource.ResourceName
            : $"{resource.ResourceName}[{instanceTag}]";
        var registration = new ResourceRegistration(this, resource, displayName);
        lock (_gate)
        {
            _registrations.Add(registration);
        }

        return registration;
    }

    /// <summary>
    ///     Immutable point-in-time snapshot for diagnostics. The registration list is copied under
    ///     the lock; <see cref="ITrackableResource.GetStats" /> runs outside it. A stats source that
    ///     throws yields a row whose <see cref="ResourceStats.LastError" /> carries the message
    ///     instead of poisoning the whole snapshot.
    /// </summary>
    public IReadOnlyList<ResourceSnapshotRecord> GetSnapshot()
    {
        ResourceRegistration[] registrations;
        lock (_gate)
        {
            registrations = [.. _registrations];
        }

        var rows = new List<ResourceSnapshotRecord>(registrations.Length);
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

            rows.Add(new ResourceSnapshotRecord(registration.DisplayName, registration.Resource.Category, stats));
        }

        return rows;
    }

    /// <summary>Sum of <see cref="ResourceStats.EstimatedBytes" /> across resources in <paramref name="category" />.</summary>
    public long TotalTrackedBytes(ResourceCategory category)
    {
        long total = 0;
        foreach (var row in GetSnapshot())
        {
            if (row.Category == category)
            {
                total += row.Stats.EstimatedBytes;
            }
        }

        return total;
    }

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

            _retired.Enqueue(new ResourceSnapshotRecord(
                registration.DisplayName, registration.Resource.Category, finalStats));
            while (_retired.Count > MaxRetiredRecords)
            {
                _retired.Dequeue();
            }
        }
    }
}
