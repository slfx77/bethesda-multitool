namespace FalloutXbox360Utils.Core.Formats.Esm.Terrain;

public readonly record struct TerrainGridMapping(int X, int Y, float FitError);

/// <summary>
///     Shared LAND coordinate mapping for runtime terrain vertices and canonical 33x33 grids.
/// </summary>
public static class TerrainCoordinateMapper
{
    public static TerrainGridMapping? TryMapLocalVertexToCanonicalCell(float x, float y)
    {
        var gridX = (int)MathF.Round((x - TerrainConstants.LandLocalMin) / TerrainConstants.LandVertexSpacing);
        var gridY = (int)MathF.Round((y - TerrainConstants.LandLocalMin) / TerrainConstants.LandVertexSpacing);
        if (gridX < 0 ||
            gridX >= TerrainConstants.LandGridSize ||
            gridY < 0 ||
            gridY >= TerrainConstants.LandGridSize)
        {
            return null;
        }

        var expectedX = TerrainConstants.LandLocalMin + gridX * TerrainConstants.LandVertexSpacing;
        var expectedY = TerrainConstants.LandLocalMin + gridY * TerrainConstants.LandVertexSpacing;
        var dx = x - expectedX;
        var dy = y - expectedY;
        var fitError = MathF.Sqrt(dx * dx + dy * dy);
        return fitError <= TerrainConstants.CanonicalVertexFitTolerance
            ? new TerrainGridMapping(gridX, gridY, fitError)
            : null;
    }

    public static bool IsWithinLocalCellBounds(float minX, float maxX, float minY, float maxY)
    {
        return minX >= TerrainConstants.LandLocalMin - TerrainConstants.LocalBoundsTolerance &&
               maxX <= TerrainConstants.LandLocalMax + TerrainConstants.LocalBoundsTolerance &&
               minY >= TerrainConstants.LandLocalMin - TerrainConstants.LocalBoundsTolerance &&
               maxY <= TerrainConstants.LandLocalMax + TerrainConstants.LocalBoundsTolerance;
    }
}
