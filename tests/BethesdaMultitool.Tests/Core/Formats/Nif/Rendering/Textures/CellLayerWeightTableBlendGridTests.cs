using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Blend-position grid-convention tests. VTXT positions are authored on a fixed 17×17 quadrant
///     grid; the weight table renders at 33 (classic), 65 (Morrowind) or 129 (Starfield/FO76 native
///     BTD). Before the fix, <c>PopulateAtxtGrids</c> indexed 17-convention positions straight into a
///     <c>QuadEdge</c>-wide dense grid — correct only at 33, and at 129 (QuadEdge 65) it crammed the
///     whole alpha map into the quadrant's first ~4.5 vertex rows: the "one repeating stone stripe
///     every 50 m" Akila City defect. These pin the resampled decode at every grid size plus the
///     native-65 emission path and its 17-grid projection.
/// </summary>
public class CellLayerWeightTableBlendGridTests
{
    private const uint BaseId = 0xFF200001u;
    private const uint AlphaId = 0xFF200002u;

    private static LandTextureLayer BaseLayer(byte quadrant)
    {
        return new LandTextureLayer
        {
            Kind = LandTextureLayerKind.Base,
            TextureFormId = BaseId,
            Quadrant = quadrant
        };
    }

    /// <summary>A quadrant-covering alpha layer: every position on the declared grid at full opacity.</summary>
    private static LandTextureLayer FullAlphaLayer(byte quadrant, int blendEdge)
    {
        var entries = new List<LandTextureBlendEntry>(blendEdge * blendEdge);
        for (var p = 0; p < blendEdge * blendEdge; p++)
        {
            entries.Add(new LandTextureBlendEntry((ushort)p, 0, 0, 1f));
        }

        return new LandTextureLayer
        {
            Kind = LandTextureLayerKind.Alpha,
            TextureFormId = AlphaId,
            Quadrant = quadrant,
            Layer = 0,
            BlendEntries = entries,
            BlendGridEdge = blendEdge
        };
    }

    private static void AssertQuadrantIsAlpha(CellLayerWeightTable table, int quadrant)
    {
        // Quadrant → cell-vertex window (matching BuildInto's mapping: SW=0, SE=1, NW=2, NE=3;
        // vy 0 = north edge). Interior vertices only — shared mid/edge rows legitimately mix
        // contributions from the neighboring quadrant's base.
        var mid = (table.GridSize - 1) / 2;
        var (x0, x1) = (quadrant & 1) == 0 ? (0, mid - 1) : (mid + 1, table.GridSize - 1);
        var (y0, y1) = (quadrant & 2) == 0 ? (mid + 1, table.GridSize - 1) : (0, mid - 1);

        for (var vy = y0; vy <= y1; vy++)
        {
            for (var vx = x0; vx <= x1; vx++)
            {
                ref var w = ref table.At(vx, vy);
                Assert.True(w.Count > 0, $"vertex ({vx},{vy}) received no weight");
                Assert.Equal(AlphaId, w.E0.FormId);
                Assert.Equal(1f, w.E0.Weight, 3);
            }
        }
    }

    /// <summary>
    ///     The decisive regression for the Akila stripe: a 17-convention alpha map covering the whole
    ///     SW quadrant must cover the whole SW quadrant at the native 129 grid too. Pre-fix, only the
    ///     ~5 southernmost vertex rows carried it.
    /// </summary>
    [Theory]
    [InlineData(33)]
    [InlineData(129)]
    public void Vtxt17FullQuadrantAlpha_CoversWholeQuadrant_AtAnyGridSize(int gridSize)
    {
        var layers = new List<LandTextureLayer> { BaseLayer(0), FullAlphaLayer(0, 17) };

        var table = CellLayerWeightTable.Build(gridSize, layers);

        Assert.NotNull(table);
        AssertQuadrantIsAlpha(table!, 0);
    }

    /// <summary>Native-resolution (65-edge) entries — the Starfield injector's emission — decode 1:1 at 129.</summary>
    [Fact]
    public void NativeEdge65FullQuadrantAlpha_CoversWholeQuadrant_At129()
    {
        var layers = new List<LandTextureLayer> { BaseLayer(1), FullAlphaLayer(1, 65) };

        var table = CellLayerWeightTable.Build(129, layers);

        Assert.NotNull(table);
        AssertQuadrantIsAlpha(table!, 1);
    }

    /// <summary>65-edge entries downsample cleanly onto the classic 33 grid (the 2D map path).</summary>
    [Fact]
    public void NativeEdge65FullQuadrantAlpha_CoversWholeQuadrant_At33()
    {
        var layers = new List<LandTextureLayer> { BaseLayer(2), FullAlphaLayer(2, 65) };

        var table = CellLayerWeightTable.Build(33, layers);

        Assert.NotNull(table);
        AssertQuadrantIsAlpha(table!, 2);
    }

    /// <summary>
    ///     A spatially-varying 17-grid map must land at the right PLACE on the 129 grid: an alpha
    ///     painted only on the quadrant's north half must weight the north half, not the south rows.
    ///     Pre-fix, the scrambled decode put every entry in the far south.
    /// </summary>
    [Fact]
    public void Vtxt17NorthHalfAlpha_LandsOnNorthHalf_At129()
    {
        var entries = new List<LandTextureBlendEntry>();
        for (var qy = 9; qy < 17; qy++) // northern half of the quadrant (qy grows north)
        {
            for (var qx = 0; qx < 17; qx++)
            {
                entries.Add(new LandTextureBlendEntry((ushort)(qy * 17 + qx), 0, 0, 1f));
            }
        }

        var layers = new List<LandTextureLayer>
        {
            BaseLayer(0),
            new()
            {
                Kind = LandTextureLayerKind.Alpha,
                TextureFormId = AlphaId,
                Quadrant = 0,
                BlendEntries = entries
            }
        };

        var table = CellLayerWeightTable.Build(129, layers);
        Assert.NotNull(table);

        // SW quadrant on the 129 grid: vx 0..64, vy 64..128 (vy 64 = its north edge). qy=9..16 of 17
        // maps to the quadrant's northern ~44%: alpha near the quadrant's north, base near its south.
        ref var northVertex = ref table!.At(30, 70);
        Assert.Equal(AlphaId, northVertex.E0.FormId);

        ref var southVertex = ref table.At(30, 124);
        Assert.True(southVertex.Count > 0);
        Assert.Equal(BaseId, southVertex.E0.FormId);
    }

    /// <summary>The 17-grid projection keeps exactly the lattice positions and rejects non-16n+1 edges.</summary>
    [Fact]
    public void EnumerateVtxt17Entries_ProjectsLatticeAndRejectsBadEdges()
    {
        var layer = FullAlphaLayer(0, 65);
        var projected = layer.EnumerateVtxt17Entries().ToList();

        Assert.Equal(17 * 17, projected.Count);
        Assert.All(projected, e => Assert.InRange(e.Position, (ushort)0, (ushort)288));
        // Spot-check the mapping: native (qx=4, qy=8) → 17-grid (1, 2).
        Assert.Contains(projected, e => e.Position == 2 * 17 + 1);

        var badEdge = layer with { BlendGridEdge = 40 };
        Assert.Empty(badEdge.EnumerateVtxt17Entries());

        var classic = FullAlphaLayer(0, 17);
        Assert.Same(classic.BlendEntries, classic.EnumerateVtxt17Entries());
    }
}