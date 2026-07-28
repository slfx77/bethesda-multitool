#if WINDOWS_GUI
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
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
    public required ID3D12Resource VertexBuffer { get; init; }
    public required ID3D12Resource BlendWeightBuffer { get; init; }
    public required TerrainTextureIndices TextureIndices { get; init; }
    /// <summary>
    ///     Resolver-owned entries retained only so each draw observes placeholder → resident metadata
    ///     promotion. In particular, an Xbox ATI2/BC5 upload changes <c>NormalDecodeMode</c> after the
    ///     cell's stable bindless indices have already been cached.
    /// </summary>
    public required GpuTextureCache12.Entry?[]? NormalTextureEntries { get; init; }
    public required GpuDeletionQueue12 DeletionQueue { get; init; }

    // Route through the deletion queue so LRU eviction can't release a buffer that the
    // GPU is still consuming from the previous frame's command list. Textures are owned by
    // TerrainTextureResolver12 and referenced through stable bindless indices.
    public void Dispose()
    {
        DeletionQueue.EnqueueDispose(VertexBuffer);
        DeletionQueue.EnqueueDispose(BlendWeightBuffer);
    }
}
#endif
