using BethesdaMultitool.Core.Formats.Esm.Land;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Land;

/// <summary>
///     Pins the FO76-1B BTD-quadrant → VtexTextureFormIds layout: the four BTD quadrant land-texture
///     FormIDs (indexed (north&lt;&lt;1)|east = SW/SE/NW/NE) fill the renderer's 16×16 row-major
///     (south→north rows, west→east columns) grid in the matching 8×8 blocks.
/// </summary>
public sealed class Fo76VtexGridTests
{
    private const int N = 16;

    [Fact]
    public void AssembleVtexGrid_PlacesEachQuadrantInItsCorner()
    {
        // SW=10, SE=20, NW=30, NE=40 (quadrant index = (north<<1)|east).
        var grid = Fo76TerrainInjector.AssembleVtexGrid([10u, 20u, 30u, 40u]);

        Assert.Equal(N * N, grid.Length);
        // Corners: (row, col) row 0 = south, row 15 = north; col 0 = west, col 15 = east.
        Assert.Equal(10u, grid[(0 * N) + 0]);    // SW
        Assert.Equal(20u, grid[(0 * N) + 15]);   // SE
        Assert.Equal(30u, grid[(15 * N) + 0]);   // NW
        Assert.Equal(40u, grid[(15 * N) + 15]);  // NE
        // Block boundaries: rows/cols 0–7 are south/west, 8–15 north/east.
        Assert.Equal(10u, grid[(7 * N) + 7]);    // last SW cell
        Assert.Equal(40u, grid[(8 * N) + 8]);    // first NE cell
        Assert.Equal(30u, grid[(8 * N) + 0]);    // first NW row, west col
        Assert.Equal(20u, grid[(0 * N) + 8]);    // south row, first east col
    }

    [Fact]
    public void AssembleVtexGrid_UniformQuadrantsFillEntireGrid()
    {
        var grid = Fo76TerrainInjector.AssembleVtexGrid([7u, 7u, 7u, 7u]);
        Assert.All(grid, v => Assert.Equal(7u, v));
    }
}
