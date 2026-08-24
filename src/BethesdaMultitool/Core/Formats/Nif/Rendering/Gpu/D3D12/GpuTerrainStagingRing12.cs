using BethesdaMultitool.Core.Diagnostics;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     One persistently-mapped UPLOAD-heap buffer that terrain cell uploads stage through, replacing
///     a freshly committed staging resource per cell.
///     <para>
///         The cost this removes is not bytes but <b>churn</b>. Every cell upload used to call
///         <c>CreateCommittedResource</c> (a kernel transition that allocates and page-maps
///         GPU-visible system memory), <c>Map</c>, <c>Unmap</c>, and then queue a deferred
///         <c>Release</c> — per cell, at up to the terrain build-start budget per frame, for the
///         whole time a flythrough is filling the view. The transient buffers also each carried
///         D3D12's 64 KiB rounding and stayed committed until the deletion queue drained, so their
///         combined peak was comparable to a ring that simply stays put.
///     </para>
///     <para>
///         <b>Reclamation rides <see cref="GpuDeletionQueue12" />, not a frame index.</b> A staging
///         region may be reused only once the GPU has finished the copy reading it, which is exactly
///         the question the deletion queue already answers; enqueueing a release handle per region
///         reuses that fence discipline instead of inventing a second one. Because the queue is a
///         frame-stamped <c>Queue</c>, handles retire in submission order, which is the FIFO
///         invariant <see cref="StagingRingAllocator" /> needs.
///     </para>
///     <para>
///         ⚠ The failure mode is quieter than the one it replaces, and worth knowing: a transient
///         staging buffer retired too early is <i>disposed</i> too early, which faults loudly; a ring
///         region retired too early is <i>reused</i> too early, which corrupts the next cell's
///         geometry silently. That is not new exposure — the arena's own range frees ride the same
///         queue with the same reuse semantics — but it does mean the deletion queue's hold is
///         load-bearing here, and any path that ticks it more than once per submitted frame breaks
///         this class before it breaks anything visible.
///     </para>
///     <para>
///         <b>Exhaustion is routine, not an error.</b> When a burst outruns the ring the caller
///         falls back to the transient committed buffer it used before — so the worst case is
///         today's behaviour rather than a dropped or corrupted cell. That is what lets the ring be
///         sized for the common case instead of the peak.
///     </para>
/// </summary>
internal sealed unsafe class GpuTerrainStagingRing12 : ITrackableResource, IDisposable
{
    /// <summary>Smallest ring worth committing. Covers a full frame of 33-grid cells many times over.</summary>
    public const long MinCapacityBytes = 4L * 1024 * 1024;

    /// <summary>
    ///     Ceiling on permanently-resident staging. Above this the ring stops paying for itself: it
    ///     would hold more than the transient buffers it replaces ever peaked at, and the overflow
    ///     path costs only what today already costs.
    /// </summary>
    public const long MaxCapacityBytes = 32L * 1024 * 1024;

    /// <summary>
    ///     Cell uploads the ring aims to absorb per frame — the terrain build-start ceiling, since a
    ///     frame cannot upload more cells than it started builds for plus its carried backlog.
    /// </summary>
    public const int UploadsPerFrameAllowance = 8;

    /// <summary>
    ///     Frames of uploads that can be outstanding at once: the deletion queue holds regions for
    ///     <see cref="GpuCommandRecorder12.FramesInFlight" /> frames, plus the frame being recorded.
    /// </summary>
    public const int GenerationsHeld = GpuCommandRecorder12.FramesInFlight + 1;

    private readonly GpuDevice12 _gpu;
    private StagingRingAllocator? _allocator;
    private ID3D12Resource? _buffer;
    private byte* _cpu;
    private bool _disposed;
    private IDisposable? _footprint;
    private ResourceRegistration? _registration;

    public GpuTerrainStagingRing12(GpuDevice12 gpu)
    {
        _gpu = gpu;
    }

    /// <summary>Committed ring bytes, or 0 before the first upload sizes it.</summary>
    public long CapacityBytes => _allocator?.Capacity ?? 0;

    /// <summary>Bytes held by regions whose copies the GPU may still be reading.</summary>
    public long LiveBytes => _allocator?.LiveBytes ?? 0;

    /// <summary>Uploads served from the ring since construction.</summary>
    public long ServedCount { get; private set; }

    /// <summary>
    ///     Uploads that fell back to a transient committed staging buffer. A count that climbs
    ///     steadily (rather than only during the initial fill) says the ring is undersized for this
    ///     worldspace's grid.
    /// </summary>
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

    public string ResourceName => nameof(GpuTerrainStagingRing12);

    /// <summary>
    ///     <see cref="ResourceCategory.GpuAttributed" />, not <c>GpuResident</c>: the ring's committed
    ///     capacity is already charged by <see cref="GpuFixedFootprintTracker12" />, and this row
    ///     reports the live portion of it. Counting both into one total would inflate every VRAM
    ///     figure the residency governor steers on.
    /// </summary>
    public ResourceCategory Category => ResourceCategory.GpuAttributed;

    /// <summary>
    ///     <see cref="ResourceStats.Misses" /> is the reason this row exists. Overflow is a legal
    ///     outcome, so an undersized ring costs memory and silently buys nothing — a hit rate that
    ///     stays low outside the initial fill says so out loud.
    ///     <para>
    ///         Read without the render thread's cooperation, per the cheap/lock-free contract. Every
    ///         field is an independently atomic 64-bit read, so the worst case is a snapshot whose
    ///         counters are a few uploads apart — never a torn value.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     Registers the ring with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the ring for fluent construction.
    /// </summary>
    public GpuTerrainStagingRing12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    /// <summary>
    ///     Ring size for a worldspace whose largest single upload is
    ///     <paramref name="largestUploadBytes" />. Pure so the sizing can be pinned without a device,
    ///     and derived from the upload size rather than configured, so it re-scales on its own when
    ///     the terrain vertex format shrinks. Returns 0 for a degenerate request.
    /// </summary>
    public static long PlanCapacityBytes(long largestUploadBytes)
    {
        if (largestUploadBytes <= 0)
        {
            return 0;
        }

        // Clamp the input first: the multiply below must not overflow on a nonsense request, and
        // anything at or above the ceiling is going to the overflow path regardless.
        var perUpload = Math.Min(largestUploadBytes, MaxCapacityBytes);
        var want = perUpload * UploadsPerFrameAllowance * GenerationsHeld;
        return Math.Clamp(want, MinCapacityBytes, MaxCapacityBytes);
    }

    /// <summary>
    ///     Reserves <paramref name="bytes" /> of staging. On success the caller writes its payload to
    ///     <paramref name="region" />'s <c>CpuPtr</c>, copies from
    ///     <c>(region.Resource, region.Offset)</c>, and enqueues <c>region.Release</c> on the deletion
    ///     queue so the region is reclaimed only after the copy has drained. Returns false — routinely
    ///     — when the ring is full or the request exceeds it; the caller then stages transiently.
    /// </summary>
    public bool TryReserve(long bytes, out StagedRegion region)
    {
        region = default;
        if (_disposed || bytes <= 0)
        {
            return false;
        }

        if (!EnsureCreated(bytes))
        {
            OverflowCount++;
            return false;
        }

        if (!_allocator!.TryAllocate(bytes, out var offset, out _, out var sequence))
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
    ///     Commits and persistently maps the ring on first use, sized from the first upload seen.
    ///     A failed commit (memory pressure) is not fatal: the ring stays absent, every upload takes
    ///     the transient path, and a later upload retries the commit once pressure eases.
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
        // UPLOAD heap = system RAM the GPU reads over the bus, so NonLocal — the same distinction
        // that stopped the geometry arena being counted as VRAM.
        _footprint = GpuFixedFootprintTracker12.NonLocalInstance.Add("terrain-staging-ring", capacity);
        return true;
    }

    private void Release(ulong sequence)
    {
        if (_disposed)
        {
            return;
        }

        _allocator?.Release(sequence);
    }

    /// <summary>
    ///     A reserved staging region: where to write it (<paramref name="CpuPtr" />), where to copy
    ///     it from (<paramref name="Resource" /> + <paramref name="Offset" />), and the handle that
    ///     returns it to the ring once the GPU is done.
    /// </summary>
    internal readonly record struct StagedRegion(
        ID3D12Resource Resource,
        ulong Offset,
        IntPtr CpuPtr,
        IDisposable Release);

    private sealed class ReleaseHandle(GpuTerrainStagingRing12 ring, ulong sequence) : IDisposable
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
