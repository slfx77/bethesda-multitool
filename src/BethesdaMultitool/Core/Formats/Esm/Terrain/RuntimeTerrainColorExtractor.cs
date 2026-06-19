using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Terrain;

internal static class RuntimeTerrainColorExtractor
{
    public static byte[]? ExtractVclr(RuntimeTerrainMesh mesh)
    {
        return mesh.ToLandVertexColorBytes();
    }
}
