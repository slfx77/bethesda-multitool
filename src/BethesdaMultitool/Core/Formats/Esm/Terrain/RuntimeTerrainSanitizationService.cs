using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Terrain;

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
