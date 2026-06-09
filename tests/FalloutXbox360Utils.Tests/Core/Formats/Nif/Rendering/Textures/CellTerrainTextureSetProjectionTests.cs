using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering.Textures;

public sealed class CellTerrainTextureSetProjectionTests
{
    private const int VxA = 5, VyA = 5; // an arbitrary interior vertex

    private static int TableIndex(int vx, int vy)
    {
        return vy * CellLayerWeightTable.CellVertexCount + vx;
    }

    [Fact]
    public void Project_KeepsAllLayersUpToSixteen_NonLossy()
    {
        // Six distinct LTEXs at one vertex with weights summing to 1. Before the 16-slot
        // change the 4-slot projection would have dropped the two lightest; now all six survive.
        var table = new CellLayerWeightTable();
        var formIds = new uint[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15 };
        var weights = new[] { 0.30f, 0.25f, 0.20f, 0.12f, 0.08f, 0.05f };
        ref var vertex = ref table.At(VxA, VyA);
        for (var i = 0; i < formIds.Length; i++) vertex.Add(formIds[i], weights[i]);

        var set = CellTerrainTextureSet.Project(table);

        Assert.NotNull(set);
        Assert.Equal(6, set!.ActiveSlotCount);

        // Each FormID survived and its weight is preserved at the right slot.
        var w = ReadVertexWeights(set, TableIndex(VxA, VyA));
        var total = 0f;
        for (var i = 0; i < formIds.Length; i++)
        {
            var slot = Array.IndexOf(set.SlotFormIds, formIds[i]);
            Assert.InRange(slot, 0, CellTerrainTextureSet.MaxSlots - 1);
            Assert.Equal(weights[i], w[slot], 3);
        }

        for (var s = 0; s < CellTerrainTextureSet.MaxSlots; s++) total += w[s];
        Assert.Equal(1f, total, 3);
    }

    [Fact]
    public void Project_TruncatesToSixteenAndRenormalizes_WhenMoreThanSixteenDistinct()
    {
        var table = new CellLayerWeightTable();
        ref var vertex = ref table.At(VxA, VyA);
        const int distinct = 20;
        for (uint i = 0; i < distinct; i++) vertex.Add(0x100 + i, 1f / distinct);

        var set = CellTerrainTextureSet.Project(table);

        Assert.NotNull(set);
        Assert.Equal(CellTerrainTextureSet.MaxSlots, set!.ActiveSlotCount); // capped at 16

        // The kept slots are renormalized so the per-vertex weight vector still sums to ~1.
        var w = ReadVertexWeights(set, TableIndex(VxA, VyA));
        var total = 0f;
        for (var s = 0; s < CellTerrainTextureSet.MaxSlots; s++) total += w[s];
        Assert.Equal(1f, total, 3);
    }

    [Fact]
    public void Project_NullTableReturnsNull()
    {
        Assert.Null(CellTerrainTextureSet.Project(null));
    }

    private static float[] ReadVertexWeights(CellTerrainTextureSet set, int vertexIndex)
    {
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
}