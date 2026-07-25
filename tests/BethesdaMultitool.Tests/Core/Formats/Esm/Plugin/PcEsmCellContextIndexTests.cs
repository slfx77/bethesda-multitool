using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public class PcEsmCellContextIndexTests
{
    [Fact]
    public void Build_InteriorCell_PicksUpBlockAndSubblockLabels()
    {
        // Synthetic GRUP stack mimicking interior cell layout:
        //   GRUP type=0 label="CELL" at offset 0, size 1000
        //     GRUP type=2 label=blockNum=5 at offset 24, size 800
        //       GRUP type=3 label=subblockNum=7 at offset 48, size 600
        //         CELL record FormId=0xABC at offset 72
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(0, 1000, "CELL"u8.ToArray(), 0),
            MakeGrup(24, 800, LeBytes(5u), 2),
            MakeGrup(48, 600, LeBytes(7u), 3)
        };
        var records = new List<ParsedMainRecord>
        {
            new()
            {
                Header = new MainRecordHeader { Signature = "CELL", FormId = 0xABC, Version = 0x000F },
                Offset = 72,
                Subrecords = []
            }
        };

        var index = PcEsmCellContextIndex.Build(records, grupHeaders);

        var ctx = Assert.Contains(0xABCu, (IDictionary<uint, PcEsmCellContext>)index);
        Assert.True(ctx.IsInterior);
        Assert.Null(ctx.WorldspaceFormId);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(ctx.BlockLabel!));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(ctx.SubblockLabel!));
        Assert.Equal(2, ctx.BlockGroupType);
        Assert.Equal(3, ctx.SubblockGroupType);
    }

    [Fact]
    public void Build_ExteriorCell_PicksUpWorldspaceAndExteriorBlockTypes()
    {
        // Synthetic GRUP stack mimicking exterior cell layout:
        //   GRUP type=0 label="WRLD" at offset 0, size 2000
        //     WRLD record FormId=0x60 at offset 24
        //     GRUP type=1 label=0x60 at offset 48, size 1900
        //       GRUP type=4 label=blockKey=0x12345 at offset 72, size 1500
        //         GRUP type=5 label=subKey=0x98765 at offset 96, size 1200
        //           CELL record FormId=0xDEAD at offset 120
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(0, 2000, "WRLD"u8.ToArray(), 0),
            MakeGrup(48, 1900, LeBytes(0x60u), 1),
            MakeGrup(72, 1500, LeBytes(0x12345u), 4),
            MakeGrup(96, 1200, LeBytes(0x98765u), 5)
        };
        var records = new List<ParsedMainRecord>
        {
            new()
            {
                Header = new MainRecordHeader { Signature = "WRLD", FormId = 0x60, Version = 0x000F },
                Offset = 24
            },
            new()
            {
                Header = new MainRecordHeader { Signature = "CELL", FormId = 0xDEAD, Version = 0x000F },
                Offset = 120,
                Subrecords = []
            }
        };

        var index = PcEsmCellContextIndex.Build(records, grupHeaders);

        var ctx = Assert.Contains(0xDEADu, (IDictionary<uint, PcEsmCellContext>)index);
        Assert.False(ctx.IsInterior);
        Assert.Equal(0x60u, ctx.WorldspaceFormId);
        Assert.Equal(0x12345u, BinaryPrimitives.ReadUInt32LittleEndian(ctx.BlockLabel!));
        Assert.Equal(0x98765u, BinaryPrimitives.ReadUInt32LittleEndian(ctx.SubblockLabel!));
        Assert.Equal(4, ctx.BlockGroupType);
        Assert.Equal(5, ctx.SubblockGroupType);
        Assert.False(ctx.IsPersistentCellContainer);
    }

    [Fact]
    public void Build_PersistentCellContainer_HasNoBlockOrSubblock()
    {
        // Persistent cell layout: directly under world children GRUP, no block/subblock wrapper.
        //   GRUP type=0 "WRLD" at 0..2000
        //     WRLD record at 24
        //     GRUP type=1 label=wrldFormId at 48..1900
        //       CELL record (persistent container) at 72
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(0, 2000, "WRLD"u8.ToArray(), 0),
            MakeGrup(48, 1900, LeBytes(0x42u), 1)
        };
        var records = new List<ParsedMainRecord>
        {
            new()
            {
                Header = new MainRecordHeader { Signature = "WRLD", FormId = 0x42, Version = 0x000F },
                Offset = 24
            },
            new()
            {
                Header = new MainRecordHeader { Signature = "CELL", FormId = 0xC001, Version = 0x000F },
                Offset = 72
            }
        };

        var index = PcEsmCellContextIndex.Build(records, grupHeaders);

        var ctx = Assert.Contains(0xC001u, (IDictionary<uint, PcEsmCellContext>)index);
        Assert.False(ctx.IsInterior);
        Assert.Equal(0x42u, ctx.WorldspaceFormId);
        Assert.Null(ctx.BlockLabel);
        Assert.Null(ctx.SubblockLabel);
        Assert.True(ctx.IsPersistentCellContainer);
    }

    [Fact]
    public void Build_PopsGrupsWhenLeavingTheirRegion()
    {
        // Two adjacent interior cells in different blocks must each pick up their own block.
        //   GRUP type=0 "CELL" at 0..2000
        //     GRUP type=2 block=1 at 24..500    (cell A inside)
        //       GRUP type=3 sub=0 at 48..400    (cell A inside)
        //         CELL FormId=0x100 at 72
        //     GRUP type=2 block=2 at 500..1000  (cell B inside, different block)
        //       GRUP type=3 sub=1 at 524..900
        //         CELL FormId=0x200 at 548
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(0, 2000, "CELL"u8.ToArray(), 0),
            MakeGrup(24, 476, LeBytes(1u), 2),
            MakeGrup(48, 352, LeBytes(0u), 3),
            MakeGrup(500, 500, LeBytes(2u), 2),
            MakeGrup(524, 376, LeBytes(1u), 3)
        };
        var records = new List<ParsedMainRecord>
        {
            new()
            {
                Header = new MainRecordHeader { Signature = "CELL", FormId = 0x100, Version = 0x000F }, Offset = 72
            },
            new()
            {
                Header = new MainRecordHeader { Signature = "CELL", FormId = 0x200, Version = 0x000F }, Offset = 548
            }
        };

        var index = PcEsmCellContextIndex.Build(records, grupHeaders);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(index[0x100].BlockLabel!));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(index[0x100].SubblockLabel!));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(index[0x200].BlockLabel!));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(index[0x200].SubblockLabel!));
    }

    private static GrupHeaderInfo MakeGrup(long offset, uint size, byte[] label, int groupType)
    {
        return new GrupHeaderInfo
        {
            Offset = offset,
            GroupSize = size,
            Label = label,
            GroupType = groupType
        };
    }

    private static byte[] LeBytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}