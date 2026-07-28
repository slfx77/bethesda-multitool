using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Morrowind ground texturing is the flat 16×16 VTEX grid (resolved to LTEX FormIds). The 2D map
///     blitter now feeds it through <see cref="CellLayerWeightTable.BuildFromVtexGrid" /> instead of the
///     coarse 4-quadrant fallback (Bug 2); these cover the grid→33×33 expansion that backs that path.
/// </summary>
public class CellLayerWeightTableVtexTests
{
    [Fact]
    public void BuildFromVtexGrid_UniformGrid_AssignsSingleLtexToEveryVertex()
    {
        const uint ltex = 0x00F0002Au; // a synthetic Morrowind LTEX FormId (Tes3FormIdScheme.LtexFormIdBase + idx)
        var vtex = new uint[16 * 16];
        Array.Fill(vtex, ltex);

        var table = CellLayerWeightTable.BuildFromVtexGrid(CellLayerWeightTable.CellVertexCount, vtex);

        for (var vy = 0; vy < CellLayerWeightTable.CellVertexCount; vy++)
        {
            for (var vx = 0; vx < CellLayerWeightTable.CellVertexCount; vx++)
            {
                ref var w = ref table.At(vx, vy);
                Assert.Equal(1, w.Count);
                Assert.Equal(ltex, w.E0.FormId);
            }
        }
    }

    [Fact]
    public void BuildFromVtexGrid_TwoTextures_BothSurviveTheExpansion()
    {
        // A coarse 4-quadrant approximation would collapse a split cell to one dominant texture; the
        // fine grid must keep both.
        var vtex = new uint[16 * 16];
        for (var i = 0; i < vtex.Length; i++)
        {
            vtex[i] = i < vtex.Length / 2 ? 0xFF100001u : 0xFF100002u;
        }

        var table = CellLayerWeightTable.BuildFromVtexGrid(CellLayerWeightTable.CellVertexCount, vtex);

        var seen = new HashSet<uint>();
        for (var vy = 0; vy < CellLayerWeightTable.CellVertexCount; vy++)
        {
            for (var vx = 0; vx < CellLayerWeightTable.CellVertexCount; vx++)
            {
                ref var w = ref table.At(vx, vy);
                if (w.Count > 0)
                {
                    seen.Add(w.E0.FormId);
                }
            }
        }

        Assert.Contains(0xFF100001u, seen);
        Assert.Contains(0xFF100002u, seen);
    }

    // Boundary vertices belong to the NEXT texture square (floor semantics), which on the cell's
    // north/east edges lives in the neighbor cell — passing the neighbor grids is what makes the
    // border quad blend cross-cell like an interior square boundary instead of a hard seam.

    [Fact]
    public void BuildFromVtexGrid_EastEdge_TakesEastNeighborFirstColumn()
    {
        const uint own = 0xFF100001u, east = 0xFF100002u;
        var vtex = new uint[16 * 16];
        Array.Fill(vtex, own);
        var eastVtex = new uint[16 * 16];
        Array.Fill(eastVtex, east);

        const int grid = CellLayerWeightTable.CellVertexCount;
        var table = CellLayerWeightTable.BuildFromVtexGrid(grid, vtex, eastVtexFormIds: eastVtex);

        for (var vy = 1; vy < grid; vy++) // vy=0 is the north edge (own clamp without a north grid)
        {
            Assert.Equal(east, table.At(grid - 1, vy).E0.FormId);
            Assert.Equal(own, table.At(grid - 2, vy).E0.FormId);
        }
    }

    [Fact]
    public void BuildFromVtexGrid_NorthEdge_TakesNorthNeighborFirstRow()
    {
        const uint own = 0xFF100001u, north = 0xFF100003u;
        var vtex = new uint[16 * 16];
        Array.Fill(vtex, own);
        var northVtex = new uint[16 * 16];
        Array.Fill(northVtex, north);

        const int grid = CellLayerWeightTable.CellVertexCount;
        var table = CellLayerWeightTable.BuildFromVtexGrid(grid, vtex, northVtexFormIds: northVtex);

        // Table vy=0 is the cell's NORTH edge row; VTEX rows are south-origin, so the neighbor's
        // adjacent squares are its row 0.
        for (var vx = 0; vx < grid - 1; vx++) // vx=last is the east edge (own clamp without an east grid)
        {
            Assert.Equal(north, table.At(vx, 0).E0.FormId);
            Assert.Equal(own, table.At(vx, 1).E0.FormId);
        }
    }

    [Fact]
    public void BuildFromVtexGrid_NorthEastCorner_TakesDiagonalNeighborOrigin()
    {
        const uint own = 0xFF100001u, ne = 0xFF100004u;
        var vtex = new uint[16 * 16];
        Array.Fill(vtex, own);
        var neVtex = new uint[16 * 16];
        Array.Fill(neVtex, ne);

        const int grid = CellLayerWeightTable.CellVertexCount;
        var table = CellLayerWeightTable.BuildFromVtexGrid(grid, vtex, northEastVtexFormIds: neVtex);

        Assert.Equal(ne, table.At(grid - 1, 0).E0.FormId);
    }

    [Fact]
    public void BuildFromVtexGrid_NoNeighbors_ClampsToOwnEdgeSquares()
    {
        const uint own = 0xFF100001u;
        var vtex = new uint[16 * 16];
        Array.Fill(vtex, own);

        const int grid = CellLayerWeightTable.CellVertexCount;
        var table = CellLayerWeightTable.BuildFromVtexGrid(grid, vtex);

        for (var v = 0; v < grid; v++)
        {
            Assert.Equal(own, table.At(grid - 1, v).E0.FormId);
            Assert.Equal(own, table.At(v, 0).E0.FormId);
        }
    }
}