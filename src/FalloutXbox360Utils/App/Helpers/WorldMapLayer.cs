namespace FalloutXbox360Utils;

/// <summary>
///     Data layer shown by the 2D world map. Selected via the toolbar dropdown
///     in <see cref="WorldMapControl" /> and threaded through both the worldspace
///     overview bitmap and the per-cell detail bitmap.
/// </summary>
internal enum WorldMapLayer
{
    /// <summary>Default. Tinted grayscale heightmap derived from VHGT.</summary>
    Heightmap,

    /// <summary>Per-vertex RGB stored in VCLR (LandVisualData.VertexColors).</summary>
    VertexColors,

    /// <summary>Per-vertex winning LTEX FormID hashed to a distinct color (BTXT + ATXT/VTXT).</summary>
    TerrainRegions,

    /// <summary>Per-vertex winning LTEX sampled from the actual DDS via LTEX -> TXST -> BSA.</summary>
    TerrainTextures,

    /// <summary>Hillshade computed from the heightmap (Lambertian shading from a NW sun).</summary>
    Slope
}

internal static class WorldMapLayerExtensions
{
    public static string DisplayName(this WorldMapLayer layer) => layer switch
    {
        WorldMapLayer.Heightmap => "Heightmap",
        WorldMapLayer.VertexColors => "Vertex colors",
        WorldMapLayer.TerrainRegions => "Terrain regions",
        WorldMapLayer.TerrainTextures => "Terrain textures",
        WorldMapLayer.Slope => "Slope / hillshade",
        _ => layer.ToString()
    };
}
