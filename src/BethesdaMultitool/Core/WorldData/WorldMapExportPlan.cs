namespace BethesdaMultitool.Core.WorldData;

/// <summary>A validated inclusive exterior-cell rectangle for 2D map export.</summary>
internal readonly record struct WorldMapExportGridBounds(
    int MinGridX,
    int MaxGridX,
    int MinGridY,
    int MaxGridY,
    int CellsWide,
    int CellsTall)
{
    internal int MaxGridDimension => Math.Max(CellsWide, CellsTall);
}

/// <summary>A world-space rectangle whose upper grid edges were evaluated without integer overflow.</summary>
internal readonly record struct WorldMapExportWorldBounds(
    float MinX,
    float MaxX,
    float MinY,
    float MaxY);

/// <summary>
///     Pure, checked plan for the 2D map export. The UI constructs this before opening the save picker,
///     then the renderer consumes the same instance so validation and execution cannot drift apart.
/// </summary>
internal sealed record WorldMapExportPlan(
    WorldMapExportGridBounds Grid,
    int PixelsPerCell,
    int CellsPerTile,
    int Columns,
    int Rows,
    int TotalTiles,
    long ImageWidth,
    long ImageHeight,
    float CellWorldSize)
{
    // The current Win2D export path draws absolute float world coordinates through one float
    // translation×scale matrix. Bound the magnitude so worst-case operand rounding stays well below
    // one quarter pixel. This is deliberately conservative; a future tile-local path can remove it.
    private const long MaxSafeProjectedGridMagnitude = 1L << 18;

    /// <summary>Validates inclusive grid spans without evaluating <c>max - min + 1</c> as an int.</summary>
    internal static bool TryCreateGridBounds(
        int minGridX,
        int maxGridX,
        int minGridY,
        int maxGridY,
        out WorldMapExportGridBounds bounds,
        out string error)
    {
        bounds = default;
        if (maxGridX < minGridX || maxGridY < minGridY)
        {
            error = "grid maxima must not be less than their minima";
            return false;
        }

        var cellsWide = (long)maxGridX - minGridX + 1;
        var cellsTall = (long)maxGridY - minGridY + 1;
        if (cellsWide > int.MaxValue || cellsTall > int.MaxValue)
        {
            error = "the inclusive cell span exceeds the exporter's integer grid capacity";
            return false;
        }

        bounds = new WorldMapExportGridBounds(
            minGridX,
            maxGridX,
            minGridY,
            maxGridY,
            (int)cellsWide,
            (int)cellsTall);
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Builds a tile plan and rejects counts, pixel dimensions, or world coordinates that the
    ///     current int-indexed/float-coordinate rendering path cannot represent.
    /// </summary>
    internal static bool TryCreate(
        WorldMapExportGridBounds grid,
        int pixelsPerCell,
        int cellsPerTile,
        int maxTileDimension,
        float cellWorldSize,
        out WorldMapExportPlan? plan,
        out string error)
    {
        plan = null;
        if (grid.CellsWide < 1 || grid.CellsTall < 1)
        {
            error = "the export grid must contain at least one cell";
            return false;
        }

        if (pixelsPerCell < 1 || cellsPerTile < 1 || maxTileDimension < 1)
        {
            error = "pixel and tile dimensions must be positive";
            return false;
        }

        if (!float.IsFinite(cellWorldSize) || cellWorldSize <= 0f)
        {
            error = "cell world size must be finite and positive";
            return false;
        }

        var pixelsPerWorldUnit = pixelsPerCell / cellWorldSize;
        if (!float.IsFinite(pixelsPerWorldUnit) || pixelsPerWorldUnit <= 0f)
        {
            error = "the pixel-to-world scale must be finite and positive";
            return false;
        }

        var columns = ((long)grid.CellsWide + cellsPerTile - 1) / cellsPerTile;
        var rows = ((long)grid.CellsTall + cellsPerTile - 1) / cellsPerTile;
        var totalTiles = columns * rows;
        if (columns > int.MaxValue || rows > int.MaxValue || totalTiles > int.MaxValue)
        {
            error = "the tile grid exceeds the exporter's integer progress/manifest capacity";
            return false;
        }

        var maxTileCellsWide = Math.Min(grid.CellsWide, cellsPerTile);
        var maxTileCellsTall = Math.Min(grid.CellsTall, cellsPerTile);
        var maxTileWidth = (long)maxTileCellsWide * pixelsPerCell;
        var maxTileHeight = (long)maxTileCellsTall * pixelsPerCell;
        if (maxTileWidth > maxTileDimension || maxTileHeight > maxTileDimension)
        {
            error = "a planned tile exceeds the GPU texture dimension limit";
            return false;
        }

        var imageWidth = (long)grid.CellsWide * pixelsPerCell;
        var imageHeight = (long)grid.CellsTall * pixelsPerCell;

        if (!HasSafeFloatTransformMagnitude(grid, pixelsPerCell))
        {
            error = "the grid is too far from the origin for the current float render transform";
            return false;
        }

        // Checking one-cell spans at both ends is intentionally stricter than checking only the
        // whole rectangle: at very large grid coordinates, two adjacent cell edges can collapse to
        // the same float even while the distant overall endpoints remain distinct.
        if (!HasRepresentableCellSpan(grid.MinGridX, cellWorldSize) ||
            !HasRepresentableCellSpan(grid.MaxGridX, cellWorldSize) ||
            !HasRepresentableCellSpan(grid.MinGridY, cellWorldSize) ||
            !HasRepresentableCellSpan(grid.MaxGridY, cellWorldSize) ||
            !TryGetWorldBounds(
                grid.MinGridX,
                grid.MaxGridX,
                grid.MinGridY,
                grid.MaxGridY,
                cellWorldSize,
                out _))
        {
            error = "the grid cannot be represented as distinct finite float world coordinates";
            return false;
        }

        plan = new WorldMapExportPlan(
            grid,
            pixelsPerCell,
            cellsPerTile,
            (int)columns,
            (int)rows,
            (int)totalTiles,
            imageWidth,
            imageHeight,
            cellWorldSize);
        error = string.Empty;
        return true;
    }

    /// <summary>Returns one planned tile's world rectangle; all upper edges use double arithmetic.</summary>
    internal WorldMapExportWorldBounds GetTileWorldBounds(
        int minGridX,
        int maxGridX,
        int minGridY,
        int maxGridY)
    {
        if (minGridX < Grid.MinGridX || maxGridX > Grid.MaxGridX || minGridX > maxGridX ||
            minGridY < Grid.MinGridY || maxGridY > Grid.MaxGridY || minGridY > maxGridY)
        {
            throw new ArgumentOutOfRangeException(nameof(minGridX), "Tile bounds fall outside the export plan.");
        }

        return GetWorldBoundsChecked(minGridX, maxGridX, minGridY, maxGridY, CellWorldSize);
    }

    /// <summary>Converts inclusive grid bounds to finite, non-collapsed world edges.</summary>
    internal static WorldMapExportWorldBounds GetWorldBoundsChecked(
        int minGridX,
        int maxGridX,
        int minGridY,
        int maxGridY,
        float cellWorldSize)
    {
        if (!TryGetWorldBounds(
                minGridX,
                maxGridX,
                minGridY,
                maxGridY,
                cellWorldSize,
                out var bounds))
        {
            throw new OverflowException("Grid bounds cannot be represented as finite float world coordinates.");
        }

        return bounds;
    }

    private static bool HasSafeFloatTransformMagnitude(
        WorldMapExportGridBounds grid,
        int pixelsPerCell)
    {
        var maxAbsGridEdge = Math.Max(
            Math.Max(Math.Abs((long)grid.MinGridX), Math.Abs(grid.MaxGridX + 1L)),
            Math.Max(Math.Abs((long)grid.MinGridY), Math.Abs(grid.MaxGridY + 1L)));
        return maxAbsGridEdge <= MaxSafeProjectedGridMagnitude / pixelsPerCell;
    }

    private static bool HasRepresentableCellSpan(int gridCoordinate, float cellWorldSize)
    {
        return TryConvertGridEdge(gridCoordinate, false, cellWorldSize, out var min) &&
               TryConvertGridEdge(gridCoordinate, true, cellWorldSize, out var max) &&
               max > min;
    }

    private static bool TryGetWorldBounds(
        int minGridX,
        int maxGridX,
        int minGridY,
        int maxGridY,
        float cellWorldSize,
        out WorldMapExportWorldBounds bounds)
    {
        bounds = default;
        if (maxGridX < minGridX || maxGridY < minGridY ||
            !float.IsFinite(cellWorldSize) || cellWorldSize <= 0f ||
            !TryConvertGridEdge(minGridX, false, cellWorldSize, out var minX) ||
            !TryConvertGridEdge(maxGridX, true, cellWorldSize, out var maxX) ||
            !TryConvertGridEdge(minGridY, false, cellWorldSize, out var minY) ||
            !TryConvertGridEdge(maxGridY, true, cellWorldSize, out var maxY) ||
            maxX <= minX || maxY <= minY)
        {
            return false;
        }

        bounds = new WorldMapExportWorldBounds(minX, maxX, minY, maxY);
        return true;
    }

    private static bool TryConvertGridEdge(
        int gridCoordinate,
        bool upperEdge,
        float cellWorldSize,
        out float worldCoordinate)
    {
        var gridEdge = gridCoordinate + (upperEdge ? 1d : 0d);
        var worldEdge = gridEdge * cellWorldSize;
        if (!double.IsFinite(worldEdge) || worldEdge < -float.MaxValue || worldEdge > float.MaxValue)
        {
            worldCoordinate = default;
            return false;
        }

        worldCoordinate = (float)worldEdge;
        return float.IsFinite(worldCoordinate);
    }
}
