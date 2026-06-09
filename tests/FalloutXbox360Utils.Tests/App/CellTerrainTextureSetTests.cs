using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

/// <summary>
///     Verifies the per-cell texture-slot projection the 3D renderer consumes. The variable-length
///     <see cref="CellLayerWeightTable" /> output is projected into up to
///     <see cref="CellTerrainTextureSet.MaxSlots" /> (16) LTEX slots + a per-vertex weight vector
///     (packed as <see cref="CellTerrainTextureSet.SlotVectors" /> Vector4s). Sixteen slots match
///     the 2D blit's per-pixel layer ceiling, so the projection is non-lossy for real cells.
/// </summary>
public sealed class CellTerrainTextureSetTests
{
    private const uint BtxtSw = 0x10001;
    private const uint BtxtSe = 0x10002;
    private const uint BtxtNw = 0x10003;
    private const uint BtxtNe = 0x10004;

    [Fact]
    public void FourBtxts_OneQuadrantEach_PopulatesExactlyFourSlots()
    {
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0), BtxtLayer(BtxtSe, 1), BtxtLayer(BtxtNw, 2), BtxtLayer(BtxtNe, 3)
        };
        var table = CellLayerWeightTable.Build(layers);

        var set = CellTerrainTextureSet.Project(table);

        Assert.NotNull(set);
        Assert.Equal(4, set!.ActiveSlotCount);
        // Only the active slots hold the four BTXTs; unused slots stay 0 (don't include them).
        Assert.Equal(new[] { BtxtSw, BtxtSe, BtxtNw, BtxtNe }.ToHashSet(),
            set.SlotFormIds.Take(set.ActiveSlotCount).ToHashSet());
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
        // NW interior vertex (4, 4): full weight on the BtxtNw slot, 0 on every other slot.
        var nwSlot = Array.IndexOf(set!.SlotFormIds, BtxtNw);
        Assert.InRange(nwSlot, 0, CellTerrainTextureSet.MaxSlots - 1);

        var w = ReadVertexWeights(set, 4, 4);
        for (var s = 0; s < CellTerrainTextureSet.MaxSlots; s++)
        {
            if (s == nwSlot) Assert.Equal(1f, w[s], 4);
            else Assert.Equal(0f, w[s], 4);
        }
    }

    [Fact]
    public void CellCenterVertex_BlendsTwentyFivePercentAcrossTheFourQuadrantSlots()
    {
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0), BtxtLayer(BtxtSe, 1), BtxtLayer(BtxtNw, 2), BtxtLayer(BtxtNe, 3)
        };
        var table = CellLayerWeightTable.Build(layers);
        var set = CellTerrainTextureSet.Project(table);

        Assert.NotNull(set);
        var w = ReadVertexWeights(set!, 16, 16); // cell center — all 4 quadrants meet
        var total = 0f;
        foreach (var btxt in new[] { BtxtSw, BtxtSe, BtxtNw, BtxtNe })
        {
            var slot = Array.IndexOf(set!.SlotFormIds, btxt);
            Assert.InRange(slot, 0, CellTerrainTextureSet.MaxSlots - 1);
            Assert.Equal(0.25f, w[slot], 4);
        }

        for (var s = 0; s < CellTerrainTextureSet.MaxSlots; s++) total += w[s];
        Assert.Equal(1f, total, 4);
    }

    [Fact]
    public void MoreThanFourLtexs_KeepsAllLayers_NonLossy()
    {
        // Four BTXTs (one per quadrant) + a single-vertex ATXT = 5 distinct LTEXs. The old
        // 4-slot projection dropped the lightest; the 16-slot projection keeps every layer.
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0), BtxtLayer(BtxtSe, 1), BtxtLayer(BtxtNw, 2), BtxtLayer(BtxtNe, 3),
            new()
            {
                Kind = LandTextureLayerKind.Alpha,
                TextureFormId = 0xDEAD,
                Quadrant = 0,
                Layer = 0,
                BlendEntries = new List<LandTextureBlendEntry>
                {
                    new(5 * 17 + 5, 0, 0, 1.0f)
                }
            }
        };
        var table = CellLayerWeightTable.Build(layers);

        var set = CellTerrainTextureSet.Project(table);

        Assert.NotNull(set);
        Assert.Equal(5, set!.ActiveSlotCount);
        Assert.Contains(0xDEADu, set.SlotFormIds);
        Assert.Equal(new[] { BtxtSw, BtxtSe, BtxtNw, BtxtNe, 0xDEADu }.ToHashSet(),
            set.SlotFormIds.Take(set.ActiveSlotCount).ToHashSet());
    }

    [Fact]
    public void NullTable_ReturnsNull()
    {
        Assert.Null(CellTerrainTextureSet.Project(null));
    }

    private static float[] ReadVertexWeights(CellTerrainTextureSet set, int vx, int vy)
    {
        var vertexIndex = vy * CellLayerWeightTable.CellVertexCount + vx;
        var w = new float[CellTerrainTextureSet.MaxSlots];
        for (var k = 0; k < CellTerrainTextureSet.SlotVectors; k++)
        {
            var v = set.VertexWeights[vertexIndex * CellTerrainTextureSet.SlotVectors + k];
            w[k * 4 + 0] = v.X;
            w[k * 4 + 1] = v.Y;
            w[k * 4 + 2] = v.Z;
            w[k * 4 + 3] = v.W;
        }

        return w;
    }

    private static LandTextureLayer BtxtLayer(uint formId, byte quadrant)
    {
        return new LandTextureLayer
        {
            Kind = LandTextureLayerKind.Base,
            TextureFormId = formId,
            Quadrant = quadrant,
            Layer = 0
        };
    }
}