namespace FalloutXbox360Utils.Core.Formats.Esm.Terrain;

/// <summary>
///     Canonical Fallout/Gamebryo LAND terrain constants shared by runtime extraction,
///     LAND encoding, and PNG export.
/// </summary>
public static class TerrainConstants
{
    public const int LandGridSize = 33;
    public const int LandQuadCount = LandGridSize - 1;
    public const int LandVertexCount = LandGridSize * LandGridSize;
    public const float LandCellWorldSize = 4096f;
    public const float LandVertexSpacing = LandCellWorldSize / LandQuadCount;
    public const float LandLocalMin = -LandCellWorldSize * 0.5f;
    public const float LandLocalMax = LandCellWorldSize * 0.5f;
    public const float CanonicalVertexFitTolerance = LandVertexSpacing * 0.55f;
    public const float LocalBoundsTolerance = LandVertexSpacing * 1.5f;
}
