#if WINDOWS_GUI
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Per-cell bindless texture indices for the 16 blend slots. The original diffuse
///     <c>uint4[4]</c> remains first (and therefore ABI-stable), followed by the FNV normal-map
///     <c>uint4[4]</c> and one <c>uint4</c> of decode metadata. A normal index of
///     <c>uint.MaxValue</c> means exact flat/identity; metadata x is the 16-slot BC5 mask.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TerrainTextureIndices
{
    public fixed uint Index[CellTerrainTextureSet.MaxSlots];
    public fixed uint NormalIndex[CellTerrainTextureSet.MaxSlots];
    public fixed uint NormalDecodeMetadata[4];
}

internal sealed class CachedCellMesh12 : IDisposable
{
    /// <summary>
    ///     This cell's range inside <see cref="GpuTerrainArena12" />: both stream GPU addresses and
    ///     their sizes. The cell no longer owns <c>ID3D12Resource</c>s — it owns a sub-allocation of
    ///     a shared 16 MiB block, which is what removes the per-cell 64 KiB alignment padding (43% of
    ///     a 33-grid cell).
    /// </summary>
    public required TerrainAllocation12 Geometry { get; init; }

    /// <summary>
    ///     Where this cell's vertex grid sits in the world, as the builder laid it out. Load-bearing
    ///     rather than informational: <see cref="TerrainVertex" /> stores only heights, so the draw
    ///     call must hand these four numbers to the vertex shader as root constants or the cell has
    ///     no horizontal position at all. Carried from the build instead of re-derived from the cell
    ///     key, so the renderer cannot disagree with the geometry about the cell size.
    /// </summary>
    public required TerrainCellGrid Grid { get; init; }

    public required TerrainTextureIndices TextureIndices { get; init; }

    /// <summary>
    ///     Resolver-owned entries retained only so each draw observes placeholder → resident metadata
    ///     promotion. In particular, an Xbox ATI2/BC5 upload changes <c>NormalDecodeMode</c> after the
    ///     cell's stable bindless indices have already been cached.
    /// </summary>
    public required GpuTextureCache12.Entry?[]? NormalTextureEntries { get; init; }

    public required GpuDeletionQueue12 DeletionQueue { get; init; }

    /// <summary>The arena that owns <see cref="Geometry" />, for the deferred free on eviction.</summary>
    public required GpuTerrainArena12 Arena { get; init; }

    /// <summary>
    ///     GPU bytes this cell costs the arena: its aligned sub-allocation. Feeds the cell LRU's byte
    ///     budget. Note this is now the SUB-ALLOCATION size, not two 64 KiB-rounded committed
    ///     buffers — the arena pays that rounding once per block instead of twice per cell, so the
    ///     same worldspace now fits far more resident terrain in the same budget.
    /// </summary>
    public long ByteSize => Geometry.Allocation.AlignedSize;

    /// <summary>
    ///     Vertex-buffer view for stream slot 0 (positions/normals/colour).
    /// </summary>
    public VertexBufferView VertexView(uint stride) => new()
    {
        BufferLocation = Geometry.VertexGpuAddress,
        SizeInBytes = Geometry.VertexBytes,
        StrideInBytes = stride
    };

    /// <summary>Vertex-buffer view for stream slot 1 (per-vertex layer blend weights).</summary>
    public VertexBufferView BlendWeightView(uint stride) => new()
    {
        BufferLocation = Geometry.BlendGpuAddress,
        SizeInBytes = Geometry.BlendBytes,
        StrideInBytes = stride
    };

    // Route the arena range through the deletion queue so LRU eviction can't recycle bytes the GPU
    // is still consuming from the previous frame's command list — a range reused too early would be
    // overwritten by the next cell mid-read. Textures are owned by TerrainTextureResolver12 and
    // referenced through stable bindless indices.
    public void Dispose()
    {
        DeletionQueue.EnqueueDispose(Arena.DeferredFreeHandle(Geometry));
    }
}
#endif
