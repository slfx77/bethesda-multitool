using BethesdaMultitool.Core.Diagnostics;
using Vortice.Direct3D12;
using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     One terrain cell's sub-allocation: the vertex stream followed by the (16-aligned) per-vertex
///     blend-weight stream, both inside one arena range. The GPU addresses are bound directly as
///     vertex-buffer views on slots 0 and 1.
/// </summary>
internal readonly record struct TerrainAllocation12(
    ByteArenaAllocation Allocation,
    ulong VertexGpuAddress,
    ulong BlendGpuAddress,
    uint VertexBytes,
    uint BlendBytes,
    string? DebugTag);

/// <summary>
///     Sub-allocating arena for per-cell terrain geometry, replacing two committed DEFAULT-heap
///     buffers plus two staging buffers plus a copy plus two barriers <b>per cell</b>.
///     <para>
///         The reason this matters is not resource count but <b>alignment waste</b>. D3D12 rounds
///         every committed buffer up to 64 KiB, and a terrain cell's two streams are small enough
///         that the padding is a large fraction of the whole: at the 33×33 grid used by
///         Fallout/Oblivion/Skyrim a cell asks for 148,104 bytes and is charged 262,144 —
///         <b>
///             43% of
///             every terrain cell is padding
///         </b>
///         . Sub-allocating from 16 MiB blocks pays the rounding
///         once per block instead of twice per cell. At Fallout 76's 129×129 grid the same waste is
///         ~94 KiB/cell, which is ~3.9 GiB across Appalachia.
///     </para>
///     <para>
///         <b>DEFAULT heap, unlike <see cref="GpuGeometryArena12" />'s UPLOAD blocks.</b> Terrain is
///         re-read by the input assembler every frame across the colour, depth-only, shadow-cascade
///         and mirror passes, so it belongs in device-local memory; reference geometry tolerates the
///         UPLOAD heap because it is read far less repeatedly. The cost of that choice is that
///         blocks cannot be persistently mapped, so uploads go through staging and a
///         <c>CopyBufferRegion</c> — but through the shared, persistently-mapped
///         <see cref="GpuTerrainStagingRing12" /> rather than a freshly committed buffer per cell,
///         so the copy remains and the per-cell allocation does not.
///     </para>
///     <para>
///         <b>Blocks rest in <see cref="ResourceStates.Common" />.</b> Buffers are implicitly
///         promoted out of COMMON to <c>COPY_DEST</c> for a copy and to any read state for a draw,
///         and decay back at <c>ExecuteCommandLists</c>. Returning each block to COMMON after its
///         copy — rather than to <c>VERTEX_AND_CONSTANT_BUFFER</c> as a single-use buffer would —
///         is what keeps this stateless: a SECOND cell uploading into the same block in the same
///         command list would otherwise need an explicit read→copy transition, and tracking that
///         per block is exactly the kind of bookkeeping that silently corrupts geometry when it
///         drifts. The transition back also provides the write→read visibility the following draws
///         depend on.
///     </para>
///     <para>
///         Render-thread only, like the geometry arena: <see cref="Upload" /> during the frame's
///         upload phase, <see cref="DeferredFreeHandle" /> on eviction so a range is reclaimed only
///         after in-flight draws referencing it have drained.
///     </para>
/// </summary>
internal sealed unsafe class GpuTerrainArena12 : ITrackableResource, IDisposable
{
    /// <summary>16 MiB per block — ~7 cells at the 129 grid, ~113 at the 33 grid.</summary>
    public const long DefaultBlockSize = 16L * 1024L * 1024L;

    /// <summary>
    ///     Sub-region alignment. 16 satisfies the 4-byte <c>CopyBufferRegion</c> destination-offset
    ///     requirement with margin and keeps both stream starts on a vector boundary.
    /// </summary>
    private const int RegionAlignment = GpuResourceFootprint.ArenaRegionAlignment;

    private readonly ByteArenaAllocator _allocator;
    private readonly NativeBufferBlocks _blocks;
    private readonly GpuDevice12 _gpu;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;
    private ResourceRegistration? _registration;
    private RetiredResourceDisposal? _retiredResources;

    /// <summary>Creates the terrain range allocator and lazy native owners without committing buffers.</summary>
    /// <param name="gpu">Borrowed device that outlives every terrain release.</param>
    /// <param name="blockSize">Default block extent; larger individual ranges use oversized blocks.</param>
    public GpuTerrainArena12(GpuDevice12 gpu, long blockSize = DefaultBlockSize)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;
        _allocator = new ByteArenaAllocator(blockSize)
        {
            StrictValidation = GeometryArenaDiagnostics.Enabled
        };
        // Preserve terrain's existing session-high-water policy. Application residency controls
        // live cell ranges; this extraction introduces no additional physical eviction or cap.
        _blocks = new NativeBufferBlocks(gpu.Device, HeapType.Default, long.MaxValue);
        StagingRing = new GpuTerrainStagingRing12(gpu);
    }

    /// <summary>Arena blocks currently committed.</summary>
    public int BlockCount => _blocks.AllocatedBlockCount;

    /// <summary>Live sub-allocated bytes (excludes per-block rounding and free-list holes).</summary>
    public long AllocatedBytes => _allocator.AllocatedBytes;

    /// <summary>The shared staging buffer cell uploads copy through. Exposed for diagnostics.</summary>
    public GpuTerrainStagingRing12 StagingRing { get; }

    /// <summary>Releases GPU-retired native backing, retaining failed releases for later disposal attempts.</summary>
    /// <remarks>The caller must first retire recorded copies and draws. This owner does not wait on the GPU.</remarks>
    public void Dispose()
    {
        VerifyAccess();
        if (_retiredResources is null)
        {
            var retired = new RetiredResourceDisposal();
            retired.Add(StagingRing, "terrain staging ring");
            retired.Add(_blocks, "terrain native backing");
            retired.Add(_registration, "terrain resource registration", 1);
            // Publish only after every child is retained. Failed admission leaves all original
            // fields intact; failed release leaves this complete owner available to retry.
            _retiredResources = retired;
            _disposed = true;
            _registration = null;
        }
        _retiredResources.Dispose();
    }

    public string ResourceName => nameof(GpuTerrainArena12);

    public ResourceCategory Category => ResourceCategory.GpuResident;

    /// <summary>
    ///     Committed DEFAULT-heap block bytes — genuinely device-local VRAM
    ///     (<see cref="GpuMemorySegment.Local" />), which is the whole point of not using the
    ///     UPLOAD-heap geometry arena for terrain. Monotonic: blocks are never released, so this is
    ///     the session's worst instantaneous demand; <see cref="AllocatedBytes" /> is the live figure.
    /// </summary>
    public ResourceStats GetStats()
    {
        return new ResourceStats
        {
            EstimatedBytes = _blocks.AllocatedBytes,
            EntryCount = _blocks.AllocatedBlockCount,
            Segment = GpuMemorySegment.Local
        };
    }

    /// <summary>Registers separate terrain backing and staging diagnostics until cleanup succeeds.</summary>
    /// <param name="registry">Registry that observes independently readable allocation statistics.</param>
    /// <param name="instanceTag">Optional world or viewer identity.</param>
    /// <returns>This arena for fluent initialization.</returns>
    public GpuTerrainArena12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        // The staging ring gets its own row rather than folding into this one: its useful signal is
        // an overflow RATE, which cannot be expressed as a share of the arena's bytes.
        StagingRing.RegisterWith(registry, instanceTag);
        return this;
    }

    /// <summary>
    ///     Packs both streams into one range and copies them through ring or transient staging
    ///     retired on <paramref name="deletionQueue" />. Must be called during the frame's upload
    ///     phase, before any terrain draw on <paramref name="cmd" />.
    ///     Retirement is queued before any allocation. The optional nonthrowing
    ///     <paramref name="retainAllocation" /> captures a provisional range with zero GPU addresses
    ///     before backing or recording; successful return transfers its cleanup to that caller,
    ///     including when a later upload step fails. A thrown callback leaves cleanup with the queue.
    /// </summary>
    public TerrainAllocation12 Upload(
        ID3D12GraphicsCommandList cmd,
        GpuDeletionQueue12 deletionQueue,
        ReadOnlySpan<byte> vertexBytes,
        ReadOnlySpan<byte> blendBytes,
        string? debugTag = null,
        Action<TerrainAllocation12>? retainAllocation = null)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(deletionQueue);
        if (vertexBytes.Length == 0)
        {
            throw new ArgumentException("Refusing to upload a cell with no vertices.", nameof(vertexBytes));
        }

        var alignedVertexBytes = AlignUp(vertexBytes.Length, RegionAlignment);
        // Same function TerrainCellResidencyPolicy predicts with, so the planned byte budget and the
        // bytes actually charged cannot drift apart.
        var totalBytes = GpuResourceFootprint.ArenaSubAllocationBytes(
            vertexBytes.Length, blendBytes.Length);

        // Terrain backing remains resident until arena shutdown, so it needs no separate copy
        // counter. The queue still owns every staging reservation and unpublished failed range.
        var rollback = new GpuGeometryUploadRetirement12(FreeAllocation,
            static _ => { /* Terrain blocks retire only with the arena. */ });
        deletionQueue.EnqueueDispose(rollback);
        try
        {
            var allocation = _allocator.Allocate(totalBytes);
            rollback.OwnAllocation(allocation);
            if (retainAllocation is not null)
            {
                retainAllocation(new TerrainAllocation12(allocation, 0, 0,
                    (uint)vertexBytes.Length, (uint)blendBytes.Length, debugTag));
                rollback.TransferAllocation();
            }
            var backing = EnsureBlocksThrough(allocation.BlockIndex);
            ID3D12Resource stagingResource;
            ulong stagingOffset = 0;
            if (StagingRing.TryReserve(totalBytes, out var region))
            {
                stagingResource = region.Resource;
                stagingOffset = region.Offset;
                rollback.OwnStaging(region.Release);
                WriteStreams((byte*)region.CpuPtr, vertexBytes, alignedVertexBytes, blendBytes);
            }
            else
            {
                // Ring full, or this grid's cells are larger than the ring will ever serve: stage
                // through a one-shot committed buffer, exactly as every upload did before the ring.
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
                    WriteStreams((byte*)cpuPtr, vertexBytes, alignedVertexBytes, blendBytes);
                }
                finally
                {
                    transientStaging.Unmap(0);
                }

                stagingResource = transientStaging;
            }
            var block = backing.Resource;
            // COMMON promotion and the explicit return preserve the existing stateless ordering
            // for multiple cell copies followed by draws within the same command list.
            cmd.CopyBufferRegion(block, (ulong)allocation.Offset, stagingResource, stagingOffset, (ulong)totalBytes);
            cmd.ResourceBarrierTransition(block, ResourceStates.CopyDest, ResourceStates.Common);
            var gpuBase = block.GPUVirtualAddress + (ulong)allocation.Offset;
            var result = new TerrainAllocation12(allocation, gpuBase, gpuBase + (ulong)alignedVertexBytes,
                (uint)vertexBytes.Length, (uint)blendBytes.Length, debugTag);
            rollback.TransferAllocation();
            return result;
        }
        finally { rollback.EndUpload(); }
    }

    /// <summary>Returns a range to the free-list. No-op after <see cref="Dispose" />.</summary>
    public void Free(TerrainAllocation12 allocation)
    {
        FreeAllocation(allocation.Allocation);
    }

    /// <summary>Returns an exact unpublished or retired range, with harmless late callbacks after GPU-idle shutdown.</summary>
    /// <param name="allocation">Owned range whose copies and draws have retired.</param>
    private void FreeAllocation(ByteArenaAllocation allocation)
    {
        VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _allocator.Free(allocation);
    }

    /// <summary>
    ///     A disposable that frees <paramref name="allocation" /> when disposed. Enqueue it on the
    ///     <see cref="GpuDeletionQueue12" /> so the range is reclaimed only after in-flight draws
    ///     referencing it have drained — a range recycled too early would be overwritten by the next
    ///     cell while the GPU was still reading the evicted one.
    /// </summary>
    public IDisposable DeferredFreeHandle(TerrainAllocation12 allocation)
    {
        return new FreeHandle(this, allocation);
    }

    /// <summary>
    ///     Backs only the requested stable slot, retaining partial initialization for retry.
    ///     Failure in a different block cannot prevent reuse of an already-backed block.
    /// </summary>
    /// <param name="requiredBlockIndex">Allocator-selected block with its fixed usable extent.</param>
    /// <returns>Borrowed DEFAULT-heap backing retained until GPU-idle arena shutdown.</returns>
    private NativeBufferBlock EnsureBlocksThrough(int requiredBlockIndex)
    {
        return _blocks.EnsureBlock(requiredBlockIndex, _allocator.BlockSizeOf(requiredBlockIndex));
    }

    /// <summary>
    ///     Lays both streams out in the staging memory exactly as the arena range expects them: the
    ///     vertex stream at the start, the blend-weight stream at the 16-aligned boundary after it.
    ///     The gap is left untouched — it is padding the GPU never reads, and zeroing it would cost
    ///     a second pass over every cell.
    /// </summary>
    private static void WriteStreams(
        byte* destination, ReadOnlySpan<byte> vertexBytes, long alignedVertexBytes, ReadOnlySpan<byte> blendBytes)
    {
        vertexBytes.CopyTo(new Span<byte>(destination, vertexBytes.Length));
        if (blendBytes.Length > 0)
        {
            blendBytes.CopyTo(new Span<byte>(destination + alignedVertexBytes, blendBytes.Length));
        }
    }

    /// <summary>Rounds a nonnegative size to the power-of-two alignment, rejecting arithmetic overflow.</summary>
    private static long AlignUp(long value, int alignment)
    {
        return checked(value + (alignment - 1L)) & ~((long)alignment - 1);
    }

    /// <summary>Rejects mutations outside the creating render thread before changing any ownership.</summary>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Terrain arena lifetime belongs to its creating render thread.");
    }

    /// <summary>Returns one retired terrain range once, retaining failed release attempts for retry.</summary>
    /// <param name="arena">The exact owner of the range.</param>
    /// <param name="allocation">The range whose GPU users have retired before disposal.</param>
    private sealed class FreeHandle(GpuTerrainArena12 arena, TerrainAllocation12 allocation) : IDisposable
    {
        /// <summary>Becomes true only after the arena accepts the range return.</summary>
        private bool _freed;

        /// <summary>Returns the range or retries a prior failed return without losing its ownership.</summary>
        public void Dispose()
        {
            if (_freed)
            {
                return;
            }

            arena.Free(allocation);
            _freed = true;
        }
    }
}
