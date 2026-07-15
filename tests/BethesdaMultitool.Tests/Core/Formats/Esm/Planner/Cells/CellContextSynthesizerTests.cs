using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class CellContextSynthesizerTests
{
    [Theory]
    [InlineData(-33, -33, -2, -2, -5, -5)]
    [InlineData(-32, -32, -1, -1, -4, -4)]
    [InlineData(-9, -9, -1, -1, -2, -2)]
    [InlineData(-8, -8, -1, -1, -1, -1)]
    [InlineData(-1, -1, -1, -1, -1, -1)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(7, 7, 0, 0, 0, 0)]
    [InlineData(8, 8, 0, 0, 1, 1)]
    [InlineData(31, 31, 0, 0, 3, 3)]
    [InlineData(32, 32, 1, 1, 4, 4)]
    public void Exterior_Grid_Uses_Floor_Division(
        int gridX, int gridY,
        int expectedBlockX, int expectedBlockY,
        int expectedSubblockX, int expectedSubblockY)
    {
        var context = CellContextSynthesizer.Synthesize(Entry(new CellRecord
        {
            FormId = 0x0010B900,
            WorldspaceFormId = 0x0010B96F,
            GridX = gridX,
            GridY = gridY,
        }));

        Assert.False(context.IsPersistentCellContainer);
        Assert.Equal(Pack(expectedBlockX, expectedBlockY), ReadLabel(context.BlockLabel));
        Assert.Equal(Pack(expectedSubblockX, expectedSubblockY), ReadLabel(context.SubblockLabel));
    }

    [Fact]
    public void Mixed_Signed_Coordinates_Pack_Y_Low_X_High()
    {
        var context = CellContextSynthesizer.Synthesize(Entry(new CellRecord
        {
            FormId = 0x0010B901,
            WorldspaceFormId = 0x0010B96F,
            GridX = -33,
            GridY = 17,
        }));

        Assert.Equal(0xFFFE0000u, ReadLabel(context.BlockLabel));
        Assert.Equal(0xFFFB0002u, ReadLabel(context.SubblockLabel));
    }

    [Fact]
    public void Gomorrah_Grid_Minus1_3_Uses_Ffff0000_At_Both_Levels()
    {
        var context = CellContextSynthesizer.Synthesize(Entry(new CellRecord
        {
            FormId = 0x01001B75,
            WorldspaceFormId = 0x0010B96F,
            GridX = -1,
            GridY = 3,
        }));

        Assert.Equal(0xFFFF0000u, ReadLabel(context.BlockLabel));
        Assert.Equal(0xFFFF0000u, ReadLabel(context.SubblockLabel));
    }

    [Fact]
    public void Persistent_Cell_Wins_Over_Zero_Grid_Coordinates()
    {
        var context = CellContextSynthesizer.Synthesize(Entry(new CellRecord
        {
            FormId = 0x0010B902,
            WorldspaceFormId = 0x0010B96F,
            GridX = 0,
            GridY = 0,
            IsPersistentCell = true,
        }));

        Assert.True(context.IsPersistentCellContainer);
        Assert.Equal(0, context.BlockGroupType);
        Assert.Equal(0, context.SubblockGroupType);
        Assert.Null(context.BlockLabel);
        Assert.Null(context.SubblockLabel);
    }

    [Fact]
    public void Interior_And_Virtual_Cells_Retain_Label_Less_Contexts()
    {
        var interior = CellContextSynthesizer.Synthesize(Entry(new CellRecord
        {
            FormId = 0x0010B903,
            Flags = 0x01,
        }));
        var virtualCell = CellContextSynthesizer.Synthesize(Entry(new CellRecord
        {
            FormId = 0x0010B904,
            WorldspaceFormId = 0x0010B96F,
            IsVirtual = true,
        }));

        Assert.True(interior.IsInterior);
        Assert.Equal(2, interior.BlockGroupType);
        Assert.Equal(3, interior.SubblockGroupType);
        Assert.Null(interior.BlockLabel);
        Assert.False(virtualCell.IsPersistentCellContainer);
        Assert.Equal(4, virtualCell.BlockGroupType);
        Assert.Equal(5, virtualCell.SubblockGroupType);
        Assert.Null(virtualCell.BlockLabel);
    }

    [Fact]
    public void Ordinary_Exterior_Without_Complete_Grid_Fails_With_FormId()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CellContextSynthesizer.Synthesize(Entry(new CellRecord
            {
                FormId = 0x0010B905,
                WorldspaceFormId = 0x0010B96F,
                GridX = 1,
            })));

        Assert.Contains("0x0010B905", error.Message, StringComparison.Ordinal);
    }

    private static CellCatalogEntry Entry(CellRecord cell) => new()
    {
        CellFormId = cell.FormId,
        Source = SourceKind.DmpNew,
        DmpModel = cell,
    };

    private static uint ReadLabel(byte[]? bytes)
    {
        Assert.NotNull(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static uint Pack(int x, int y) => unchecked((ushort)y | ((uint)(ushort)x << 16));
}
