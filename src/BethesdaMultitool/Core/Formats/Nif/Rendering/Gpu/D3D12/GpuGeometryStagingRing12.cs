using BethesdaMultitool.Core.Diagnostics;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Bounded, persistently-mapped UPLOAD-heap staging for the geometry arena's optional DEFAULT-heap
///     backing mode. Regions are reclaimed in copy-submission order through
///     <see cref="GpuDeletionQueue12" />, using the same <see cref="StagingRingAllocator" /> invariant
///     as terrain uploads.
///     <para>
///         The ring is an optimisation, not an admission limit. If a burst fills it, or one mesh is
///         larger than the cap, <see cref="GpuGeometryArena12" /> uses a transient staging resource
///         for that upload. The permanent staging footprint therefore remains bounded while an
///         unusual mesh still makes progress.
///     </para>
/// </summary>
internal sealed unsafe class GpuGeometryStagingRing12 : ITrackableResource, IDisposable
{
    /// <summary>
    ///     Floor for ordinary reference streaming. This holds four generations of the default
    ///     4-MiB/frame byte budget without paying the full ceiling on every scene.
    /// </summary>
    public const long MinCapacityBytes = 16L * 1024 * 1024;

    /// <summary>
    ///     Hard cap on permanently committed geometry staging. Larger or temporarily overlapping
    ///     uploads use the arena's transient overflow path rather than growing resident system RAM.
    /// </summary>
    public const long MaxCapacityBytes = 64L * 1024 * 1024;

    /// <summary>
    ///     The deletion queue holds submitted regions for the recorder's in-flight frames; include
    ///     the frame currently being recorded so normal steady-state traffic can recycle safely.
    /// </summary>
    public const int GenerationsHeld = GpuCommandRecorder12.FramesInFlight + 1;

    private readonly GpuDevice12 _gpu;
    private StagingRingAllocator? _allocator;
    private ID3D12Resource? _buffer;
    private byte* _cpu;
    private bool _disposed;
    private IDisposable? _footprint;
    private ResourceRegistration? _registration;

    public GpuGeometryStagingRing12(GpuDevice12 gpu)
    {
        _gpu = gpu;
    }

    /// <summary>Committed ring bytes, or 0 until the first DEFAULT-heap upload creates it.</summary>
    public long CapacityBytes => _allocator?.Capacity ?? 0;

    /// <summary>Bytes in regions whose recorded copies may still be reading them.</summary>
    public long LiveBytes => _allocator?.LiveBytes ?? 0;

    public long ServedCount { get; private set; }

    public long OverflowCount { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration?.Dispose();
        _registration = null;
        _footprint?.Dispose();
        _footprint = null;
        if (_buffer is not null)
        {
            _buffer.Unmap(0);
            _buffer.Dispose();
            _buffer = null;
        }

        _cpu = null;
        _allocator = null;
    }

    public string ResourceName => nameof(GpuGeometryStagingRing12);

    /// <summary>
    ///     The fixed tracker owns the ring's committed-byte accounting; this row reports live staging
    ///     and overflow behaviour without double-counting the same UPLOAD resource.
    /// </summary>
    public ResourceCategory Category => ResourceCategory.GpuAttributed;

    public ResourceStats GetStats()
    {
        var allocator = _allocator;
        return new ResourceStats
        {
            EstimatedBytes = allocator?.LiveBytes ?? 0,
            EntryCount = allocator?.OutstandingCount ?? 0,
            Hits = ServedCount,
            Misses = OverflowCount,
            Segment = GpuMemorySegment.NonLocal
        };
    }

    public GpuGeometryStagingRing12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    /// <summary>
    ///     Plans a first-use ring from the first mesh seen. Three generations keep the first request's
    ///     copy traffic recyclable; the floor covers many small meshes and the ceiling prevents one
    ///     pathological mesh from permanently reserving an equally pathological staging buffer.
    /// </summary>
    public static long PlanCapacityBytes(long firstRequestBytes)
    {
        if (firstRequestBytes <= 0)
        {
            return 0;
        }

        var perGeneration = Math.Min(firstRequestBytes, MaxCapacityBytes);
        var wanted = perGeneration > MaxCapacityBytes / GenerationsHeld
            ? MaxCapacityBytes
            : perGeneration * GenerationsHeld;
        return Math.Clamp(wanted, MinCapacityBytes, MaxCapacityBytes);
    }

    /// <summary>
    ///     Reserves mapped staging. After recording the copy, the caller must enqueue
    ///     <paramref name="region" />.<see cref="StagedRegion.Release" /> on the same FIFO deletion
    ///     queue used by the frame submission. A false result is routine and selects transient staging.
    /// </summary>
    public bool TryReserve(long bytes, out StagedRegion region)
    {
        region = default;
        if (_disposed || bytes <= 0)
        {
            return false;
        }

        if (!EnsureCreated(bytes) || !_allocator!.TryAllocate(bytes, out var offset, out _, out var sequence))
        {
            OverflowCount++;
            return false;
        }

        ServedCount++;
        region = new StagedRegion(
            _buffer!,
            (ulong)offset,
            (IntPtr)(_cpu + offset),
            new ReleaseHandle(this, sequence));
        return true;
    }

    /// <summary>
    ///     Lazily commits the bounded ring. Commit failure is recoverable: this upload falls back to
    ///     transient staging and a later upload retries after memory pressure changes.
    /// </summary>
    private bool EnsureCreated(long firstRequestBytes)
    {
        if (_allocator is not null)
        {
            return true;
        }

        var capacity = PlanCapacityBytes(firstRequestBytes);
        if (capacity <= 0)
        {
            return false;
        }

        ID3D12Resource? buffer = null;
        try
        {
            buffer = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)capacity),
                ResourceStates.GenericRead);

            void* mapped = null;
            buffer.Map(0, &mapped).CheckError();
            _cpu = (byte*)mapped;
            _buffer = buffer;
        }
        catch
        {
            buffer?.Dispose();
            _cpu = null;
            _buffer = null;
            return false;
        }

        _allocator = new StagingRingAllocator(capacity)
        {
            StrictValidation = GeometryArenaDiagnostics.Enabled
        };
        _footprint = GpuFixedFootprintTracker12.NonLocalInstance.Add("geometry-staging-ring", capacity);
        return true;
    }

    private void Release(ulong sequence)
    {
        if (!_disposed)
        {
            _allocator?.Release(sequence);
        }
    }

    internal readonly record struct StagedRegion(
        ID3D12Resource Resource,
        ulong Offset,
        IntPtr CpuPtr,
        IDisposable Release);

    private sealed class ReleaseHandle(GpuGeometryStagingRing12 ring, ulong sequence) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            ring.Release(sequence);
        }
    }
}
