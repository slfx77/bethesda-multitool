using BethesdaMultitool.Core.Diagnostics;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     One terrain cell's sub-allocation: the vertex stream followed by the (16-aligned) per-vertex
///     blend-weight stream, both inside one arena range. The GPU addresses are bound directly as
///     vertex-buffer views on slots 0 and 1.
/// </summary>
internal readonly record struct TerrainAllocation12(
    ArenaAllocation Allocation,
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
///         Fallout/Oblivion/Skyrim a cell asks for 148,104 bytes and is charged 262,144 — <b>43% of
///         every terrain cell is padding</b>. Sub-allocating from 16 MiB blocks pays the rounding
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

    private readonly GeometryArenaAllocator _allocator;
    private readonly List<ID3D12Resource> _blockResources = new();
    private readonly GpuDevice12 _gpu;
    private readonly GpuTerrainStagingRing12 _stagingRing;
    private long _committedBytes;
    private bool _disposed;
    private ResourceRegistration? _registration;

    public GpuTerrainArena12(GpuDevice12 gpu, long blockSize = DefaultBlockSize)
    {
        _gpu = gpu;
        _allocator = new GeometryArenaAllocator(blockSize)
        {
            StrictValidation = GeometryArenaDiagnostics.Enabled
        };
        _stagingRing = new GpuTerrainStagingRing12(gpu);
    }

    /// <summary>Arena blocks currently committed.</summary>
    public int BlockCount => _blockResources.Count;

    /// <summary>Live sub-allocated bytes (excludes per-block rounding and free-list holes).</summary>
    public long AllocatedBytes => _allocator.AllocatedBytes;

    /// <summary>The shared staging buffer cell uploads copy through. Exposed for diagnostics.</summary>
    public GpuTerrainStagingRing12 StagingRing => _stagingRing;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration?.Dispose();
        _registration = null;
        _stagingRing.Dispose();
        foreach (var block in _blockResources)
        {
            block.Dispose();
        }

        _blockResources.Clear();
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
            EstimatedBytes = _committedBytes,
            EntryCount = _blockResources.Count,
            Segment = GpuMemorySegment.Local
        };
    }

    public GpuTerrainArena12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        // The staging ring gets its own row rather than folding into this one: its useful signal is
        // an overflow RATE, which cannot be expressed as a share of the arena's bytes.
        _stagingRing.RegisterWith(registry, instanceTag);
        return this;
    }

    /// <summary>
    ///     Packs both streams into one range and copies them in through a transient staging buffer
    ///     retired on <paramref name="deletionQueue" />. Must be called during the frame's upload
    ///     phase, before any terrain draw on <paramref name="cmd" />.
    /// </summary>
    public TerrainAllocation12 Upload(
        ID3D12GraphicsCommandList cmd,
        GpuDeletionQueue12 deletionQueue,
        ReadOnlySpan<byte> vertexBytes,
        ReadOnlySpan<byte> blendBytes,
        string? debugTag = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (vertexBytes.Length == 0)
        {
            throw new ArgumentException("Refusing to upload a cell with no vertices.", nameof(vertexBytes));
        }

        var alignedVertexBytes = (int)AlignUp(vertexBytes.Length, RegionAlignment);
        // Same function TerrainCellResidencyPolicy predicts with, so the planned byte budget and the
        // bytes actually charged cannot drift apart.
        var totalBytes = (int)GpuResourceFootprint.ArenaSubAllocationBytes(
            vertexBytes.Length, blendBytes.Length);

        var allocation = _allocator.Allocate(totalBytes);
        ID3D12Resource stagingResource;
        ulong stagingOffset;
        IDisposable stagingRelease;
        ID3D12Resource? transientStaging = null;
        try
        {
            EnsureBlocksThrough(allocation.BlockIndex);

            // Everything that can throw happens in this block, and the ring reservation is its last
            // step. That ordering is deliberate: a region taken from the ring and then abandoned by
            // an exception could only be handed back out of order, and FIFO release is the shared
            // staging ring's load-bearing invariant.
            if (_stagingRing.TryReserve(totalBytes, out var region))
            {
                stagingResource = region.Resource;
                stagingOffset = region.Offset;
                stagingRelease = region.Release;
                WriteStreams((byte*)region.CpuPtr, vertexBytes, alignedVertexBytes, blendBytes);
            }
            else
            {
                // Ring full, or this grid's cells are larger than the ring will ever serve: stage
                // through a one-shot committed buffer, exactly as every upload did before the ring.
                transientStaging = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                    HeapProperties.UploadHeapProperties,
                    HeapFlags.None,
                    ResourceDescription.Buffer((ulong)totalBytes),
                    ResourceStates.GenericRead);

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
                stagingOffset = 0;
                stagingRelease = transientStaging;
            }
        }
        catch
        {
            // The allocator already reserved the range; hand it back or AllocatedBytes drifts and
            // the span leaks as permanently allocated. Commit failure under memory pressure is
            // expected (E_OUTOFMEMORY was observed on the geometry arena 2026-08-12) and a later
            // upload retries once pressure eases.
            transientStaging?.Dispose();
            _allocator.Free(allocation);
            throw;
        }

        var block = _blockResources[allocation.BlockIndex];
        // Block is in COMMON, so the copy implicitly promotes it to COPY_DEST; the barrier returns
        // it to COMMON, which both restores the resting state for the next upload and makes this
        // write visible to the draws that follow.
        cmd.CopyBufferRegion(block, (ulong)allocation.Offset, stagingResource, stagingOffset, (ulong)totalBytes);
        cmd.ResourceBarrierTransition(block, ResourceStates.CopyDest, ResourceStates.Common);
        // Same queue for both staging kinds: a transient buffer is disposed, a ring region is handed
        // back. Either way it happens only once the fence proves this copy has drained.
        deletionQueue.EnqueueDispose(stagingRelease);

        var gpuBase = block.GPUVirtualAddress + (ulong)allocation.Offset;
        return new TerrainAllocation12(
            allocation,
            gpuBase,
            gpuBase + (ulong)alignedVertexBytes,
            (uint)vertexBytes.Length,
            (uint)blendBytes.Length,
            debugTag);
    }

    /// <summary>Returns a range to the free-list. No-op after <see cref="Dispose" />.</summary>
    public void Free(TerrainAllocation12 allocation)
    {
        if (_disposed)
        {
            return;
        }

        _allocator.Free(allocation.Allocation);
    }

    /// <summary>
    ///     A disposable that frees <paramref name="allocation" /> when disposed. Enqueue it on the
    ///     <see cref="GpuDeletionQueue12" /> so the range is reclaimed only after in-flight draws
    ///     referencing it have drained — a range recycled too early would be overwritten by the next
    ///     cell while the GPU was still reading the evicted one.
    /// </summary>
    public IDisposable DeferredFreeHandle(TerrainAllocation12 allocation) => new FreeHandle(this, allocation);

    /// <summary>
    ///     Commits blocks in order up to and including <paramref name="requiredBlockIndex" /> —
    ///     never "through the allocator's full count", so a tail block whose commit failed under
    ///     memory pressure cannot hold hostage an upload landing in an already-backed earlier block.
    /// </summary>
    private void EnsureBlocksThrough(int requiredBlockIndex)
    {
        while (_blockResources.Count <= requiredBlockIndex)
        {
            var blockBytes = _allocator.BlockSizeOf(_blockResources.Count);
            // Buffers are always created in COMMON — D3D12 ignores any other initial state and the
            // debug layer warns if one is supplied.
            var resource = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)blockBytes),
                ResourceStates.Common);
            _blockResources.Add(resource);
            _committedBytes += blockBytes;
        }
    }

    /// <summary>
    ///     Lays both streams out in the staging memory exactly as the arena range expects them: the
    ///     vertex stream at the start, the blend-weight stream at the 16-aligned boundary after it.
    ///     The gap is left untouched — it is padding the GPU never reads, and zeroing it would cost
    ///     a second pass over every cell.
    /// </summary>
    private static void WriteStreams(
        byte* destination, ReadOnlySpan<byte> vertexBytes, int alignedVertexBytes, ReadOnlySpan<byte> blendBytes)
    {
        vertexBytes.CopyTo(new Span<byte>(destination, vertexBytes.Length));
        if (blendBytes.Length > 0)
        {
            blendBytes.CopyTo(new Span<byte>(destination + alignedVertexBytes, blendBytes.Length));
        }
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~((long)alignment - 1);

    private sealed class FreeHandle(GpuTerrainArena12 arena, TerrainAllocation12 allocation) : IDisposable
    {
        private bool _freed;

        public void Dispose()
        {
            if (_freed)
            {
                return;
            }

            _freed = true;
            arena.Free(allocation);
        }
    }
}
