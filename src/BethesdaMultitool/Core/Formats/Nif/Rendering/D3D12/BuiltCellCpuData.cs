#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
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
internal sealed record BuiltCellCpuData(
    GpuMeshUploader.GpuVertex[]? Vertices,
    Vector4[]? BlendWeights,
    CellTerrainTextureSet? TextureSet,
    bool Unusable,
    int Generation)
{
    public static BuiltCellCpuData Failed(int generation) => new(null, null, null, true, generation);
}
#endif
