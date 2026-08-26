namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     What a 2D map export will produce for a requested long-edge pixel budget: the image
///     dimensions, the pixels-per-cell actually used, and how the result is split.
/// </summary>
/// <param name="ImageWidth">Total output width in pixels (summed across tiles when tiled).</param>
/// <param name="ImageHeight">Total output height in pixels.</param>
/// <param name="EffectivePxPerCell">Pixels per cell after any single-image clamp.</param>
/// <param name="Capped">The single-image path clamped the request to fit the max tile dimension.</param>
/// <param name="Columns">Tile columns (1 when the export is a single image).</param>
/// <param name="Rows">Tile rows (1 when the export is a single image).</param>
internal readonly record struct MapExportSize(
    long ImageWidth,
    long ImageHeight,
    int EffectivePxPerCell,
    bool Capped,
    long Columns,
    long Rows);

/// <summary>
///     Sizing arithmetic for the 2D map exporter's "Output: W × H px" preview.
///     <para>
///         Every product of a cell span and a px/cell scale is computed in <see cref="long" />.
///         That is the whole point of this type: a large worldspace at a high pixel budget
///         overflows <see cref="int" /> easily, and the failure is silent — a negative or wrapped
///         dimension reads as a plausible number in the label and only surfaces later as a bad
///         allocation.
///     </para>
///     <para>
///         In <c>Core/</c> because the caller lives under <c>App/</c>, which is excluded from the
///         <c>net10.0</c> target framework. The previous coverage asserted that the source text
///         still contained <c>"(long)cellsWide * effectivePpc"</c> — a string check that cannot
///         evaluate a single product, let alone one near the overflow boundary.
///     </para>
/// </summary>
internal static class MapExportSizeEstimate
{
    /// <summary>
    ///     Plans the output for a requested long-edge budget.
    /// </summary>
    /// <param name="cellsWide">Worldspace width in cells.</param>
    /// <param name="cellsTall">Worldspace height in cells.</param>
    /// <param name="maxGridDimension">The longer of the two cell spans; drives the px/cell budget.</param>
    /// <param name="requestedLongEdgePx">Requested pixel length of the long edge.</param>
    /// <param name="maxTileDimension">Hard per-image pixel ceiling (GPU texture limit).</param>
    /// <param name="tiled">When true the export splits into multiple PNGs instead of clamping.</param>
    public static MapExportSize Plan(
        int cellsWide,
        int cellsTall,
        int maxGridDimension,
        int requestedLongEdgePx,
        int maxTileDimension,
        bool tiled)
    {
        // A degenerate grid would divide by zero; treat it as one cell so the caller still gets a
        // sane preview rather than an exception behind a text label.
        var maxCells = Math.Max(1, maxGridDimension);
        var requestedPxPerCell = Math.Max(1, requestedLongEdgePx / maxCells);

        var effectivePxPerCell = requestedPxPerCell;
        var capped = false;
        if (!tiled && (long)requestedPxPerCell * maxCells > maxTileDimension)
        {
            // Without tiling a single PNG cannot exceed the max dimension, so report the px/cell
            // that actually survives the clamp.
            effectivePxPerCell = Math.Max(1, maxTileDimension / maxCells);
            capped = true;
        }

        var imageWidth = (long)cellsWide * effectivePxPerCell;
        var imageHeight = (long)cellsTall * effectivePxPerCell;

        var columns = 1L;
        var rows = 1L;
        if (tiled && (long)effectivePxPerCell * maxCells > maxTileDimension)
        {
            var cellsPerTile = Math.Max(1, maxTileDimension / effectivePxPerCell);
            columns = CeilingDivide(cellsWide, cellsPerTile);
            rows = CeilingDivide(cellsTall, cellsPerTile);
        }

        return new MapExportSize(imageWidth, imageHeight, effectivePxPerCell, capped, columns, rows);
    }

    /// <summary>Ceiling division in <see cref="long" />, so a wide grid cannot wrap the tile count.</summary>
    private static long CeilingDivide(int cells, int cellsPerTile)
    {
        return ((long)cells + cellsPerTile - 1) / cellsPerTile;
    }
}
