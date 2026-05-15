using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Terrain;

internal static class RuntimeTerrainColorExtractor
{
    public static byte[]? ExtractVclr(RuntimeTerrainMesh mesh)
    {
        return mesh.ToLandVertexColorBytes();
    }
}
