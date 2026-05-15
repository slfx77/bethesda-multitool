using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

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

internal static class RuntimeTerrainSanitizationService
{
    public static RuntimeTerrainMesh Sanitize(RuntimeTerrainMesh mesh, float maxAbsZ = 20_000f)
    {
        return mesh.SanitizeVertices(maxAbsZ);
    }

    public static int CountGarbageZ(RuntimeTerrainMesh mesh, float maxAbsZ = 20_000f)
    {
        return mesh.CountGarbageZ(maxAbsZ);
    }
}

internal static class RuntimeTerrainDiagnosticService
{
    public static TerrainMeshDiagnostic DiagnoseQuality(
        RuntimeTerrainMesh mesh,
        int cellX = 0,
        int cellY = 0,
        uint formId = 0,
        float baseHeight = 0f)
    {
        return mesh.DiagnoseQuality(cellX, cellY, formId, baseHeight);
    }
}

internal static class RuntimeTerrainHeightmapEncoder
{
    public static LandHeightmap Encode(RuntimeTerrainMesh mesh, float baseHeight = 0f)
    {
        return mesh.ToLandHeightmap(baseHeight);
    }
}

internal static class RuntimeTerrainColorExtractor
{
    public static byte[]? ExtractVclr(RuntimeTerrainMesh mesh)
    {
        return mesh.ToLandVertexColorBytes();
    }
}
