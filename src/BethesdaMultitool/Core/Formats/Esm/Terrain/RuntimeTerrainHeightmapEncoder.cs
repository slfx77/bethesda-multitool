using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Terrain;

internal static class RuntimeTerrainHeightmapEncoder
{
    public static LandHeightmap Encode(RuntimeTerrainMesh mesh, float baseHeight = 0f)
    {
        return mesh.ToLandHeightmap(baseHeight);
    }
}
