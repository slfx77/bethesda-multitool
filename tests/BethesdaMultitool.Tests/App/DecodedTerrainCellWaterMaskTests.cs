using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Pins the dry-cell early-out in the water-mask builders.
///     <para>
///         The 2D map's terrain-textures layer samples the hi-res mask at up to
///         <c>WorldMapLayerRenderer.MaxTexturePixelsPerCell</c> = 1,056 px per cell axis — ~1.1M bilinear
///         height samples and a ~1 MB <c>byte[]</c> per cell, per call, uncached. Across a desert
///         worldspace every one of those cells is bone dry, so the whole cost was waste. The early-out
///         must be OUTPUT-IDENTICAL, which is the property these tests exist to hold: it may only fire
///         where the mask would have been all-zero, and an all-zero mask is already equivalent to
///         <c>null</c> for every consumer.
///     </para>
/// </summary>
public sealed class DecodedTerrainCellWaterMaskTests
{
    private const float Softness = 8f; // GetHiResWaterMask's default shorelineSoftnessUnits.

    [Theory]
    [InlineData(33)]
    [InlineData(66)]
    [InlineData(132)]
    [InlineData(264)]
    [InlineData(528)]
    [InlineData(1056)]
    public void HiResMask_CellEntirelyAboveWaterline_ReturnsNull(int pixelsPerCell)
    {
        var cell = FlatCell(80f);

        Assert.Null(DecodedTerrainCell.Decode(cell).GetHiResWaterMask(-2300f, pixelsPerCell));
    }

    /// <summary>
    ///     The boundary is inclusive-safe: at <c>MinHeight == waterH + softness</c> the fade parameter is
    ///     exactly 0 for every pixel, so the mask really is all-zero and skipping it is lossless.
    /// </summary>
    [Fact]
    public void HiResMask_MinHeightExactlyAtSoftnessBoundary_ReturnsNull()
    {
        var cell = FlatCell(80f);

        Assert.Null(DecodedTerrainCell.Decode(cell).GetHiResWaterMask(80f - Softness, 132));
    }

    /// <summary>
    ///     One notch below that boundary the mask carries real (if small) coverage, so the early-out must
    ///     NOT fire. This is the test that fails if the predicate is ever loosened to <c>&gt;=</c> waterH.
    /// </summary>
    [Fact]
    public void HiResMask_JustBelowSoftnessBoundary_StillBuildsANonZeroMask()
    {
        var cell = FlatCell(80f);

        var mask = DecodedTerrainCell.Decode(cell).GetHiResWaterMask(80f - Softness + 0.5f, 132);

        Assert.NotNull(mask);
        Assert.Contains(mask!, v => v > 0);
    }

    /// <summary>
    ///     Golden shape for a partially-submerged cell — the early-out must leave this path untouched.
    ///     The fixture ramps north-to-south, and the mask's row 0 samples the HIGHEST grid row (the
    ///     builder flips vertically), so row 0 is dry land and the last row is open water.
    /// </summary>
    [Fact]
    public void HiResMask_PartiallySubmerged_IsUnaffectedByTheEarlyOut()
    {
        var heights = new float[33, 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                heights[y, x] = y * 10f; // y=0 → 0 (deep), y=32 → 320 (high ground)
            }
        }

        var mask = DecodedTerrainCell.Decode(RampCell(heights)).GetHiResWaterMask(160f, 33);

        Assert.NotNull(mask);
        Assert.Equal(0, mask![0]); // north row samples grid y=32 (h=320) → dry
        Assert.Equal(180, mask[32 * 33]); // south row samples grid y=0 (h=0) → full water
        Assert.Contains(mask, v => v is > 0 and < 180); // and a real shoreline gradient between them
    }

    [Fact]
    public void LowResMask_CellEntirelyAboveWaterline_ReturnsNull()
    {
        var cell = FlatCell(200f);

        Assert.Null(DecodedTerrainCell.Decode(cell).GetLowResWaterMask(100f));
    }

    [Fact]
    public void LowResMask_SubmergedCell_StillBuilds()
    {
        var cell = FlatCell(80f);

        var mask = DecodedTerrainCell.Decode(cell).GetLowResWaterMask(100f);

        Assert.NotNull(mask);
        Assert.All(mask!, v => Assert.Equal((byte)180, v));
    }

    [Fact]
    public void NeighborMask_AllCellsAboveWaterline_ReturnsNull()
    {
        var dry = DecodedTerrainCell.Decode(FlatCell(200f));

        Assert.Null(DecodedTerrainCell.BuildLowResWaterMaskWithNeighbors(
            dry, dry, dry, dry, dry, 100f));
    }

    /// <summary>
    ///     The correctness case for the neighbor-aware early-out: a DRY cell beside a SUBMERGED one still
    ///     needs its mask built, because the 3×3 blur pulls the neighbor's border row in to produce the
    ///     shoreline fade. Skipping it here would hard-cut the waterline at every cell boundary.
    /// </summary>
    [Fact]
    public void NeighborMask_DryCellBesideSubmergedNeighbor_StillBuilds()
    {
        var dry = DecodedTerrainCell.Decode(FlatCell(200f));
        var submerged = DecodedTerrainCell.Decode(FlatCell(0f));

        var mask = DecodedTerrainCell.BuildLowResWaterMaskWithNeighbors(
            dry, null, submerged, null, null, 100f);

        Assert.NotNull(mask);
        Assert.Contains(mask!, v => v > 0);
    }

    /// <summary>
    ///     A terrain-LESS neighbor must not force the slow path: the builder copies this cell's own edge
    ///     row for those, so it cannot introduce water the self-test already ruled out.
    /// </summary>
    [Fact]
    public void NeighborMask_TerrainlessNeighbor_DoesNotDefeatTheEarlyOut()
    {
        var dry = DecodedTerrainCell.Decode(FlatCell(200f));
        var terrainless = DecodedTerrainCell.Decode(new CellRecord { FormId = 0x200 });

        Assert.False(terrainless.HasTerrain);
        Assert.Null(DecodedTerrainCell.BuildLowResWaterMaskWithNeighbors(
            dry, terrainless, terrainless, terrainless, terrainless, 100f));
    }

    [Fact]
    public void MinHeight_IsNegativeInfinity_WhenTheCellHasNoTerrain()
    {
        var decoded = DecodedTerrainCell.Decode(new CellRecord { FormId = 0x300 });

        Assert.False(decoded.HasTerrain);
        Assert.Equal(float.NegativeInfinity, decoded.MinHeight);
    }

    [Fact]
    public void MinHeight_IsTheLowestVertexOnTheThirtyThreeGrid()
    {
        var heights = new float[33, 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                heights[y, x] = 500f;
            }
        }

        heights[7, 11] = -42f;

        Assert.Equal(-42f, DecodedTerrainCell.Decode(RampCell(heights)).MinHeight);
    }

    private static CellRecord FlatCell(float height)
    {
        var heights = new float[33, 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                heights[y, x] = height;
            }
        }

        return RampCell(heights);
    }

    // ExactHeights bypasses VHGT delta decoding, so the fixture height IS the decoded height.
    // HeightDeltas is `required` but unread on that path — an empty grid keeps the fixture honest.
    private static CellRecord RampCell(float[,] heights)
    {
        return new CellRecord
        {
            FormId = 0x100,
            GridX = 0,
            GridY = 0,
            Heightmap = new LandHeightmap
            {
                HeightDeltas = new sbyte[33 * 33],
                ExactHeights = heights
            }
        };
    }
}