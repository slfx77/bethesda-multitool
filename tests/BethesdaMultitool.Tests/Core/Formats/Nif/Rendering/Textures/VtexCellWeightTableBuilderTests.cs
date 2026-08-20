using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

public sealed class VtexCellWeightTableBuilderTests
{
    [Fact]
    public void WorldMapTextureBlitter_UsesSharedBoundaryBuilder()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldMap", "Rendering",
            "WorldMapTextureBlitter.cs");
        var vtexBranch = SourceContract.Extract(
            source,
            "if (cell.LandVisualData?.VtexTextureFormIds is { Length: > 0 } vtex)",
            "else if (hasOwnLayers || hasRealNeighbor)");

        Assert.Contains("VtexCellWeightTableBuilder.Build(", vtexBranch, StringComparison.Ordinal);
        Assert.Contains("cellByGrid);", vtexBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("CellLayerWeightTable.BuildFromVtexGrid", vtexBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UsesAllThreePositiveEdgeNeighbors()
    {
        const uint ownBase = 0xF1000000u;
        const uint eastBase = 0xF2000000u;
        const uint northBase = 0xF3000000u;
        const uint northEastBase = 0xF4000000u;

        var own = TextureGrid(ownBase);
        var origin = Cell(0, 0, own);
        var cells = new Dictionary<(int gx, int gy), CellRecord>
        {
            [(0, 0)] = origin,
            [(1, 0)] = Cell(1, 0, TextureGrid(eastBase)),
            [(0, 1)] = Cell(0, 1, TextureGrid(northBase)),
            [(1, 1)] = Cell(1, 1, TextureGrid(northEastBase))
        };

        var table = VtexCellWeightTableBuilder.Build(
            CellLayerWeightTable.CellVertexCount,
            (0, 0),
            origin.LandVisualData!.VtexTextureFormIds!,
            cells);

        const int last = CellLayerWeightTable.CellVertexCount - 1;
        const int midVertex = 17;
        const int eastExpectedRow = 7; // (last - midVertex) * 16 / last
        const int northExpectedColumn = 8; // midVertex * 16 / last
        Assert.Equal(GridValue(eastBase, eastExpectedRow, 0), table.At(last, midVertex).E0.FormId);
        Assert.Equal(GridValue(northBase, 0, northExpectedColumn), table.At(midVertex, 0).E0.FormId);
        Assert.Equal(GridValue(northEastBase, 0, 0), table.At(last, 0).E0.FormId);
        Assert.Equal(GridValue(ownBase, eastExpectedRow, 15), table.At(last - 1, midVertex).E0.FormId);
        Assert.Equal(GridValue(ownBase, 15, northExpectedColumn), table.At(midVertex, 1).E0.FormId);
    }

    [Fact]
    public void Build_MissingNeighborsClampsToOwnGrid()
    {
        const uint ownBase = 0xF5000000u;
        var origin = Cell(0, 0, TextureGrid(ownBase));

        var table = VtexCellWeightTableBuilder.Build(
            CellLayerWeightTable.CellVertexCount,
            (0, 0),
            origin.LandVisualData!.VtexTextureFormIds!,
            new Dictionary<(int gx, int gy), CellRecord> { [(0, 0)] = origin });

        const int last = CellLayerWeightTable.CellVertexCount - 1;
        const int midVertex = 17;
        Assert.Equal(GridValue(ownBase, 7, 15), table.At(last, midVertex).E0.FormId);
        Assert.Equal(GridValue(ownBase, 15, 8), table.At(midVertex, 0).E0.FormId);
        Assert.Equal(GridValue(ownBase, 15, 15), table.At(last, 0).E0.FormId);
    }

    private static CellRecord Cell(int gx, int gy, uint[] grid)
    {
        return new CellRecord
        {
            GridX = gx,
            GridY = gy,
            LandVisualData = new LandVisualData { VtexTextureFormIds = grid }
        };
    }

    private static uint[] TextureGrid(uint baseId)
    {
        return Enumerable.Range(0, 16 * 16).Select(i => baseId + (uint)i).ToArray();
    }

    private static uint GridValue(uint baseId, int row, int column)
    {
        return baseId + (uint)(row * 16 + column);
    }
}