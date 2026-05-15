using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Terrain;

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
