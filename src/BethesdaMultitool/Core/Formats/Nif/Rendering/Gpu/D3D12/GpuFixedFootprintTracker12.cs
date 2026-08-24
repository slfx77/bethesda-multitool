using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     One registry row for the fixed, non-trimmable GPU allocations that previously appeared in no
///     accounting at all: the upload ring (128–512 MB), the shadow cascades (16–256 MB), swap-chain
///     and offscreen scene targets, water noise tiles, the terrain shared index buffer. Individually
///     each is "just a field in some renderer"; together they were multiple GB missing from every
///     VRAM figure, which matters because the residency governor steers on the difference between
///     the DXGI budget and what we can account for.
///     <para>
///         One tracker instance per <see cref="GpuMemorySegment" /> — a registry row carries a
///         single segment, and the whole point of the segment split is that VRAM and upload-heap
///         system RAM must never sum into one number. Allocation sites <see cref="Add" /> a named
///         entry and dispose the returned handle when the resource is released (resize recreates
///         are simply dispose-old + add-new).
///     </para>
///     <para>
///         Thread-safe: allocations happen on the render thread but resize/teardown can interleave,
///         and <see cref="GetStats" /> is called from arbitrary threads. Mutations lock; the stats
///         read is a volatile total per the <see cref="ITrackableResource" /> contract.
///     </para>
/// </summary>
internal sealed class GpuFixedFootprintTracker12 : ITrackableResource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, (string Name, long Bytes)> _entries = new();
    private readonly GpuMemorySegment _segment;
    private long _nextId;
    private long _totalBytes;

    internal GpuFixedFootprintTracker12(GpuMemorySegment segment)
    {
        _segment = segment;
    }

    /// <summary>
    ///     Device-local (VRAM) fixed allocations: render targets, shadow cascades, DEFAULT-heap
    ///     buffers. Registered lazily — the type is only touched by GPU code paths, so CLI commands
    ///     that never render keep their <c>--resource-stats</c> output free of empty GPU rows.
    /// </summary>
    public static GpuFixedFootprintTracker12 LocalInstance { get; } =
        new GpuFixedFootprintTracker12(GpuMemorySegment.Local)
            .RegisterWith(ResourceRegistry.Instance, "local");

    /// <summary>Host-visible (UPLOAD-heap) fixed allocations — the per-frame ring above all.</summary>
    public static GpuFixedFootprintTracker12 NonLocalInstance { get; } =
        new GpuFixedFootprintTracker12(GpuMemorySegment.NonLocal)
            .RegisterWith(ResourceRegistry.Instance, "nonlocal");

    public string ResourceName => "GpuFixedFootprint";

    public ResourceCategory Category => ResourceCategory.GpuResident;

    public ResourceStats GetStats()
    {
        // Under the lock so bytes and entry count are a COHERENT pair. Both reads are individually
        // safe without it, but a snapshot taken mid-Add/mid-Remove could show a byte total that
        // belongs to a different entry set — and this row exists precisely so a VRAM figure can be
        // trusted. The critical section is a field read and a Count; the 1 Hz diagnostics tick and
        // the render thread cannot contend meaningfully on it.
        lock (_gate)
        {
            return new ResourceStats
            {
                EstimatedBytes = _totalBytes,
                EntryCount = _entries.Count,
                Segment = _segment
            };
        }
    }

    /// <summary>
    ///     Records a fixed allocation. Dispose the returned handle when the resource is released;
    ///     double-dispose is a no-op. Non-positive sizes register a zero-byte named entry rather
    ///     than throwing — a degenerate target (0-width window) is a caller state, not an error.
    /// </summary>
    public IDisposable Add(string name, long bytes)
    {
        var clamped = Math.Max(0, bytes);
        long id;
        lock (_gate)
        {
            id = _nextId++;
            _entries[id] = (name, clamped);
            _totalBytes += clamped;
        }

        return new Handle(this, id);
    }

    internal GpuFixedFootprintTracker12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        registry.Register(this, instanceTag);
        return this;
    }

    private void Remove(long id)
    {
        lock (_gate)
        {
            if (_entries.Remove(id, out var entry))
            {
                _totalBytes -= entry.Bytes;
            }
        }
    }

    private sealed class Handle(GpuFixedFootprintTracker12 owner, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(id);
            }
        }
    }
}
