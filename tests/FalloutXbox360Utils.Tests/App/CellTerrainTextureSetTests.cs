using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

/// <summary>
///     Verifies the fixed-4-slot projection that the 3D renderer consumes. The variable-length
///     <see cref="CellLayerWeightTable" /> output is collapsed into 4 LTEX texture slots + a
///     per-vertex <see cref="System.Numerics.Vector4" /> of weights, with top-N-by-cell-weight
///     selection when there are more than 4 unique LTEXs.
/// </summary>
public sealed class CellTerrainTextureSetTests
{
    private const uint BtxtSw = 0x10001;
    private const uint BtxtSe = 0x10002;
    private const uint BtxtNw = 0x10003;
    private const uint BtxtNe = 0x10004;

    [Fact]
    public void FourBtxts_OneQuadrantEach_AllFourSlotsPopulated()
    {
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0), BtxtLayer(BtxtSe, 1), BtxtLayer(BtxtNw, 2), BtxtLayer(BtxtNe, 3)
        };
        var table = CellLayerWeightTable.Build(layers);

        var set = CellTerrainTextureSet.Project(table);

        Assert.NotNull(set);
        Assert.Equal(4, set!.ActiveSlotCount);
        Assert.Equal(new[] { BtxtSw, BtxtSe, BtxtNw, BtxtNe }.ToHashSet(),
                     set.SlotFormIds.ToHashSet());
    }

    [Fact]
    public void InteriorVertex_HasFullWeightOnItsQuadrantsSlot()
    {
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0), BtxtLayer(BtxtSe, 1), BtxtLayer(BtxtNw, 2), BtxtLayer(BtxtNe, 3)
        };
        var table = CellLayerWeightTable.Build(layers);
        var set = CellTerrainTextureSet.Project(table);

        Assert.NotNull(set);
        // NW interior vertex (4, 4): should have weight=1 in the slot that holds BtxtNw and
        // 0 elsewhere.
        var nwSlot = Array.IndexOf(set!.SlotFormIds, BtxtNw);
        Assert.InRange(nwSlot, 0, 3);

        var w = set.VertexWeights[4 * 33 + 4];  // vy=4, vx=4
        // Walk all 4 slots; the BtxtNw slot has ~1, the others have ~0.
        for (var s = 0; s < 4; s++)
        {
            var weight = s switch { 0 => w.X, 1 => w.Y, 2 => w.Z, _ => w.W };
            if (s == nwSlot) Assert.Equal(1f, weight, 4);
            else Assert.Equal(0f, weight, 4);
        }
    }

    [Fact]
    public void CellCenterVertex_BlendsTwentyFivePercentAcrossAllFourSlots()
    {
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0), BtxtLayer(BtxtSe, 1), BtxtLayer(BtxtNw, 2), BtxtLayer(BtxtNe, 3)
        };
        var table = CellLayerWeightTable.Build(layers);
        var set = CellTerrainTextureSet.Project(table);

        Assert.NotNull(set);
        var w = set!.VertexWeights[16 * 33 + 16];  // cell center
        // Sum should be ~1, each slot should be ~0.25.
        Assert.Equal(1f, w.X + w.Y + w.Z + w.W, 4);
        Assert.Equal(0.25f, w.X, 4);
        Assert.Equal(0.25f, w.Y, 4);
        Assert.Equal(0.25f, w.Z, 4);
        Assert.Equal(0.25f, w.W, 4);
    }

    [Fact]
    public void MoreThanFourLtexs_KeepsTopFourByTotalWeight()
    {
        // Five distinct BTXTs would mean one quadrant has 2 BTXTs — not legal in real data,
        // but ATXTs can create the same situation: a quadrant with BTXT + 3 ATXTs across 4
        // quadrants gives 4 + 12 = 16 LTEXs. Easier to fabricate the truncation case with a
        // synthetic spread of ATXTs that each cover a single vertex with tiny opacity.
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0), BtxtLayer(BtxtSe, 1), BtxtLayer(BtxtNw, 2), BtxtLayer(BtxtNe, 3),
            // Q0 ATXT covering ONE vertex with high opacity — its total cell-wide weight is
            // ~1, but each of the 4 BTXTs has total ~272 (17×17 vertices). So the BTXTs
            // should dominate the top-4; the ATXT gets dropped.
            new LandTextureLayer
            {
                Kind = LandTextureLayerKind.Alpha,
                TextureFormId = 0xDEAD,
                Quadrant = 0,
                Layer = 0,
                BlendEntries = new List<LandTextureBlendEntry>
                {
                    new(Position: 5 * 17 + 5, Unused0: 0, Unused1: 0, Opacity: 1.0f),
                }
            }
        };
        var table = CellLayerWeightTable.Build(layers);

        var set = CellTerrainTextureSet.Project(table);

        Assert.NotNull(set);
        Assert.Equal(4, set!.ActiveSlotCount);
        // The 4 BTXTs (each covering 1/4 of the cell) outweigh the single-vertex ATXT.
        Assert.DoesNotContain(0xDEADu, set.SlotFormIds);
        Assert.Equal(new[] { BtxtSw, BtxtSe, BtxtNw, BtxtNe }.ToHashSet(),
                     set.SlotFormIds.ToHashSet());
    }

    [Fact]
    public void NullTable_ReturnsNull()
    {
        Assert.Null(CellTerrainTextureSet.Project(null));
    }

    private static LandTextureLayer BtxtLayer(uint formId, byte quadrant) => new()
    {
        Kind = LandTextureLayerKind.Base,
        TextureFormId = formId,
        Quadrant = quadrant,
        Layer = 0,
    };
}
