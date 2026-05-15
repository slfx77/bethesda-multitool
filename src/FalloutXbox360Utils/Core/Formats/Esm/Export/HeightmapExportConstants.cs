using FalloutXbox360Utils.Core.Formats.Esm.Terrain;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class HeightmapExportConstants
{
    internal const int LandVertexCount = TerrainConstants.LandGridSize;
    internal const int LandCellStride = TerrainConstants.LandQuadCount;
    internal const float VhgtQuantizationUnits = 8f;
    internal const float GrayscaleBucketUnits = VhgtQuantizationUnits * 256f;
}
