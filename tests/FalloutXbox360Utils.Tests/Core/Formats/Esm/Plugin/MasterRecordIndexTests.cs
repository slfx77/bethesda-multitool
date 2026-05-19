using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public sealed class MasterRecordIndexTests
{
    [Fact]
    public void Build_ChildLocations_UsesCellChildGrupLabel()
    {
        var records = new List<ParsedMainRecord>
        {
            Record("WRLD", 0x60, 24),
            Record("CELL", 0xC001, 72),
            Record("REFR", 0xAA01, 148),
            Record("ACHR", 0xAA02, 188),
            Record("LAND", 0xAA03, 260),
            Record("NAVM", 0xAA04, 300)
        };
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(0, 1000, "WRLD"u8.ToArray(), 0),
            MakeGrup(48, 900, LeBytes(0x60), 1),
            MakeGrup(96, 180, LeBytes(0xC001), 6),
            MakeGrup(120, 100, LeBytes(0xC001), 8),
            MakeGrup(228, 120, LeBytes(0xC001), 9)
        };

        var index = MasterRecordIndex.Build(records, grupHeaders);

        Assert.Equal(0xC001u, index.RefToCell[0xAA01]);
        Assert.Equal(0xC001u, index.RefToCell[0xAA02]);
        Assert.Equal(8, index.ChildLocations[0xAA01].GroupType);
        Assert.Equal(8, index.ChildLocations[0xAA02].GroupType);
        Assert.Equal(9, index.ChildLocations[0xAA03].GroupType);
        Assert.Equal(9, index.ChildLocations[0xAA04].GroupType);
        Assert.Equal(new List<uint> { 0xAA03u }, index.LandsByCell[0xC001]);
        Assert.Equal(new List<uint> { 0xAA04u }, index.NavmsByCell[0xC001]);
    }

    [Fact]
    public void Build_RefToCell_DoesNotIncludeLandOrNavm()
    {
        var records = new List<ParsedMainRecord>
        {
            Record("WRLD", 0x60, 24),
            Record("CELL", 0xC001, 72),
            Record("LAND", 0xAA03, 148),
            Record("NAVM", 0xAA04, 188)
        };
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(0, 1000, "WRLD"u8.ToArray(), 0),
            MakeGrup(48, 900, LeBytes(0x60), 1),
            MakeGrup(96, 180, LeBytes(0xC001), 6),
            MakeGrup(120, 100, LeBytes(0xC001), 9)
        };

        var index = MasterRecordIndex.Build(records, grupHeaders);

        Assert.DoesNotContain(0xAA03u, index.RefToCell.Keys);
        Assert.DoesNotContain(0xAA04u, index.RefToCell.Keys);
        Assert.Equal(new List<uint> { 0xAA03u }, index.LandsByCell[0xC001]);
        Assert.Equal(new List<uint> { 0xAA04u }, index.NavmsByCell[0xC001]);
    }

    private static ParsedMainRecord Record(string signature, uint formId, long offset)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = signature, FormId = formId, Version = 0x000F },
            Offset = offset,
            Subrecords = []
        };
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
