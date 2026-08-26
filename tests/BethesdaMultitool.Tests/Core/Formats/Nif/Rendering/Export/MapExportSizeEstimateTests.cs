using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Sizing arithmetic for the 2D map exporter's output preview.
///     <para>
///         Previously covered by asserting the source text still contained
///         <c>"(long)cellsWide * effectivePpc"</c>. That is the right <em>intent</em> — keep the
///         products in 64-bit — but a string match cannot evaluate a product, so the overflow class
///         the assertion exists to prevent was never actually exercised. These tests run the
///         arithmetic at the boundary instead.
///     </para>
/// </summary>
public class MapExportSizeEstimateTests
{
    /// <summary>A realistic GPU max texture dimension.</summary>
    private const int MaxTile = 16_384;

    [Fact]
    public void Plan_SmallGridWithinBudget_UsesTheRequestedScale()
    {
        // 32 cells across, 8192 px long edge -> 256 px/cell, well inside the tile ceiling.
        var size = MapExportSizeEstimate.Plan(
            cellsWide: 32, cellsTall: 16, maxGridDimension: 32,
            requestedLongEdgePx: 8_192, maxTileDimension: MaxTile, tiled: false);

        Assert.Equal(256, size.EffectivePxPerCell);
        Assert.Equal(32L * 256, size.ImageWidth);
        Assert.Equal(16L * 256, size.ImageHeight);
        Assert.False(size.Capped);
        Assert.Equal(1, size.Columns);
        Assert.Equal(1, size.Rows);
    }

    /// <summary>
    ///     A single PNG cannot exceed the tile ceiling, so the preview must report the reduced
    ///     px/cell rather than a size the exporter could never produce.
    /// </summary>
    [Fact]
    public void Plan_SingleImageOverTheCeiling_ClampsAndReportsCapped()
    {
        var size = MapExportSizeEstimate.Plan(
            cellsWide: 64, cellsTall: 64, maxGridDimension: 64,
            requestedLongEdgePx: 1_000_000, maxTileDimension: MaxTile, tiled: false);

        Assert.True(size.Capped);
        Assert.Equal(MaxTile / 64, size.EffectivePxPerCell);
        Assert.True(size.ImageWidth <= MaxTile);
        Assert.True(size.ImageHeight <= MaxTile);
    }

    /// <summary>Tiling is the alternative to clamping, so a tiled export never reports capped.</summary>
    [Fact]
    public void Plan_TiledOverTheCeiling_SplitsInsteadOfClamping()
    {
        var size = MapExportSizeEstimate.Plan(
            cellsWide: 64, cellsTall: 32, maxGridDimension: 64,
            requestedLongEdgePx: 65_536, maxTileDimension: MaxTile, tiled: true);

        Assert.False(size.Capped);
        Assert.Equal(1_024, size.EffectivePxPerCell);
        Assert.True(size.Columns > 1);
        // 16384/1024 = 16 cells per tile -> 64/16 = 4 columns, 32/16 = 2 rows.
        Assert.Equal(4, size.Columns);
        Assert.Equal(2, size.Rows);
    }

    /// <summary>Partial tiles must round up, or the right/bottom edge would be dropped.</summary>
    [Fact]
    public void Plan_TiledWithAPartialTrailingTile_RoundsTheTileCountUp()
    {
        // 16384/1024 = 16 cells per tile; 33 cells needs 3 columns, not 2.
        var size = MapExportSizeEstimate.Plan(
            cellsWide: 33, cellsTall: 17, maxGridDimension: 64,
            requestedLongEdgePx: 65_536, maxTileDimension: MaxTile, tiled: true);

        Assert.Equal(3, size.Columns);
        Assert.Equal(2, size.Rows);
    }

    /// <summary>
    ///     Why every product is 64-bit.
    ///     <para>
    ///         With consistent inputs the width is bounded by the requested long edge — px/cell is
    ///         derived by dividing by the same span — so it cannot exceed <see cref="int.MaxValue" />
    ///         on its own. The exposure is an <em>inconsistent</em> caller: a cell span larger than
    ///         the <c>maxGridDimension</c> the scale was derived from multiplies back up without
    ///         bound. In 32-bit arithmetic that wraps to a negative number and the preview shows a
    ///         plausible-looking lie instead of an obviously huge one.
    ///     </para>
    /// </summary>
    [Fact]
    public void Plan_CellSpanLargerThanTheScaleReference_DoesNotWrapIntoNegativeDimensions()
    {
        var size = MapExportSizeEstimate.Plan(
            cellsWide: 2_000_000, cellsTall: 2_000_000, maxGridDimension: 10,
            requestedLongEdgePx: 100_000, maxTileDimension: MaxTile, tiled: true);

        Assert.True(size.ImageWidth > int.MaxValue,
            $"Expected a width past int.MaxValue, got {size.ImageWidth}.");
        Assert.True(size.ImageWidth > 0, "Width wrapped to a negative value.");
        Assert.True(size.ImageHeight > 0, "Height wrapped to a negative value.");
        Assert.True(size.Columns > 0 && size.Rows > 0, "Tile counts wrapped.");
    }

    /// <summary>
    ///     A known limit of the single-image path, asserted so it stays deliberate: when the grid
    ///     has more cells than the tile ceiling has pixels, px/cell floors at 1 and the image
    ///     necessarily exceeds the ceiling. Tiling is the only way to export such a worldspace,
    ///     which is exactly what the "enable Tiled for full detail" note tells the user.
    /// </summary>
    [Fact]
    public void Plan_MoreCellsThanTheTileCeiling_FloorsAtOnePixelPerCellAndCannotHonourTheCeiling()
    {
        const int cells = 20_000; // > MaxTile

        var size = MapExportSizeEstimate.Plan(
            cellsWide: cells, cellsTall: cells, maxGridDimension: cells,
            requestedLongEdgePx: int.MaxValue, maxTileDimension: MaxTile, tiled: false);

        Assert.True(size.Capped);
        Assert.Equal(1, size.EffectivePxPerCell);
        Assert.Equal(cells, size.ImageWidth);
        Assert.True(size.ImageWidth > MaxTile,
            "One pixel per cell already exceeds the ceiling — tiling is the only route.");
    }

    /// <summary>
    ///     Below that threshold the clamp does hold the single image inside the ceiling.
    /// </summary>
    [Fact]
    public void Plan_HugeRequestOnAModestGrid_StaysWithinTheTileCeiling()
    {
        var size = MapExportSizeEstimate.Plan(
            cellsWide: 512, cellsTall: 512, maxGridDimension: 512,
            requestedLongEdgePx: int.MaxValue, maxTileDimension: MaxTile, tiled: false);

        Assert.True(size.Capped);
        Assert.True(size.ImageWidth is > 0 and <= MaxTile);
        Assert.True(size.ImageHeight is > 0 and <= MaxTile);
    }

    /// <summary>
    ///     px/cell floors at 1: a request finer than one pixel per cell still has to produce a
    ///     drawable image rather than a zero-width one.
    /// </summary>
    [Theory]
    [InlineData(0, "no pixel budget at all")]
    [InlineData(1, "one pixel across the whole grid")]
    [InlineData(63, "fewer pixels than cells")]
    public void Plan_TinyPixelBudget_FloorsAtOnePixelPerCell(int requestedLongEdgePx, string because)
    {
        _ = because;

        var size = MapExportSizeEstimate.Plan(
            cellsWide: 64, cellsTall: 64, maxGridDimension: 64,
            requestedLongEdgePx, maxTileDimension: MaxTile, tiled: false);

        Assert.Equal(1, size.EffectivePxPerCell);
        Assert.Equal(64, size.ImageWidth);
        Assert.Equal(64, size.ImageHeight);
    }

    /// <summary>A degenerate grid must not divide by zero behind a text label.</summary>
    [Fact]
    public void Plan_ZeroGridDimension_DoesNotThrow()
    {
        var size = MapExportSizeEstimate.Plan(
            cellsWide: 0, cellsTall: 0, maxGridDimension: 0,
            requestedLongEdgePx: 4_096, maxTileDimension: MaxTile, tiled: false);

        Assert.True(size.EffectivePxPerCell >= 1);
        Assert.Equal(0, size.ImageWidth);
        Assert.Equal(0, size.ImageHeight);
    }

    /// <summary>
    ///     Tiling exists so the user can get more detail than a single image allows: for the same
    ///     request, the tiled plan must never be coarser.
    /// </summary>
    [Fact]
    public void Plan_TiledIsNeverCoarserThanTheClampedSingleImage()
    {
        const int cellsWide = 128;
        const int longEdge = 262_144;

        var single = MapExportSizeEstimate.Plan(
            cellsWide, cellsWide, cellsWide, longEdge, MaxTile, tiled: false);
        var tiled = MapExportSizeEstimate.Plan(
            cellsWide, cellsWide, cellsWide, longEdge, MaxTile, tiled: true);

        Assert.True(tiled.EffectivePxPerCell >= single.EffectivePxPerCell);
    }
}
