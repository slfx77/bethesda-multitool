using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Resources;
using Slfx77.Multitool.Core.Lifetime;
using CellEntry = Slfx77.Multitool.Core.Lifetime.ResourceResidencyEntry<(int gx, int gy), BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTerrainCellResources12>;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Combines terrain's existing LRU policy with shared ownership of prepared, resident and retiring ranges.</summary>
/// <typeparam name="TMesh">Borrowed draw metadata whose range belongs to the exact residency entry.</typeparam>
/// <remarks>One cache instance is one world generation. Logical cell bytes include failed and retiring
/// entries until release succeeds; the arena separately reports physical committed backing.</remarks>
internal sealed class GpuTerrainCellCache12<TMesh> : ITrackableResource, IDisposable where TMesh : class
{
    private readonly ResourceResidencyCache<(int gx, int gy), GpuTerrainCellResources12> _residency;
    private readonly LruCache<(int gx, int gy), (CellEntry Entry, TMesh Mesh)> _recency;
    private readonly Dictionary<(int gx, int gy), CellEntry> _current = new();
    private readonly RetiredResourceDisposal _transfers = new();
    private readonly Action<TerrainAllocation12> _freeGeometry;
    private readonly GpuTerrainCellCacheLifetime12 _lifetime;
    private readonly Action _invalidated;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private ResourceRegistration? _registration;
    private long _chargedBytes;
    private int _entryCount;
    private bool _stopped;

    /// <summary>Creates a bounded logical residency domain without allocating GPU resources.</summary>
    /// <param name="capacity">Existing terrain entry backstop, including pending and retiring entries.</param>
    /// <param name="byteBudget">Existing byte backstop; zero preserves the unknown-VRAM policy.</param>
    /// <param name="freeGeometry">Returns an exact range only after caller-proven GPU retirement.</param>
    /// <param name="lifetime">Existing recorder and queue ownership operations.</param>
    /// <param name="invalidated">Updates terrain content identity when draw metadata becomes unavailable.</param>
    internal GpuTerrainCellCache12(int capacity, long byteBudget, Action<TerrainAllocation12> freeGeometry,
        GpuTerrainCellCacheLifetime12 lifetime, Action invalidated)
    {
        ArgumentNullException.ThrowIfNull(freeGeometry);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(invalidated);
        ArgumentOutOfRangeException.ThrowIfNegative(byteBudget);
        _freeGeometry = freeGeometry;
        _lifetime = lifetime;
        _invalidated = invalidated;
        _residency = new(byteBudget == 0 ? long.MaxValue : byteBudget, capacity);
        _recency = new("CellMeshLru", ResourceCategory.GpuAttributed, maxEntries: capacity,
            maxBytes: byteBudget == 0 ? null : byteBudget,
            sizeOf: static (_, item) => item.Entry.AllocationBytes,
            onEvicted: (_, item) => Retire(item.Entry));
    }

    /// <summary>Gets logical bytes still owned, including pending and failed retirement.</summary>
    internal long EstimatedBytes => Volatile.Read(ref _chargedBytes);

    /// <summary>Gets drawable metadata bytes for the distance-based eviction policy.</summary>
    internal long ResidentBytes { get { VerifyAccess(); return _recency.EstimatedBytes; } }

    /// <summary>Gets current draw metadata count; retired charges may outlive these entries.</summary>
    internal int Count => _recency.Count;

    /// <summary>Gets the existing terrain registry identity.</summary>
    public string ResourceName => "CellMeshLru";

    /// <summary>Attributes suballocated bytes; native backing is counted once by the arena.</summary>
    public ResourceCategory Category => ResourceCategory.GpuAttributed;

    /// <summary>Publishes lock-free ownership counters without crossing the shared ledger's thread boundary.</summary>
    public ResourceStats GetStats() => new()
    {
        EstimatedBytes = Volatile.Read(ref _chargedBytes),
        EntryCount = Volatile.Read(ref _entryCount),
        Segment = GpuMemorySegment.Local
    };

    /// <summary>Registers logical ownership separately from committed arena backing.</summary>
    /// <param name="registry">Existing application registry.</param>
    internal GpuTerrainCellCache12<TMesh> RegisterWith(ResourceRegistry registry)
    {
        VerifyOpen();
        _registration?.Dispose();
        _registration = registry.Register(this, "terrain-cells");
        return this;
    }

    /// <summary>Admits a complete cell charge and registers real submission and retirement before upload.</summary>
    /// <param name="key">Grid identity within this exact world-generation cache.</param>
    /// <param name="bytes">Checked aligned allocation charge shared with the terrain arena.</param>
    /// <param name="entry">Empty retained candidate on success; null while prior charges prevent admission.</param>
    /// <returns>Whether the existing budget can admit the cell after LRU pressure handling.</returns>
    internal bool TryReserve((int gx, int gy) key, long bytes, [NotNullWhen(true)] out CellEntry? entry)
    {
        VerifyOpen();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        entry = null;
        _transfers.Dispose();
        if (_current.ContainsKey(key)) throw new InvalidOperationException("Terrain cell already has a current owner.");
        if (bytes > _residency.MaximumBytes) return false;
        // Plan only the additional eviction needed after pending retirements finish. Admission
        // below still includes every pending charge, so eviction never creates early headroom.
        var pendingEntries = _current.Count - _recency.Count;
        _recency.TrimToCount(Math.Max(0, _residency.MaximumEntries - pendingEntries - 1));
        var pendingBytes = Math.Max(0, _residency.TotalBytes - _residency.RetiringBytes - _recency.EstimatedBytes);
        _recency.TrimToBytes(Math.Max(0, _residency.MaximumBytes - bytes - pendingBytes));
        if (_residency.EntryCount >= _residency.MaximumEntries || bytes > _residency.MaximumBytes - _residency.TotalBytes)
            return false;
        var candidate = _residency.Reserve(key, bytes);
        try
        {
            _current.Add(key, candidate);
            candidate.Attach(new GpuTerrainCellResources12(_freeGeometry));
            var preparation = new GpuTerrainCellPreparation12(candidate, UpdateStats);
            _lifetime.RetireAfterFrame(preparation);
            _lifetime.Enlist(preparation);
            entry = candidate;
            return true;
        }
        catch
        {
            // No upload can begin until this method returns; the empty owner has no GPU users.
            _current.Remove(key);
            candidate.ReleaseAfterRetirement();
            throw;
        }
        finally { UpdateStats(); }
    }

    /// <summary>Makes completed metadata available for same-list draws; actual submission decides residency.</summary>
    /// <param name="entry">Exact candidate returned by TryReserve.</param>
    /// <param name="mesh">Fully initialized borrowed draw metadata.</param>
    internal void Complete(CellEntry entry, TMesh mesh)
    {
        VerifyOpen();
        ArgumentNullException.ThrowIfNull(mesh);
        VerifyCurrent(entry);
        if (entry.Resource.Geometry.Allocation.AlignedSize != entry.AllocationBytes)
            throw new InvalidOperationException("Terrain allocation differs from its admitted charge.");
        entry.MarkPrepared();
        _recency.Set(entry.Key, (entry, mesh));
        UpdateStats();
    }

    /// <summary>Invalidates a failed candidate while its pre-registered frame owner retains cleanup.</summary>
    /// <param name="entry">Exact failed candidate; another generation cannot be invalidated by this call.</param>
    internal void Abort(CellEntry entry)
    {
        VerifyAccess();
        VerifyCurrent(entry);
        _residency.Remove(entry.Key);
        _current.Remove(entry.Key);
        _recency.Remove(entry.Key);
        UpdateStats();
    }

    /// <summary>Fetches usable metadata and updates the existing LRU order.</summary>
    internal bool TryGet((int gx, int gy) key, [NotNullWhen(true)] out TMesh? mesh) => Lookup(key, true, out mesh);

    /// <summary>Fetches usable metadata without affecting recency, including shadow and mirror passes.</summary>
    internal bool TryPeek((int gx, int gy) key, [NotNullWhen(true)] out TMesh? mesh) => Lookup(key, false, out mesh);

    /// <summary>Checks visibility without changing recency; abandoned candidates are never drawable.</summary>
    internal bool ContainsKey((int gx, int gy) key) => TryPeek(key, out _);

    /// <summary>Snapshots keys for the renderer's existing distance-ordered eviction policy.</summary>
    internal (int gx, int gy)[] SnapshotKeys() { VerifyOpen(); return _recency.SnapshotKeys(); }

    /// <summary>Evicts metadata while retaining its range until the queue accepts and completes retirement.</summary>
    internal long Evict((int gx, int gy) key) { VerifyOpen(); return _recency.Evict(key); }

    /// <summary>Stops admission and transfers all remaining entries through the existing fence-delayed queue.</summary>
    /// <remarks>Failure keeps this cache retryable. Successful queue transfer keeps the ledger alive through its entries.</remarks>
    public void Dispose()
    {
        VerifyAccess();
        _stopped = true;
        foreach (var entry in _current.Values.ToArray()) Retire(entry);
        _recency.Dispose();
        _transfers.Dispose();
        _registration?.Dispose();
        _registration = null;
    }

    /// <summary>Releases all logical ownership only after the renderer's caller proves complete GPU retirement.</summary>
    /// <remarks>This is for final renderer teardown, not changing worlds during ordinary rendering.</remarks>
    internal void ReleaseAfterRetirement()
    {
        Dispose();
        try { _residency.Dispose(); }
        finally { UpdateStats(); }
    }

    /// <summary>Retains a queue-transfer action before invalidating an exact current entry.</summary>
    private void Retire(CellEntry entry)
    {
        if (!_current.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry)) return;
        var release = new GpuTerrainCellRetirement12(entry, UpdateStats);
        _transfers.Add(() => _lifetime.RetireAfterDelay(release), "terrain cell retirement transfer");
        _residency.Remove(entry.Key);
        _current.Remove(entry.Key);
        _recency.Remove(entry.Key);
        UpdateStats();
        _invalidated();
        _transfers.Dispose();
    }

    /// <summary>Rejects abandoned/uncertain candidates before exposing their GPU addresses to another draw.</summary>
    private bool Lookup((int gx, int gy) key, bool touch, [NotNullWhen(true)] out TMesh? mesh)
    {
        VerifyOpen();
        var found = touch ? _recency.TryGet(key, out var item) : _recency.TryPeek(key, out item);
        if (found && item.Entry.State is ResourceResidencyState.Prepared or ResourceResidencyState.Resident)
        {
            mesh = item.Mesh;
            return true;
        }
        if (found) Retire(item.Entry);
        mesh = null;
        return false;
    }

    /// <summary>Checks complete ownership identity before changing a cell with a reused grid key.</summary>
    private void VerifyCurrent(CellEntry entry)
    {
        if (!_current.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
            throw new InvalidOperationException("Terrain candidate is no longer current in this cache.");
    }

    /// <summary>Copies creating-thread ownership counters for asynchronous diagnostics.</summary>
    private void UpdateStats()
    {
        Volatile.Write(ref _chargedBytes, _residency.TotalBytes);
        Volatile.Write(ref _entryCount, _residency.EntryCount);
    }

    /// <summary>Rejects mutation after closure before touching cache or native ownership.</summary>
    private void VerifyOpen() { VerifyAccess(); ObjectDisposedException.ThrowIf(_stopped, this); }

    /// <summary>Confines ownership transitions to the rendering thread.</summary>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Terrain cell ownership belongs to its creating thread.");
    }
}
