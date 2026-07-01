using System.Collections.Generic;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

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
        System.Array.Fill(vtex, ltex);

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
}
