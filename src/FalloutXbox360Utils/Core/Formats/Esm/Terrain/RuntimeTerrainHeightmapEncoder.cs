using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Terrain;

internal static class RuntimeTerrainHeightmapEncoder
{
    public static LandHeightmap Encode(RuntimeTerrainMesh mesh, float baseHeight = 0f)
    {
        return mesh.ToLandHeightmap(baseHeight);
    }
}
