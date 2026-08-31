#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     CPU-only product of a background cell build. Holds freshly-allocated per-task arrays (so
///     concurrent builds never share scratch) and the resolved texture set — but NO
///     <c>ID3D12Resource</c>; GPU buffers are created on the render thread in
///     <c>TerrainRenderer12.UploadBuiltCell</c>. <see cref="Generation" /> tags the worldspace the
///     build ran against so <c>TerrainRenderer12.StoreBuildResult</c> can drop results from a stale
///     LoadData.
/// </summary>
/// <param name="BlendQuadCount">
///     Layer-weight quads <see cref="BlendWeights" /> was sized to — <c>ceil(ActiveSlotCount / 4)</c>.
///     Selects both the shader permutation and the input layout at upload, so it has to travel with
///     the array rather than be re-derived: re-deriving it from <see cref="TextureSet" /> on the
///     render thread would let a future change to the sizing rule disagree with the bytes already
///     written, which reads back as every vertex's weights shifted by one quad.
/// </param>
internal sealed record BuiltCellCpuData(
    TerrainVertex[]? Vertices,
    ushort[]? BlendWeights,
    int BlendQuadCount,
    CellTerrainTextureSet? TextureSet,
    TerrainCellGrid Grid,
    TerrainCellHeightBounds HeightBounds,
    bool Unusable,
    int Generation)
{
    public static BuiltCellCpuData Failed(int generation) =>
        new(null, null, 0, null, default, TerrainCellHeightBounds.Invalid, true, generation);
}
#endif
