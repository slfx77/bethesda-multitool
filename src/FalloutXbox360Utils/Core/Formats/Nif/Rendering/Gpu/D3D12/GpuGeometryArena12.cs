using Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

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
internal sealed unsafe class GpuGeometryArena12 : IDisposable
{
    /// <summary>16 MB per block — comfortably larger than any single reference mesh, few blocks.</summary>
    public const long DefaultBlockSize = 16L * 1024L * 1024L;

    private const int RegionAlignment = 16;

    private readonly GpuDevice12 _gpu;
    private readonly GeometryArenaAllocator _allocator;
    private readonly List<ID3D12Resource> _blockResources = new();
    private readonly List<IntPtr> _blockPointers = new();
    private bool _disposed;

    public GpuGeometryArena12(GpuDevice12 gpu, long blockSize = DefaultBlockSize)
    {
        _gpu = gpu;
        _allocator = new GeometryArenaAllocator(blockSize, RegionAlignment);
    }

    /// <summary>Arena blocks currently committed.</summary>
    public int BlockCount => _blockResources.Count;

    /// <summary>
    ///     Packs <paramref name="vertexBytes" /> then (alignment-padded) <paramref name="indexBytes" />
    ///     into one sub-allocation and copies both into the mapped block. The returned GPU virtual
    ///     addresses are bound directly as vertex / index buffer views.
    /// </summary>
    public GeometryAllocation12 Upload(ReadOnlySpan<byte> vertexBytes, ReadOnlySpan<byte> indexBytes)
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

        var gpuBase = _blockResources[allocation.BlockIndex].GPUVirtualAddress + (ulong)allocation.Offset;
        return new GeometryAllocation12(
            allocation,
            vertexBufferLocation: gpuBase,
            indexBufferLocation: gpuBase + (ulong)alignedVertexBytes,
            vertexBytes: (uint)vertexBytes.Length,
            indexBytes: (uint)indexBytes.Length);
    }

    /// <summary>Returns an allocation's range to the free-list. No-op after <see cref="Dispose" />.</summary>
    public void Free(GeometryAllocation12 allocation)
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
    ///     referencing it have drained.
    /// </summary>
    public IDisposable DeferredFreeHandle(GeometryAllocation12 allocation) => new FreeHandle(this, allocation);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
            var resource = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)_allocator.BlockSize),
                ResourceStates.GenericRead,
                optimizedClearValue: null);

            void* mapped = null;
            resource.Map(0, &mapped).CheckError();
            _blockResources.Add(resource);
            _blockPointers.Add((IntPtr)mapped);
        }
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~((long)alignment - 1);

    private sealed class FreeHandle(GpuGeometryArena12 arena, GeometryAllocation12 allocation) : IDisposable
    {
        public void Dispose() => arena.Free(allocation);
    }
}

/// <summary>
///     A geometry sub-allocation. <see cref="VertexBufferLocation" /> / <see cref="IndexBufferLocation" />
///     are GPU virtual addresses bound directly as <see cref="VertexBufferView" /> /
///     <see cref="IndexBufferView" /> base locations; <see cref="Allocation" /> carries the range
///     back to <see cref="GpuGeometryArena12.Free" />.
/// </summary>
internal readonly struct GeometryAllocation12
{
    internal GeometryAllocation12(
        ArenaAllocation allocation,
        ulong vertexBufferLocation,
        ulong indexBufferLocation,
        uint vertexBytes,
        uint indexBytes)
    {
        Allocation = allocation;
        VertexBufferLocation = vertexBufferLocation;
        IndexBufferLocation = indexBufferLocation;
        VertexBytes = vertexBytes;
        IndexBytes = indexBytes;
    }

    internal ArenaAllocation Allocation { get; }

    public ulong VertexBufferLocation { get; }

    public ulong IndexBufferLocation { get; }

    public uint VertexBytes { get; }

    public uint IndexBytes { get; }
}
