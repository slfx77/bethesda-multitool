using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Pins the horizontal position the vertex shader rebuilds from <c>SV_VertexID</c> after world
///     X and Y stopped being stored per vertex.
///     <para>
///         The load-bearing claim is <b>exactness</b>, not closeness. Two adjacent cells compute the
///         same shared edge from different origins, so anything less than bit-equality is a hairline
///         crack running the length of every cell boundary in the worldspace — and it would be
///         invisible in any test that compared with a tolerance.
///     </para>
/// </summary>
public sealed class TerrainCellGridTests
{
    private const float FalloutCell = 4096f;
    private const float MorrowindCell = 8192f;

    /// <summary>The three (cell size, grid) pairs the supported games actually use.</summary>
    public static TheoryData<float, int, float> RealGrids => new()
    {
        { FalloutCell, 33, 128f }, // Fallout 3/NV/4, Oblivion, Skyrim, runtime DMP
        { MorrowindCell, 65, 128f }, // Morrowind
        { FalloutCell, 129, 32f } // Fallout 76 / high-resolution heightmaps
    };

    [Theory]
    [MemberData(nameof(RealGrids))]
    public void The_spacing_the_builder_derives_is_a_power_of_two(float cellSize, int gridSize, float expectedSpacing)
    {
        // Why the reconstruction is exact rather than lucky: cellSize / (gridSize - 1) lands on a
        // power of two for every grid the games use, so index × spacing is an exact integer.
        Assert.Equal(expectedSpacing, cellSize / (gridSize - 1));
    }

    [Theory]
    [MemberData(nameof(RealGrids))]
    public void Every_vertex_of_a_real_grid_is_exactly_reconstructible(float cellSize, int gridSize, float spacing)
    {
        // Far from the origin, where a float loses its fractional bits first: gx = 64 is beyond the
        // edge of any shipped worldspace.
        var grid = new TerrainCellGrid(64 * cellSize, -64 * cellSize, spacing, gridSize);

        Assert.True(grid.IsExactlyReconstructible());

        for (var index = 0; index < grid.VertexCount; index++)
        {
            var position = grid.PositionOf(index, 0f);
            Assert.Equal(MathF.Truncate(position.X), position.X);
            Assert.Equal(MathF.Truncate(position.Y), position.Y);
        }
    }

    [Theory]
    [MemberData(nameof(RealGrids))]
    public void A_cells_east_column_lands_on_its_neighbours_west_column_exactly(
        float cellSize, int gridSize, float spacing)
    {
        // THE seam test. Cell (gx, gy)'s last column and cell (gx+1, gy)'s first column are the same
        // world line, computed from two different origins. Bit-equality, not approximate equality:
        // one ULP of disagreement is a visible crack.
        var west = new TerrainCellGrid(3 * cellSize, 7 * cellSize, spacing, gridSize);
        var east = new TerrainCellGrid(4 * cellSize, 7 * cellSize, spacing, gridSize);
        var last = gridSize - 1;

        for (var row = 0; row < gridSize; row++)
        {
            var westEdge = west.PositionOf(row * gridSize + last, 0f);
            var eastEdge = east.PositionOf(row * gridSize, 0f);

            Assert.Equal(westEdge.X, eastEdge.X);
            Assert.Equal(westEdge.Y, eastEdge.Y);
        }
    }

    [Theory]
    [MemberData(nameof(RealGrids))]
    public void A_cells_north_row_lands_on_its_neighbours_south_row_exactly(
        float cellSize, int gridSize, float spacing)
    {
        var south = new TerrainCellGrid(3 * cellSize, 7 * cellSize, spacing, gridSize);
        var north = new TerrainCellGrid(3 * cellSize, 8 * cellSize, spacing, gridSize);
        var last = gridSize - 1;

        for (var column = 0; column < gridSize; column++)
        {
            var southEdge = south.PositionOf(last * gridSize + column, 0f);
            var northEdge = north.PositionOf(column, 0f);

            Assert.Equal(southEdge.X, northEdge.X);
            Assert.Equal(southEdge.Y, northEdge.Y);
        }
    }

    [Fact]
    public void The_index_walks_rows_before_columns()
    {
        // Must match both the builder's fill order and the shared index buffer's j*n+i, or the cell
        // renders transposed. Independently stated here rather than read from either.
        var grid = new TerrainCellGrid(0f, 0f, 128f, 33);

        Assert.Equal(new Vector3(0f, 0f, 5f), grid.PositionOf(0, 5f));
        Assert.Equal(new Vector3(128f, 0f, 5f), grid.PositionOf(1, 5f)); // +1 index → +X
        Assert.Equal(new Vector3(0f, 128f, 5f), grid.PositionOf(33, 5f)); // +gridSize → +Y
        Assert.Equal(new Vector3(4096f, 4096f, 5f), grid.PositionOf(33 * 33 - 1, 5f));
    }

    [Fact]
    public void The_height_passes_through_untouched()
    {
        var grid = new TerrainCellGrid(0f, 0f, 128f, 33);

        Assert.Equal(-1234.5f, grid.PositionOf(17, -1234.5f).Z);
    }

    [Fact]
    public void A_non_integer_spacing_is_reported_as_not_exactly_reconstructible()
    {
        // Nothing produces this today, but the guarantee is a precondition rather than a law of
        // nature: a worldspace with an odd cell size would break the seam property, and the honest
        // answer is to say so rather than to keep claiming exactness.
        var grid = new TerrainCellGrid(0f, 0f, 100f / 3f, 33);

        Assert.False(grid.IsExactlyReconstructible());
    }

    [Fact]
    public void An_origin_beyond_the_exact_integer_range_is_reported_as_not_reconstructible()
    {
        // Past 2^24 a float can no longer hold consecutive integers, so origin + offset starts
        // snapping. No worldspace comes near it; the check exists so the claim stays checkable.
        var grid = new TerrainCellGrid(1 << 25, 0f, 128f, 33);

        Assert.False(grid.IsExactlyReconstructible());
    }

    [Fact]
    public void A_degenerate_grid_is_not_reconstructible()
    {
        Assert.False(new TerrainCellGrid(0f, 0f, 128f, 0).IsExactlyReconstructible());
        Assert.False(new TerrainCellGrid(0f, 0f, 128f, 1).IsExactlyReconstructible());
        Assert.False(new TerrainCellGrid(float.NaN, 0f, 128f, 33).IsExactlyReconstructible());
    }

    [Fact]
    public void VertexCount_is_the_square_of_the_edge()
    {
        Assert.Equal(1089, new TerrainCellGrid(0f, 0f, 128f, 33).VertexCount);
        Assert.Equal(4225, new TerrainCellGrid(0f, 0f, 128f, 65).VertexCount);
        Assert.Equal(16_641, new TerrainCellGrid(0f, 0f, 32f, 129).VertexCount);
    }
}
