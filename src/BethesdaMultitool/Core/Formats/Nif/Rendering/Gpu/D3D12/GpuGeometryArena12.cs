using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A sub-allocating geometry arena for static reference meshes. Replaces the per-mesh
///     committed-resource + staging-buffer + copy + barrier pattern
///     (<see cref="GpuMeshBufferFactory12.CreateDefaultBuffer{T}" />) — the committed-resource churn
///     the profiler flagged — with a handful of large, persistently-mapped UPLOAD-heap blocks that
///     each mesh's vertices and indices are <c>memcpy</c>'d into. UPLOAD-heap static vertex/index
///     buffers are an accepted pattern here (see <see cref="GpuMeshBufferFactory12" />) and the ring
///     buffer already binds them directly; the block stays in <see cref="ResourceStates.GenericRead" />
///     for life and only freshly-allocated (un-referenced) sub-ranges are ever written, so there is
///     no state transition and no read/write hazard.
///     <para>
///         Render-thread only: <see cref="Upload" /> on mesh upload, <see cref="Free" /> on eviction
///         (deferred through the deletion queue so in-flight draws drain first).
///     </para>
/// </summary>
internal sealed unsafe class GpuGeometryArena12 : ITrackableResource, IDisposable
{
    /// <summary>16 MB per standard block. A handful of monolithic meshes exceed it (RepBay.NIF and
    /// B29_RiseAnim.NIF run ~25 MB) — those get a dedicated block sized to the allocation.</summary>
    public const long DefaultBlockSize = 16L * 1024L * 1024L;

    private const int RegionAlignment = 16;

    private readonly GpuDevice12 _gpu;
    private readonly GeometryArenaAllocator _allocator;
    private readonly List<ID3D12Resource> _blockResources = new();
    private readonly List<IntPtr> _blockPointers = new();
    private long _committedBytes;
    private ResourceRegistration? _registration;
    private bool _disposed;

    public GpuGeometryArena12(GpuDevice12 gpu, long blockSize = DefaultBlockSize)
    {
        _gpu = gpu;
        _allocator = new GeometryArenaAllocator(blockSize, RegionAlignment)
        {
            // FALLOUT_VIEWER_GEOMETRY_VALIDATE: double-free / overlap throws at the offending
            // call site and QueryLiveness can distinguish freed from recycled ranges.
            StrictValidation = GeometryArenaDiagnostics.Enabled,
        };
    }

    /// <summary>Arena blocks currently committed.</summary>
    public int BlockCount => _blockResources.Count;

    public string ResourceName => nameof(GpuGeometryArena12);

    public ResourceCategory Category => ResourceCategory.GpuResident;

    /// <summary>
    ///     Tracking-only conformance: bytes = committed UPLOAD-heap blocks; entries = block count.
    ///     The arena is never trimmed — allocations are owned by live meshes and reclaimed through
    ///     the mesh LRU's eviction cascade.
    /// </summary>
    public ResourceStats GetStats() => new()
    {
        EstimatedBytes = _committedBytes,
        EntryCount = _blockResources.Count,
    };

    /// <summary>
    ///     Registers the arena with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the arena for fluent construction.
    /// </summary>
    public GpuGeometryArena12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
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
        if (vertexBytes.Length == 0)
            throw new ArgumentException("Refusing to upload zero vertices.", nameof(vertexBytes));

        var alignedVertexBytes = (int)AlignUp(vertexBytes.Length, RegionAlignment);
        var totalBytes = alignedVertexBytes + indexBytes.Length;

        var allocation = _allocator.Allocate(totalBytes);
        EnsureBlocks();

        var cpuBase = (byte*)_blockPointers[allocation.BlockIndex] + allocation.Offset;
        vertexBytes.CopyTo(new Span<byte>(cpuBase, vertexBytes.Length));
        if (indexBytes.Length > 0)
        {
            indexBytes.CopyTo(new Span<byte>(cpuBase + alignedVertexBytes, indexBytes.Length));
        }

        EmitAudit("alloc", allocation, debugTag);
        var gpuBase = _blockResources[allocation.BlockIndex].GPUVirtualAddress + (ulong)allocation.Offset;
        return new GeometryAllocation12(
            allocation,
            vertexBufferLocation: gpuBase,
            indexBufferLocation: gpuBase + (ulong)alignedVertexBytes,
            vertexBytes: (uint)vertexBytes.Length,
            indexBytes: (uint)indexBytes.Length,
            debugTag: debugTag);
    }

    /// <summary>Returns an allocation's range to the free-list. No-op after <see cref="Dispose" />.</summary>
    public void Free(GeometryAllocation12 allocation)
    {
        if (_disposed)
        {
            return;
        }

        EmitAudit("free", allocation.Allocation, allocation.DebugTag);
        _allocator.Free(allocation.Allocation);
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

    /// <summary>Liveness of <paramref name="allocation" />'s range under strict tracking
    /// (<see cref="ArenaLiveness.Untracked" /> when FALLOUT_VIEWER_GEOMETRY_VALIDATE is off).</summary>
    public ArenaLiveness QueryLiveness(in GeometryAllocation12 allocation) =>
        _disposed ? ArenaLiveness.Untracked : _allocator.QueryLiveness(allocation.Allocation);

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
        if (_disposed || (uint)blockIndex >= (uint)_blockResources.Count)
        {
            return false;
        }

        var blockBase = _blockResources[blockIndex].GPUVirtualAddress;
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
            ["tag"] = tag,
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration?.Dispose();
        _registration = null;
        for (var i = 0; i < _blockResources.Count; i++)
        {
            _blockResources[i].Unmap(0, null);
            _blockResources[i].Dispose();
        }

        _blockResources.Clear();
        _blockPointers.Clear();
    }

    private void EnsureBlocks()
    {
        while (_blockResources.Count < _allocator.BlockCount)
        {
            // Per-block size: standard blocks share _blockSize; an oversized mesh's dedicated
            // block is exactly as large as the allocation that forced it.
            var blockBytes = _allocator.BlockSizeOf(_blockResources.Count);
            var resource = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)blockBytes),
                ResourceStates.GenericRead,
                optimizedClearValue: null);

            void* mapped = null;
            resource.Map(0, &mapped).CheckError();
            _blockResources.Add(resource);
            _blockPointers.Add((IntPtr)mapped);
            _committedBytes += blockBytes;
        }
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~((long)alignment - 1);

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
