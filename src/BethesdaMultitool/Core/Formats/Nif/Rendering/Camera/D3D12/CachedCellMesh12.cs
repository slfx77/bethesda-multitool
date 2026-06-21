#if WINDOWS_GUI
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Per-cell bindless diffuse texture indices for the 16 blend slots, laid out as
///     <c>uint4[4]</c> to match the terrain fragment shader's <c>uTextureIndices[4]</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TerrainTextureIndices
{
    public fixed uint Index[CellTerrainTextureSet.MaxSlots];
}

internal sealed class CachedCellMesh12 : IDisposable
{
    public required ID3D12Resource VertexBuffer { get; init; }
    public required ID3D12Resource BlendWeightBuffer { get; init; }
    public required TerrainTextureIndices TextureIndices { get; init; }
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
