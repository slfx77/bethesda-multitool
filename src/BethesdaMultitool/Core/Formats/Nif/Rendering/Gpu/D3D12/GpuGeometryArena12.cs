using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
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
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly ByteArenaAllocator _allocator;

    // Shared owns native blocks, mapping, physical admission and release. Application range/copy
    // ownership below determines when each stable block slot is safe to release or reuse.
    private readonly NativeBufferBlocks _blocks;
    private readonly GpuDevice12 _gpu;
    private readonly List<int> _pendingBlockCopies = new();
    private readonly HashSet<GpuGeometryUploadRetirement12> _failedMappedUploads = new();
    private readonly GpuGeometryStagingRing12? _stagingRing;
    private bool _disposed;
    private RetiredResourceDisposal? _retiredResources;
    private ResourceRegistration? _registration;

    /// <summary>Creates a render-thread arena with independently bounded physical block ownership.</summary>
    /// <param name="gpu">Native device retained by the caller.</param>
    /// <param name="blockSize">Usable standard block size; larger ranges use dedicated blocks.</param>
    /// <param name="backingMode">Native heap and upload policy.</param>
    /// <param name="maximumBackingBytes">Positive physical ceiling, including alignment and blocks awaiting release.</param>
    public GpuGeometryArena12(
        GpuDevice12 gpu,
        long blockSize = DefaultBlockSize,
        GpuGeometryArenaBackingMode backingMode = GpuGeometryArenaBackingMode.UploadHeap,
        long maximumBackingBytes = 4L * 1024 * 1024 * 1024)
    {
        if (backingMode is not GpuGeometryArenaBackingMode.UploadHeap and
            not GpuGeometryArenaBackingMode.DefaultHeap)
        {
            throw new ArgumentOutOfRangeException(nameof(backingMode), backingMode,
                "Unknown geometry arena backing mode.");
        }

        ArgumentNullException.ThrowIfNull(gpu);
        _blocks = new NativeBufferBlocks(gpu.Device,
            backingMode == GpuGeometryArenaBackingMode.UploadHeap ? HeapType.Upload : HeapType.Default,
            maximumBackingBytes);
        _gpu = gpu;
        BackingMode = backingMode;
        _stagingRing = backingMode == GpuGeometryArenaBackingMode.DefaultHeap
            ? new GpuGeometryStagingRing12(gpu)
            : null;
        _allocator = new ByteArenaAllocator(blockSize)
        {
            // Exact ownership is always checked; the diagnostic option adds overlap scanning.
            StrictValidation = GeometryArenaDiagnostics.Enabled
        };
    }

    /// <summary>Arena block slots, including empty slots that can be backed again without changing range identities.</summary>
    public int BlockCount => _blocks.BlockCount;

    /// <summary>Gets all reserved, resident and retiring physical block bytes, including device alignment.</summary>
    public long BackingBytes => _blocks.AllocatedBytes;

    /// <summary>Gets the fixed physical block budget; staging has separate ownership and accounting.</summary>
    public long MaximumBackingBytes => _blocks.MaximumBytes;

    /// <summary>The heap class selected at construction; it never changes while allocations live.</summary>
    public GpuGeometryArenaBackingMode BackingMode { get; }

    /// <summary>Permanent UPLOAD staging committed for DEFAULT backing, otherwise 0.</summary>
    public long StagingCapacityBytes => _stagingRing?.CapacityBytes ?? 0;

    /// <summary>Staging bytes whose recorded copies may still be reading them.</summary>
    public long StagingLiveBytes => _stagingRing?.LiveBytes ?? 0;

    public long StagingServedCount => _stagingRing?.ServedCount ?? 0;

    public long StagingOverflowCount => _stagingRing?.OverflowCount ?? 0;

    /// <summary>Recorded DEFAULT-heap copies not yet retired by the frame deletion queue.</summary>
    public int PendingCopyCount { get; private set; }

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
    ///     Failed native releases and their full charges remain owned for retry.
    /// </summary>
    public void Dispose()
    {
        VerifyAccess();
        if (_retiredResources is null)
        {
            var retired = new RetiredResourceDisposal();
            foreach (var upload in _failedMappedUploads)
                retired.Add(upload, "failed mapped upload");
            retired.Add(_registration, "resource registration", 2);
            retired.Add(_stagingRing, "staging ring");
            retired.Add(_blocks, "physical geometry backing", 1);
            // Publish the complete release owner before dropping aliases. If registration above
            // fails, every failed upload and native child remains in the original collections.
            _retiredResources = retired;
            _disposed = true;
            _registration = null;
            _failedMappedUploads.Clear();
            _pendingBlockCopies.Clear();
            PendingCopyCount = 0;
        }
        _retiredResources.Dispose();
    }

    public string ResourceName => nameof(GpuGeometryArena12);

    public ResourceCategory Category => ResourceCategory.GpuResident;

    /// <summary>
    ///     Tracking-only conformance: bytes = admitted physical blocks; entries = their live owners.
    ///     Empty-block release decrements the committed total; allocator ranges remain owned by live
    ///     meshes and are reclaimed through the mesh LRU's eviction cascade.
    ///     <para>
    ///         UPLOAD backing is <see cref="GpuMemorySegment.NonLocal" /> system memory. DEFAULT
    ///         backing is <see cref="GpuMemorySegment.Local" /> device-local memory; its bounded
    ///         staging ring is accounted separately as NonLocal.
    ///     </para>
    ///     <para>
    ///         <see cref="BackingBytes" /> is the live committed backing, including free-list holes
    ///         inside non-empty blocks. <c>ByteArenaAllocator.AllocatedBytes</c> is the tighter live
    ///         sub-allocation figure.
    ///     </para>
    /// </summary>
    public ResourceStats GetStats()
    {
        return new ResourceStats
        {
            EstimatedBytes = BackingBytes,
            EntryCount = _blocks.AllocatedBlockCount,
            Segment = BackingMode == GpuGeometryArenaBackingMode.DefaultHeap
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
    ///     <paramref name="retainAllocation" /> may capture the provisional range before backing or
    ///     copying; after it returns successfully, that caller owns release even when upload fails.
    /// </summary>
    public GeometryAllocation12 Upload(
        ReadOnlySpan<byte> vertexBytes, ReadOnlySpan<byte> indexBytes, string? debugTag = null,
        Action<GeometryAllocation12>? retainAllocation = null)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (BackingMode != GpuGeometryArenaBackingMode.UploadHeap)
        {
            throw new InvalidOperationException(
                "DEFAULT-heap geometry must be uploaded through the command-list overload.");
        }

        if (vertexBytes.Length == 0)
            throw new ArgumentException("Refusing to upload zero vertices.", nameof(vertexBytes));

        var allocationBytes = CalculateAllocationBytes(vertexBytes.Length, indexBytes.Length);
        var alignedVertexBytes = AlignUp(vertexBytes.Length, RegionAlignment);
        var totalBytes = checked(alignedVertexBytes + indexBytes.Length);

        var rollback = new GpuGeometryUploadRetirement12(allocation => FreeAllocation(allocation), CompleteCopy);
        _failedMappedUploads.Add(rollback);
        try
        {
            var allocation = _allocator.Allocate(totalBytes);
            rollback.OwnAllocation(allocation);
            RetainAllocation(retainAllocation, rollback, allocation, vertexBytes.Length, indexBytes.Length, debugTag);
            VerifyAllocationCharge(allocation, allocationBytes);
            var backing = EnsureBlocksThrough(allocation.BlockIndex);

            var cpuBase = (byte*)backing.MappedPointer + allocation.Offset;
            vertexBytes.CopyTo(new Span<byte>(cpuBase, vertexBytes.Length));
            if (indexBytes.Length > 0)
                indexBytes.CopyTo(new Span<byte>(cpuBase + alignedVertexBytes, indexBytes.Length));
            EmitAudit("alloc", allocation, debugTag);
            var block = backing.Resource;
            var gpuBase = block.GPUVirtualAddress + (ulong)allocation.Offset;
            var result = new GeometryAllocation12(allocation, gpuBase, gpuBase + (ulong)alignedVertexBytes,
                (uint)vertexBytes.Length, (uint)indexBytes.Length, debugTag);
            rollback.TransferAllocation();
            _failedMappedUploads.Remove(rollback);
            return result;
        }
        catch (Exception failure)
        {
            rollback.EndUpload();
            try
            {
                rollback.Dispose();
                _failedMappedUploads.Remove(rollback);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Mapped geometry upload and retained rollback failed.", failure, cleanupFailure);
            }
            throw;
        }
        finally { rollback.EndUpload(); }
    }

    /// <summary>
    ///     Unified render-frame upload entry point. In <see cref="GpuGeometryArenaBackingMode.UploadHeap" />
    ///     this delegates to the established mapped upload
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
    ///         Retirement ownership is queued before allocation and the copy hold is captured before
    ///         either native command. The block hold matters when later mesh construction rejects
    ///         every submesh and frees the range immediately: an allocator-empty block still cannot
    ///         be released until the recorded copy itself drains.
    ///         <paramref name="deletionQueue" /> must be the render submission's FIFO queue and tick
    ///         only after the recorder's frame-slot fence wait; recycling a ring region earlier would
    ///         let the CPU overwrite bytes the GPU copy still reads.
    ///     </para>
    ///     <paramref name="retainAllocation" /> may capture the provisional range before backing or
    ///     recording; after it returns successfully, that caller owns release even when upload fails.
    /// </summary>
    public GeometryAllocation12 Upload(
        ID3D12GraphicsCommandList cmd,
        GpuDeletionQueue12 deletionQueue,
        ReadOnlySpan<byte> vertexBytes,
        ReadOnlySpan<byte> indexBytes,
        string? debugTag = null,
        Action<GeometryAllocation12>? retainAllocation = null)
    {
        VerifyAccess();
        if (BackingMode == GpuGeometryArenaBackingMode.UploadHeap)
        {
            return Upload(vertexBytes, indexBytes, debugTag, retainAllocation);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(deletionQueue);
        if (vertexBytes.Length == 0)
        {
            throw new ArgumentException("Refusing to upload zero vertices.", nameof(vertexBytes));
        }

        var allocationBytes = CalculateAllocationBytes(vertexBytes.Length, indexBytes.Length);
        var alignedVertexBytes = AlignUp(vertexBytes.Length, RegionAlignment);
        var totalBytes = checked(alignedVertexBytes + indexBytes.Length);
        var rollback = new GpuGeometryUploadRetirement12(allocation => FreeAllocation(allocation), CompleteCopy);
        // Admission precedes native allocation and command recording. A stopped queue rejects the
        // active ticket synchronously, so no unowned range or staging can be created afterward.
        deletionQueue.EnqueueDispose(rollback);
        try
        {
            var allocation = _allocator.Allocate(totalBytes);
            rollback.OwnAllocation(allocation);
            RetainAllocation(retainAllocation, rollback, allocation, vertexBytes.Length, indexBytes.Length, debugTag);
            VerifyAllocationCharge(allocation, allocationBytes);
            var backing = EnsureBlocksThrough(allocation.BlockIndex);
            ID3D12Resource stagingResource;
            ulong stagingOffset = 0;
            if (_stagingRing!.TryReserve(totalBytes, out var region))
            {
                stagingResource = region.Resource;
                stagingOffset = region.Offset;
                rollback.OwnStaging(region.Release);
                WriteStreams((byte*)region.CpuPtr, vertexBytes, alignedVertexBytes, indexBytes);
            }
            else
            {
                // Preserve progress when the bounded ring is full or a single mesh exceeds its cap.
                // This one-shot resource is retired by the same queue after its recorded copy drains.
                var transientStaging = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                    HeapProperties.UploadHeapProperties,
                    HeapFlags.None,
                    ResourceDescription.Buffer((ulong)totalBytes),
                    ResourceStates.GenericRead);
                rollback.OwnStaging(transientStaging);

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
            }
            var block = backing.Resource;
            // The pre-registered ticket already owns staging and the destination hold before either
            // native command can throw or make the upload potentially executable.
            MarkCopyPending(allocation.BlockIndex);
            rollback.OwnCopyHold();
            cmd.CopyBufferRegion(block, (ulong)allocation.Offset, stagingResource, stagingOffset, (ulong)totalBytes);
            cmd.ResourceBarrierTransition(block, ResourceStates.CopyDest, ResourceStates.Common);
            EmitAudit("alloc", allocation, debugTag);
            var gpuBase = block.GPUVirtualAddress + (ulong)allocation.Offset;
            var result = new GeometryAllocation12(allocation, gpuBase, gpuBase + (ulong)alignedVertexBytes,
                (uint)vertexBytes.Length, (uint)indexBytes.Length, debugTag);
            rollback.TransferAllocation();
            return result;
        }
        finally { rollback.EndUpload(); }
    }

    /// <summary>Transfers the range before fallible backing or recording, so failed uploads keep their caller's residency charge.</summary>
    /// <param name="retain">Optional nonthrowing ownership capture; a thrown callback leaves ownership with rollback.</param>
    /// <param name="rollback">Already-retained upload owner.</param>
    /// <param name="allocation">Exact range with GPU views not yet available.</param>
    /// <param name="vertexBytes">Raw vertex stream size.</param>
    /// <param name="indexBytes">Raw index stream size.</param>
    /// <param name="debugTag">Source identity for later release diagnostics.</param>
    private static void RetainAllocation(Action<GeometryAllocation12>? retain, GpuGeometryUploadRetirement12 rollback,
        ByteArenaAllocation allocation, int vertexBytes, int indexBytes, string? debugTag)
    {
        if (retain is null) return;
        retain(new GeometryAllocation12(allocation, 0, 0, (uint)vertexBytes, (uint)indexBytes, debugTag));
        rollback.TransferAllocation();
    }

    /// <summary>Returns an allocation's range to the free-list. No-op after <see cref="Dispose" />.</summary>
    public void Free(GeometryAllocation12 allocation)
    {
        VerifyAccess();
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
        VerifyAccess();
        if (_disposed)
        {
            return 0;
        }

        long released = 0;
        List<Exception>? failures = null;
        for (var i = 0; i < _blocks.BlockCount; i++)
        {
            if (!_allocator.IsBlockEmpty(i) || _pendingBlockCopies[i] != 0)
            {
                continue;
            }

            // Shared retains this exact physical charge if unmapping or release fails. The slot
            // stays occupied until cleanup succeeds; no later allocation can overwrite that owner.
            try
            {
                released += _blocks.ReleaseBlockAfterRetirement(i);
            }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }

        if (failures is not null)
            throw new AggregateException("Empty geometry blocks retain failed native releases for retry.", failures);
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
    ///     Reports exact shared range ownership regardless of optional overlap diagnostics.
    ///     A disposed arena or default range reports <see cref="ByteArenaLiveness.Untracked" />.
    /// </summary>
    public ByteArenaLiveness QueryLiveness(in GeometryAllocation12 allocation)
    {
        return _disposed ? ByteArenaLiveness.Untracked : _allocator.QueryLiveness(allocation.Allocation);
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
        if (_disposed || BackingMode != GpuGeometryArenaBackingMode.UploadHeap ||
            (uint)blockIndex >= (uint)_blocks.BlockCount)
        {
            return false;
        }

        // A released block has no bytes to hash — the same "range left the arena" answer this method
        // already gives for a stale view.
        if (!_blocks.TryGetBlock(blockIndex, out var backing))
        {
            return false;
        }

        var block = backing.Resource;
        var blockBase = block.GPUVirtualAddress;
        var blockSize = (ulong)_allocator.BlockSizeOf(blockIndex);
        if (gpuAddress < blockBase || gpuAddress - blockBase + sizeInBytes > blockSize)
        {
            return false;
        }

        var cpu = (byte*)backing.MappedPointer + (long)(gpuAddress - blockBase);
        fnv1a64 = GeometryArenaDiagnostics.Fnv1a64(new ReadOnlySpan<byte>(cpu, (int)sizeInBytes));
        return true;
    }

    private static void EmitAudit(string op, in ByteArenaAllocation allocation, string? tag)
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
    ///     Extends pending-copy metadata and obtains the exact Shared-owned native block.
    ///     An upload in an already-backed block remains usable when another block cannot be admitted.
    /// </summary>
    /// <param name="requiredBlockIndex">Stable slot selected by the shared byte allocator.</param>
    /// <returns>Borrowed native backing; the arena retains its owner through all range and copy users.</returns>
    /// <exception cref="GpuGeometryBudgetExceededException">Reclaiming drained blocks cannot admit the physical charge.</exception>
    private NativeBufferBlock EnsureBlocksThrough(int requiredBlockIndex)
    {
        // Grow application metadata before Shared can reserve or allocate a native child.
        var count = checked(requiredBlockIndex + 1);
        _pendingBlockCopies.EnsureCapacity(count);
        while (_pendingBlockCopies.Count < count)
            _pendingBlockCopies.Add(0);

        var usableBytes = _allocator.BlockSizeOf(requiredBlockIndex);
        try
        {
            return _blocks.EnsureBlock(requiredBlockIndex, usableBytes);
        }
        catch (NativeBufferBudgetExceededException)
        {
            // Eviction policy and proof remain application-owned: only drained ranges and copies
            // can release a block. Preserve the existing structured pressure result for callers.
            ReleaseEmptyBlocks();
            try
            {
                return _blocks.EnsureBlock(requiredBlockIndex, usableBytes);
            }
            catch (NativeBufferBudgetExceededException pressure)
            {
                throw new GpuGeometryBudgetExceededException(pressure.RequestedBytes, pressure.MaximumBytes);
            }
        }
    }

    /// <summary>Writes the two packed streams into either mapped ring or transient staging memory.</summary>
    private static void WriteStreams(
        byte* destination,
        ReadOnlySpan<byte> vertexBytes,
        long alignedVertexBytes,
        ReadOnlySpan<byte> indexBytes)
    {
        vertexBytes.CopyTo(new Span<byte>(destination, vertexBytes.Length));
        if (indexBytes.Length > 0)
        {
            indexBytes.CopyTo(new Span<byte>(destination + alignedVertexBytes, indexBytes.Length));
        }
    }

    /// <summary>Calculates the exact attributed arena range before either native allocation or residency admission.</summary>
    /// <param name="vertexBytes">Positive vertex stream length before 16-byte index-offset padding.</param>
    /// <param name="indexBytes">Nonnegative index stream length before final range padding.</param>
    /// <returns>The final 16-byte-aligned allocation charge, excluding separately owned block and staging overhead.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The vertex length is not positive or the index length is negative.</exception>
    /// <exception cref="OverflowException">Either alignment or the combined streams exceed a signed 64-bit byte count.</exception>
    internal static long CalculateAllocationBytes(long vertexBytes, long indexBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vertexBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(indexBytes);
        var alignedVertexBytes = AlignUp(vertexBytes, RegionAlignment);
        return AlignUp(checked(alignedVertexBytes + indexBytes), RegionAlignment);
    }

    /// <summary>Rejects allocator-policy drift before backing or copying a range with an incorrectly admitted charge.</summary>
    /// <param name="allocation">New allocator range, still covered by the upload's rollback.</param>
    /// <param name="expectedBytes">Pure calculation used for pre-allocation admission.</param>
    /// <exception cref="InvalidOperationException">The allocator's actual aligned range differs from the admission charge.</exception>
    private static void VerifyAllocationCharge(in ByteArenaAllocation allocation, long expectedBytes)
    {
        if (allocation.AlignedSize != expectedBytes)
            throw new InvalidOperationException("Geometry allocator charge differs from the admitted aligned byte count.");
    }

    /// <summary>Rounds a nonnegative byte count to a power-of-two boundary without allowing arithmetic wrap.</summary>
    /// <param name="value">Byte count validated by the caller.</param>
    /// <param name="alignment">Positive power-of-two alignment.</param>
    /// <returns>The aligned count.</returns>
    /// <exception cref="OverflowException">The alignment addition exceeds a signed 64-bit byte count.</exception>
    private static long AlignUp(long value, int alignment)
    {
        return checked(value + (alignment - 1)) & ~((long)alignment - 1);
    }

    private void MarkCopyPending(int blockIndex)
    {
        var blockPending = checked(_pendingBlockCopies[blockIndex] + 1);
        var totalPending = checked(PendingCopyCount + 1);
        _pendingBlockCopies[blockIndex] = blockPending;
        PendingCopyCount = totalPending;
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
        PendingCopyCount--;
        AdvanceReclamationGeneration();
    }

    /// <summary>
    ///     The sole allocator-free path. Generation advances after, never before, the allocator
    ///     accepts the free; strict-validation rejection therefore cannot signal phantom work.
    /// </summary>
    private void FreeAllocation(in ByteArenaAllocation allocation)
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

    /// <summary>Rejects cross-thread mutations before either allocator or Shared backing ownership can change.</summary>
    /// <exception cref="InvalidOperationException">The caller is not the creating render thread.</exception>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Geometry arena lifetime belongs to its creating render thread.");
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

            arena.Free(allocation);
            _freed = true;
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
        ByteArenaAllocation allocation,
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

    internal ByteArenaAllocation Allocation { get; }

    public ulong VertexBufferLocation { get; }

    public ulong IndexBufferLocation { get; }

    public uint VertexBytes { get; }

    public uint IndexBytes { get; }

    public string? DebugTag { get; }
}
