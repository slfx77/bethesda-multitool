using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A sub-allocating geometry arena for static reference meshes. Replaces the per-mesh
///     committed-resource churn with a handful of large blocks. The default
///     <see cref="GpuGeometryArenaBackingMode.UploadHeap" /> mode preserves the established path:
///     persistently-mapped UPLOAD blocks receive a direct <c>memcpy</c>, stay in
///     <see cref="ResourceStates.GenericRead" /> for life, and are bound directly. The opt-in
///     <see cref="GpuGeometryArenaBackingMode.DefaultHeap" /> mode keeps the long-lived geometry in
///     device-local memory and records a staging copy plus a COPY_DEST→COMMON barrier during the
///     frame's upload phase.
///     <para>
///         Render-thread only: <c>Upload</c> on mesh upload, <see cref="Free" /> on eviction
///         (deferred through the deletion queue so in-flight draws drain first).
///     </para>
/// </summary>
internal sealed unsafe class GpuGeometryArena12 : ITrackableResource, IDisposable
{
    /// <summary>
    ///     16 MB per standard block. A handful of monolithic meshes exceed it (RepBay.NIF and
    ///     B29_RiseAnim.NIF run ~25 MB) — those get a dedicated block sized to the allocation.
    /// </summary>
    public const long DefaultBlockSize = 16L * 1024L * 1024L;

    private const int RegionAlignment = 16;
    private readonly GeometryArenaAllocator _allocator;
    private readonly List<IntPtr> _blockPointers = new();

    // Nullable and never compacted: a slot is null while its memory is released (see
    // ReleaseEmptyBlocks) and is re-backed in place on demand. ArenaAllocation.BlockIndex indexes
    // this list, so removing an entry would invalidate every live allocation after it.
    private readonly List<ID3D12Resource?> _blockResources = new();

    private readonly GpuGeometryArenaBackingMode _backingMode;
    private readonly GpuDevice12 _gpu;
    private readonly List<int> _pendingBlockCopies = new();
    private readonly GpuGeometryStagingRing12? _stagingRing;
    private long _committedBytes;
    private int _pendingCopyCount;
    private bool _disposed;
    private ResourceRegistration? _registration;

    public GpuGeometryArena12(
        GpuDevice12 gpu,
        long blockSize = DefaultBlockSize,
        GpuGeometryArenaBackingMode backingMode = GpuGeometryArenaBackingMode.UploadHeap)
    {
        if (backingMode is not GpuGeometryArenaBackingMode.UploadHeap and
            not GpuGeometryArenaBackingMode.DefaultHeap)
        {
            throw new ArgumentOutOfRangeException(nameof(backingMode), backingMode, "Unknown geometry arena backing mode.");
        }

        _gpu = gpu;
        _backingMode = backingMode;
        _stagingRing = backingMode == GpuGeometryArenaBackingMode.DefaultHeap
            ? new GpuGeometryStagingRing12(gpu)
            : null;
        _allocator = new GeometryArenaAllocator(blockSize)
        {
            // FALLOUT_VIEWER_GEOMETRY_VALIDATE: double-free / overlap throws at the offending
            // call site and QueryLiveness can distinguish freed from recycled ranges.
            StrictValidation = GeometryArenaDiagnostics.Enabled
        };
    }

    /// <summary>Arena blocks currently committed.</summary>
    public int BlockCount => _blockResources.Count;

    /// <summary>The heap class selected at construction; it never changes while allocations live.</summary>
    public GpuGeometryArenaBackingMode BackingMode => _backingMode;

    /// <summary>Permanent UPLOAD staging committed for DEFAULT backing, otherwise 0.</summary>
    public long StagingCapacityBytes => _stagingRing?.CapacityBytes ?? 0;

    /// <summary>Staging bytes whose recorded copies may still be reading them.</summary>
    public long StagingLiveBytes => _stagingRing?.LiveBytes ?? 0;

    public long StagingServedCount => _stagingRing?.ServedCount ?? 0;

    public long StagingOverflowCount => _stagingRing?.OverflowCount ?? 0;

    /// <summary>Recorded DEFAULT-heap copies not yet retired by the frame deletion queue.</summary>
    public int PendingCopyCount => _pendingCopyCount;

    /// <summary>
    ///     Monotonic signal that allocator or copy-retirement state changed in a way that can make an
    ///     arena block newly releasable. Consumers can skip <see cref="ReleaseEmptyBlocks" /> while
    ///     this value is unchanged without coupling reclamation to mesh-cache eviction bookkeeping.
    ///     Advances only after a free succeeds, and after an actual pending-copy decrement.
    /// </summary>
    public ulong ReclamationGeneration { get; private set; }

    /// <summary>
    ///     Releases arena and staging resources. This method does not submit or wait on a fence; the
    ///     owner must idle the GPU first, as the reference-cache teardown already does. Pending copy
    ///     retirement handles become harmless no-ops after disposal.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration?.Dispose();
        _registration = null;
        _stagingRing?.Dispose();
        for (var i = 0; i < _blockResources.Count; i++)
        {
            // Null when ReleaseEmptyBlocks already returned this block's memory.
            if (_blockResources[i] is not { } resource)
            {
                continue;
            }

            if (_backingMode == GpuGeometryArenaBackingMode.UploadHeap)
            {
                resource.Unmap(0);
            }

            resource.Dispose();
        }

        _blockResources.Clear();
        _blockPointers.Clear();
        _pendingBlockCopies.Clear();
        _pendingCopyCount = 0;
    }

    public string ResourceName => nameof(GpuGeometryArena12);

    public ResourceCategory Category => ResourceCategory.GpuResident;

    /// <summary>
    ///     Tracking-only conformance: bytes = committed arena blocks; entries = block slots.
    ///     Empty-block release decrements the committed total; allocator ranges remain owned by live
    ///     meshes and are reclaimed through the mesh LRU's eviction cascade.
    ///     <para>
    ///         UPLOAD backing is <see cref="GpuMemorySegment.NonLocal" /> system memory. DEFAULT
    ///         backing is <see cref="GpuMemorySegment.Local" /> device-local memory; its bounded
    ///         staging ring is accounted separately as NonLocal.
    ///     </para>
    ///     <para>
    ///         <see cref="_committedBytes" /> is the live committed backing, including free-list holes
    ///         inside non-empty blocks. <c>GeometryArenaAllocator.AllocatedBytes</c> is the tighter live
    ///         sub-allocation figure.
    ///     </para>
    /// </summary>
    public ResourceStats GetStats()
    {
        return new ResourceStats
        {
            EstimatedBytes = _committedBytes,
            EntryCount = _blockResources.Count,
            Segment = _backingMode == GpuGeometryArenaBackingMode.DefaultHeap
                ? GpuMemorySegment.Local
                : GpuMemorySegment.NonLocal
        };
    }

    /// <summary>
    ///     Registers the arena with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the arena for fluent construction.
    /// </summary>
    public GpuGeometryArena12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        _stagingRing?.RegisterWith(registry, instanceTag);
        return this;
    }

    /// <summary>
    ///     Packs <paramref name="vertexBytes" /> then (alignment-padded) <paramref name="indexBytes" />
    ///     into one sub-allocation and copies both into the mapped block. The returned GPU virtual
    ///     addresses are bound directly as vertex / index buffer views.
    /// </summary>
    public GeometryAllocation12 Upload(
        ReadOnlySpan<byte> vertexBytes, ReadOnlySpan<byte> indexBytes, string? debugTag = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backingMode != GpuGeometryArenaBackingMode.UploadHeap)
        {
            throw new InvalidOperationException(
                "DEFAULT-heap geometry must be uploaded through the command-list overload.");
        }

        if (vertexBytes.Length == 0)
            throw new ArgumentException("Refusing to upload zero vertices.", nameof(vertexBytes));

        var alignedVertexBytes = (int)AlignUp(vertexBytes.Length, RegionAlignment);
        var totalBytes = alignedVertexBytes + indexBytes.Length;

        var allocation = _allocator.Allocate(totalBytes);
        try
        {
            EnsureBlocksThrough(allocation.BlockIndex);
        }
        catch
        {
            // CreateCommittedResource can fail under memory pressure (E_OUTOFMEMORY seen 2026-08-12).
            // The allocator already reserved the range, so return it — otherwise the span leaks as
            // allocated and _allocatedBytes drifts. The allocator keeps the unbacked block; a later
            // upload retries the commit once pressure eases.
            FreeAllocation(allocation);
            throw;
        }

        var cpuBase = (byte*)_blockPointers[allocation.BlockIndex] + allocation.Offset;
        vertexBytes.CopyTo(new Span<byte>(cpuBase, vertexBytes.Length));
        if (indexBytes.Length > 0)
        {
            indexBytes.CopyTo(new Span<byte>(cpuBase + alignedVertexBytes, indexBytes.Length));
        }

        EmitAudit("alloc", allocation, debugTag);

        // EnsureBlocksThrough above guarantees this slot is backed; assert it rather than suppress,
        // because a null here would mean a released block handed out a live range — the one way this
        // scheme could corrupt geometry, and it should fail loudly rather than produce a bad address.
        var block = _blockResources[allocation.BlockIndex]
                    ?? throw new InvalidOperationException(
                        $"Arena block {allocation.BlockIndex} is unbacked after EnsureBlocksThrough.");
        var gpuBase = block.GPUVirtualAddress + (ulong)allocation.Offset;
        return new GeometryAllocation12(
            allocation,
            gpuBase,
            gpuBase + (ulong)alignedVertexBytes,
            (uint)vertexBytes.Length,
            (uint)indexBytes.Length,
            debugTag);
    }

    /// <summary>
    ///     Unified render-frame upload entry point. In <see cref="GpuGeometryArenaBackingMode.UploadHeap" />
    ///     this delegates to the established mapped <see cref="Upload(ReadOnlySpan{byte}, ReadOnlySpan{byte}, string?)" />
    ///     path without recording a command. In <see cref="GpuGeometryArenaBackingMode.DefaultHeap" /> it
    ///     stages both streams, records their copy, and returns the block to COMMON for subsequent draws.
    ///     <para>
    ///         DEFAULT uploads must run before any geometry-arena draw on <paramref name="cmd" />. A
    ///         COMMON buffer can then promote implicitly to COPY_DEST, the explicit COPY_DEST→COMMON
    ///         barrier provides write visibility and restores the block's resting state, and the draw
    ///         later promotes it to vertex/index read states. Uploading after a draw would require an
    ///         explicit read→COPY_DEST transition that this stateless arena deliberately does not track.
    ///     </para>
    ///     <para>
    ///         The staging release and a destination-block lifetime hold are enqueued after the copy
    ///         and barrier are recorded. The block hold matters when later mesh construction rejects
    ///         every submesh and frees the range immediately: an allocator-empty block still cannot
    ///         be released until the recorded copy itself drains.
    ///         <paramref name="deletionQueue" /> must be the render submission's FIFO queue and tick
    ///         only after the recorder's frame-slot fence wait; recycling a ring region earlier would
    ///         let the CPU overwrite bytes the GPU copy still reads.
    ///     </para>
    /// </summary>
    public GeometryAllocation12 Upload(
        ID3D12GraphicsCommandList cmd,
        GpuDeletionQueue12 deletionQueue,
        ReadOnlySpan<byte> vertexBytes,
        ReadOnlySpan<byte> indexBytes,
        string? debugTag = null)
    {
        if (_backingMode == GpuGeometryArenaBackingMode.UploadHeap)
        {
            return Upload(vertexBytes, indexBytes, debugTag);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(deletionQueue);
        if (vertexBytes.Length == 0)
        {
            throw new ArgumentException("Refusing to upload zero vertices.", nameof(vertexBytes));
        }

        var alignedVertexBytes = (int)AlignUp(vertexBytes.Length, RegionAlignment);
        var totalBytes = alignedVertexBytes + indexBytes.Length;
        var allocation = _allocator.Allocate(totalBytes);

        ID3D12Resource stagingResource = null!;
        ulong stagingOffset = 0;
        IDisposable? stagingRelease = null;
        ID3D12Resource? transientStaging = null;
        var stagingFromRing = false;
        try
        {
            EnsureBlocksThrough(allocation.BlockIndex);
            if (_stagingRing!.TryReserve(totalBytes, out var region))
            {
                stagingResource = region.Resource;
                stagingOffset = region.Offset;
                stagingRelease = region.Release;
                stagingFromRing = true;
                WriteStreams((byte*)region.CpuPtr, vertexBytes, alignedVertexBytes, indexBytes);
            }
            else
            {
                // Preserve progress when the bounded ring is full or a single mesh exceeds its cap.
                // This one-shot resource is retired by the same queue after its recorded copy drains.
                transientStaging = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                    HeapProperties.UploadHeapProperties,
                    HeapFlags.None,
                    ResourceDescription.Buffer((ulong)totalBytes),
                    ResourceStates.GenericRead);

                void* cpuPtr = null;
                transientStaging.Map(0, &cpuPtr).CheckError();
                try
                {
                    WriteStreams((byte*)cpuPtr, vertexBytes, alignedVertexBytes, indexBytes);
                }
                finally
                {
                    transientStaging.Unmap(0);
                }

                stagingResource = transientStaging;
                stagingRelease = transientStaging;
            }
        }
        catch
        {
            // If a FIFO ring region was reserved, delay even an unsubmitted region behind older
            // submissions rather than releasing it out of order. Transient staging has no such
            // dependency and can be returned immediately before any copy command was recorded.
            if (stagingFromRing && stagingRelease is not null)
            {
                deletionQueue.EnqueueDispose(stagingRelease);
            }
            else
            {
                stagingRelease?.Dispose();
                if (stagingRelease is null)
                {
                    transientStaging?.Dispose();
                }
            }

            FreeAllocation(allocation);
            throw;
        }

        var block = _blockResources[allocation.BlockIndex]
                    ?? throw new InvalidOperationException(
                        $"Arena block {allocation.BlockIndex} is unbacked after EnsureBlocksThrough.");

        // Blocks are created/rest in COMMON. Copy promotes to COPY_DEST; returning to COMMON both
        // makes the write visible and permits the later vertex/index read promotion without tracking
        // a mutable per-block state across the many meshes packed into it.
        cmd.CopyBufferRegion(block, (ulong)allocation.Offset, stagingResource, stagingOffset, (ulong)totalBytes);
        cmd.ResourceBarrierTransition(block, ResourceStates.CopyDest, ResourceStates.Common);
        MarkCopyPending(allocation.BlockIndex);
        deletionQueue.EnqueueDispose(new CopyRetirement(this, allocation.BlockIndex, stagingRelease!));

        EmitAudit("alloc", allocation, debugTag);
        var gpuBase = block.GPUVirtualAddress + (ulong)allocation.Offset;
        return new GeometryAllocation12(
            allocation,
            gpuBase,
            gpuBase + (ulong)alignedVertexBytes,
            (uint)vertexBytes.Length,
            (uint)indexBytes.Length,
            debugTag);
    }

    /// <summary>Returns an allocation's range to the free-list. No-op after <see cref="Dispose" />.</summary>
    public void Free(GeometryAllocation12 allocation)
    {
        if (_disposed)
        {
            return;
        }

        EmitAudit("free", allocation.Allocation, allocation.DebugTag);
        FreeAllocation(allocation.Allocation);
    }

    /// <summary>
    ///     Returns the memory of every fully-drained block to the OS, leaving the block's slot in
    ///     place so live allocations elsewhere keep their indices. Returns the bytes released.
    ///     <para>
    ///         Render-thread only, and safe by the arena's own ordering: evicted draw ranges return
    ///         through the deletion queue, while DEFAULT uploads hold a per-block pending-copy count
    ///         on that same queue. A block is released only when both its allocator range and copy
    ///         count are empty, so neither an in-flight draw nor an immediately-rejected mesh upload
    ///         can still reference it. The arena does not need a second fence.
    ///     </para>
    ///     <para>
    ///         The block is NOT retired: its free list stays intact, and
    ///         <see cref="EnsureBlocksThrough" /> re-backs the slot on demand if the allocator hands
    ///         out a range in it again. Retiring instead would return the memory but permanently
    ///         strand the address space, so a long session with churn would keep appending blocks it
    ///         could have reused.
    ///     </para>
    /// </summary>
    public long ReleaseEmptyBlocks()
    {
        if (_disposed)
        {
            return 0;
        }

        long released = 0;
        for (var i = 0; i < _blockResources.Count; i++)
        {
            if (_blockResources[i] is not { } resource || !_allocator.IsBlockEmpty(i) ||
                _pendingBlockCopies[i] != 0)
            {
                continue;
            }

            if (_backingMode == GpuGeometryArenaBackingMode.UploadHeap)
            {
                resource.Unmap(0);
            }

            resource.Dispose();
            _blockResources[i] = null;
            _blockPointers[i] = IntPtr.Zero;

            var blockBytes = _allocator.BlockSizeOf(i);
            _committedBytes -= blockBytes;
            released += blockBytes;
        }

        return released;
    }

    /// <summary>
    ///     A disposable that frees <paramref name="allocation" /> when disposed. Enqueue it on the
    ///     <see cref="GpuDeletionQueue12" /> so the range is reclaimed only after in-flight draws
    ///     referencing it have drained.
    /// </summary>
    public IDisposable DeferredFreeHandle(GeometryAllocation12 allocation)
    {
        // Creation moment == eviction moment: logged separately from the eventual "free" so the
        // audit trail shows how long the deletion queue held the range.
        EmitAudit("free-enqueue", allocation.Allocation, allocation.DebugTag);
        return new FreeHandle(this, allocation);
    }

    /// <summary>
    ///     Liveness of <paramref name="allocation" />'s range under strict tracking
    ///     (<see cref="ArenaLiveness.Untracked" /> when FALLOUT_VIEWER_GEOMETRY_VALIDATE is off).
    /// </summary>
    public ArenaLiveness QueryLiveness(in GeometryAllocation12 allocation)
    {
        return _disposed ? ArenaLiveness.Untracked : _allocator.QueryLiveness(allocation.Allocation);
    }

    /// <summary>
    ///     Hashes the mapped arena bytes that a view over <paramref name="gpuAddress" /> /
    ///     <paramref name="sizeInBytes" /> would make the GPU read — possible only because arena
    ///     blocks are persistently-mapped UPLOAD heap. False when the address does not fall inside
    ///     the allocation's block (a stale view whose range left the arena entirely).
    /// </summary>
    public bool TryHashRange(
        in GeometryAllocation12 allocation, ulong gpuAddress, uint sizeInBytes, out ulong fnv1a64)
    {
        fnv1a64 = 0;
        var blockIndex = allocation.Allocation.BlockIndex;
        if (_disposed || _backingMode != GpuGeometryArenaBackingMode.UploadHeap ||
            (uint)blockIndex >= (uint)_blockResources.Count)
        {
            return false;
        }

        // A released block has no bytes to hash — the same "range left the arena" answer this method
        // already gives for a stale view.
        if (_blockResources[blockIndex] is not { } block)
        {
            return false;
        }

        var blockBase = block.GPUVirtualAddress;
        var blockSize = (ulong)_allocator.BlockSizeOf(blockIndex);
        if (gpuAddress < blockBase || gpuAddress - blockBase + sizeInBytes > blockSize)
        {
            return false;
        }

        var cpu = (byte*)_blockPointers[blockIndex] + (long)(gpuAddress - blockBase);
        fnv1a64 = GeometryArenaDiagnostics.Fnv1a64(new ReadOnlySpan<byte>(cpu, (int)sizeInBytes));
        return true;
    }

    private static void EmitAudit(string op, in ArenaAllocation allocation, string? tag)
    {
        if (!GeometryArenaDiagnostics.AuditEnabled || !RendererProfilerTrace.IsEnabled)
        {
            return;
        }

        RendererProfilerTrace.Event("geometry-arena", new Dictionary<string, object?>
        {
            ["op"] = op,
            ["allocId"] = allocation.AllocationId,
            ["block"] = allocation.BlockIndex,
            ["offset"] = allocation.Offset,
            ["alignedSize"] = allocation.AlignedSize,
            ["tag"] = tag
        });
    }

    /// <summary>
    ///     Commits backing resources in block order up to and including
    ///     <paramref name="requiredBlockIndex" />. Deliberately NOT "through the allocator's full
    ///     block count": if a tail block's commit failed under memory pressure, an upload landing in
    ///     an already-backed earlier block must not be held hostage by the unbacked tail.
    /// </summary>
    private void EnsureBlocksThrough(int requiredBlockIndex)
    {
        while (_blockResources.Count <= requiredBlockIndex)
        {
            _blockResources.Add(null);
            _blockPointers.Add(IntPtr.Zero);
            _pendingBlockCopies.Add(0);
        }

        // A slot can be null either because it was just appended, or because ReleaseEmptyBlocks gave
        // its memory back. Both are re-backed the same way — the slot itself never moves, because
        // ArenaAllocation.BlockIndex indexes this list and shifting it would invalidate every live
        // allocation in the arena.
        if (_blockResources[requiredBlockIndex] is not null)
        {
            return;
        }

        // Per-block size: standard blocks share _blockSize; an oversized mesh's dedicated
        // block is exactly as large as the allocation that forced it.
        var blockBytes = _allocator.BlockSizeOf(requiredBlockIndex);
        ID3D12Resource resource;
        if (_backingMode == GpuGeometryArenaBackingMode.DefaultHeap)
        {
            // Buffers created on DEFAULT heap must begin in COMMON. Each upload promotes to
            // COPY_DEST and explicitly transitions back; the resource is never CPU-mapped.
            resource = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)blockBytes),
                ResourceStates.Common);
        }
        else
        {
            resource = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)blockBytes),
                ResourceStates.GenericRead);

            void* mapped = null;
            try
            {
                resource.Map(0, &mapped).CheckError();
            }
            catch
            {
                resource.Dispose();
                throw;
            }

            _blockPointers[requiredBlockIndex] = (IntPtr)mapped;
        }

        _blockResources[requiredBlockIndex] = resource;
        _committedBytes += blockBytes;
    }

    /// <summary>Writes the two packed streams into either mapped ring or transient staging memory.</summary>
    private static void WriteStreams(
        byte* destination,
        ReadOnlySpan<byte> vertexBytes,
        int alignedVertexBytes,
        ReadOnlySpan<byte> indexBytes)
    {
        vertexBytes.CopyTo(new Span<byte>(destination, vertexBytes.Length));
        if (indexBytes.Length > 0)
        {
            indexBytes.CopyTo(new Span<byte>(destination + alignedVertexBytes, indexBytes.Length));
        }
    }

    private static long AlignUp(long value, int alignment)
    {
        return (value + alignment - 1) & ~((long)alignment - 1);
    }

    private void MarkCopyPending(int blockIndex)
    {
        _pendingBlockCopies[blockIndex]++;
        _pendingCopyCount++;
    }

    private void CompleteCopy(int blockIndex)
    {
        if (_disposed)
        {
            return;
        }

        if ((uint)blockIndex >= (uint)_pendingBlockCopies.Count || _pendingBlockCopies[blockIndex] <= 0)
        {
            if (GeometryArenaDiagnostics.Enabled)
            {
                throw new InvalidOperationException(
                    $"Geometry arena copy retirement for block {blockIndex} has no matching pending copy.");
            }

            return;
        }

        _pendingBlockCopies[blockIndex]--;
        _pendingCopyCount--;
        AdvanceReclamationGeneration();
    }

    /// <summary>
    ///     The sole allocator-free path. Generation advances after, never before, the allocator
    ///     accepts the free; strict-validation rejection therefore cannot signal phantom work.
    /// </summary>
    private void FreeAllocation(in ArenaAllocation allocation)
    {
        _allocator.Free(allocation);
        AdvanceReclamationGeneration();
    }

    private void AdvanceReclamationGeneration()
    {
        // Saturation preserves monotonicity even in the theoretical 2^64-operation session.
        if (ReclamationGeneration != ulong.MaxValue)
        {
            ReclamationGeneration++;
        }
    }

    private sealed class FreeHandle(GpuGeometryArena12 arena, GeometryAllocation12 allocation) : IDisposable
    {
        private bool _freed;

        public void Dispose()
        {
            // Idempotence guard: the deletion queue disposes each entry once, but a double-Dispose
            // from any future path must not become a silent double-free of the arena range.
            if (_freed)
            {
                return;
            }

            _freed = true;
            arena.Free(allocation);
        }
    }

    /// <summary>
    ///     One deletion-queue entry owns both sides of a DEFAULT upload's lifetime: staging remains
    ///     readable until the copy drains, and the destination block remains committed even if its
    ///     sub-allocation was rejected and freed before submission.
    /// </summary>
    private sealed class CopyRetirement(
        GpuGeometryArena12 arena,
        int blockIndex,
        IDisposable stagingRelease) : IDisposable
    {
        private bool _retired;

        public void Dispose()
        {
            if (_retired)
            {
                return;
            }

            _retired = true;
            try
            {
                stagingRelease.Dispose();
            }
            finally
            {
                arena.CompleteCopy(blockIndex);
            }
        }
    }
}

/// <summary>
///     A geometry sub-allocation. <see cref="VertexBufferLocation" /> / <see cref="IndexBufferLocation" />
///     are GPU virtual addresses bound directly as <see cref="VertexBufferView" /> /
///     <see cref="IndexBufferView" /> base locations; <see cref="Allocation" /> carries the range
///     back to <see cref="GpuGeometryArena12.Free" />. <see cref="DebugTag" /> names the owning mesh
///     in diagnostics (model path) and costs one reference copy.
/// </summary>
internal readonly struct GeometryAllocation12
{
    internal GeometryAllocation12(
        ArenaAllocation allocation,
        ulong vertexBufferLocation,
        ulong indexBufferLocation,
        uint vertexBytes,
        uint indexBytes,
        string? debugTag = null)
    {
        Allocation = allocation;
        VertexBufferLocation = vertexBufferLocation;
        IndexBufferLocation = indexBufferLocation;
        VertexBytes = vertexBytes;
        IndexBytes = indexBytes;
        DebugTag = debugTag;
    }

    internal ArenaAllocation Allocation { get; }

    public ulong VertexBufferLocation { get; }

    public ulong IndexBufferLocation { get; }

    public uint VertexBytes { get; }

    public uint IndexBytes { get; }

    public string? DebugTag { get; }
}
