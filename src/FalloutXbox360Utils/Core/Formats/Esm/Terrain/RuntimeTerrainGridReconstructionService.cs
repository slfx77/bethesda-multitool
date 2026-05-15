using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Terrain;

internal static class RuntimeTerrainGridReconstructionService
{
    public static RuntimeTerrainMesh.TerrainGridReconstruction? Reconstruct(RuntimeTerrainMesh mesh)
    {
        return mesh.TryReconstructHeightGrid();
    }

    public static bool[,]? GetCanonicalSourceCoverageMask(RuntimeTerrainMesh mesh)
    {
        return mesh.TryGetCanonicalSourceCoverageMask();
    }
}
