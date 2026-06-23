using BethesdaMultitool.Core.Formats.Esm.Terrain;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Heightmap;

internal static class HeightmapExportConstants
{
    internal const int LandVertexCount = TerrainConstants.LandGridSize;
    internal const int LandCellStride = TerrainConstants.LandQuadCount;
    internal const float VhgtQuantizationUnits = 8f;
    internal const float GrayscaleBucketUnits = VhgtQuantizationUnits * 256f;
}
