using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

public class EsmFileAnalyzerTests
{
    private static GrupHeaderInfo MakeGrup(long offset, uint size, uint labelFormId, int groupType)
    {
        var label = BitConverter.GetBytes(labelFormId);
        return new GrupHeaderInfo
        {
            Offset = offset,
            GroupSize = size,
            Label = label,
            GroupType = groupType
        };
    }

    private static ParsedMainRecord MakeRecord(string signature, long offset, uint formId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId
            },
            Offset = offset
        };
    }

    [Fact]
    public void BuildAllMaps_CellAndLandInWorldChildren_MapsToWorldspace()
    {
        // GRUP type 1 (World Children) for worldspace 0x0003C2EC at offset 1000, size 500
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(1000, 500, 0x0003C2EC, groupType: 1)
        };

        var records = new List<ParsedMainRecord>
        {
            MakeRecord("CELL", 1050, 0x00001001),
            MakeRecord("LAND", 1200, 0x00001002),
            MakeRecord("CELL", 2000, 0x00001003) // Outside the GRUP — should NOT map
        };

        var (cellToWorld, landToWorld, cellToRefr, topicToInfo) =
            EsmFileAnalyzer.BuildAllMaps(records, grupHeaders);

        // CELL at 1050 is inside [1000..1500) → mapped to worldspace 0x0003C2EC
        Assert.Single(cellToWorld);
        Assert.Equal(0x0003C2ECu, cellToWorld[0x00001001u]);

        // LAND at 1200 is inside [1000..1500) → mapped to worldspace
        Assert.Single(landToWorld);
        Assert.Equal(0x0003C2ECu, landToWorld[0x00001002u]);

        // CELL at 2000 is outside — not mapped
        Assert.False(cellToWorld.ContainsKey(0x00001003u));

        // No REFR/INFO records → empty maps
        Assert.Empty(cellToRefr);
        Assert.Empty(topicToInfo);
    }

    [Fact]
    public void BuildAllMaps_RefrInCellChildren_MapsToCellFormId()
    {
        // GRUP type 9 (Cell Temporary Children) for cell 0x0000A001 at offset 2000, size 300
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(2000, 300, 0x0000A001, groupType: 9)
        };

        var records = new List<ParsedMainRecord>
        {
            MakeRecord("REFR", 2100, 0x0000B001),
            MakeRecord("ACHR", 2150, 0x0000B002),
            MakeRecord("ACRE", 2200, 0x0000B003)
        };

        var (cellToWorld, landToWorld, cellToRefr, topicToInfo) =
            EsmFileAnalyzer.BuildAllMaps(records, grupHeaders);

        // All three REFR/ACHR/ACRE should be mapped to cell 0x0000A001
        Assert.Single(cellToRefr);
        Assert.True(cellToRefr.ContainsKey(0x0000A001u));
        Assert.Equal(3, cellToRefr[0x0000A001u].Count);
        Assert.Contains(0x0000B001u, cellToRefr[0x0000A001u]);
        Assert.Contains(0x0000B002u, cellToRefr[0x0000A001u]);
        Assert.Contains(0x0000B003u, cellToRefr[0x0000A001u]);

        Assert.Empty(cellToWorld);
        Assert.Empty(landToWorld);
        Assert.Empty(topicToInfo);
    }

    [Fact]
    public void BuildAllMaps_InfoInTopicChildren_MapsToDial()
    {
        // GRUP type 7 (Topic Children) for DIAL 0x000D0001 at offset 5000, size 200
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(5000, 200, 0x000D0001, groupType: 7)
        };

        var records = new List<ParsedMainRecord>
        {
            MakeRecord("INFO", 5050, 0x000E0001),
            MakeRecord("INFO", 5100, 0x000E0002)
        };

        var (cellToWorld, landToWorld, cellToRefr, topicToInfo) =
            EsmFileAnalyzer.BuildAllMaps(records, grupHeaders);

        Assert.Single(topicToInfo);
        Assert.True(topicToInfo.ContainsKey(0x000D0001u));
        Assert.Equal(2, topicToInfo[0x000D0001u].Count);
        Assert.Contains(0x000E0001u, topicToInfo[0x000D0001u]);
        Assert.Contains(0x000E0002u, topicToInfo[0x000D0001u]);

        Assert.Empty(cellToWorld);
        Assert.Empty(landToWorld);
        Assert.Empty(cellToRefr);
    }

    [Fact]
    public void BuildAllMaps_EmptyInputs_ReturnsEmptyMaps()
    {
        var (cellToWorld, landToWorld, cellToRefr, topicToInfo) =
            EsmFileAnalyzer.BuildAllMaps([], []);

        Assert.Empty(cellToWorld);
        Assert.Empty(landToWorld);
        Assert.Empty(cellToRefr);
        Assert.Empty(topicToInfo);
    }

    [Fact]
    public void BuildAllMaps_MultipleGrupTypes_AllMapsPopulated()
    {
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(100, 500, 0x0001, groupType: 1),   // World Children
            MakeGrup(700, 300, 0x0002, groupType: 8),    // Cell Persistent Children
            MakeGrup(1100, 200, 0x0003, groupType: 7)    // Topic Children
        };

        var records = new List<ParsedMainRecord>
        {
            MakeRecord("CELL", 200, 0x1001),
            MakeRecord("LAND", 300, 0x1002),
            MakeRecord("REFR", 800, 0x2001),
            MakeRecord("INFO", 1150, 0x3001)
        };

        var (cellToWorld, landToWorld, cellToRefr, topicToInfo) =
            EsmFileAnalyzer.BuildAllMaps(records, grupHeaders);

        Assert.Single(cellToWorld);
        Assert.Equal(0x0001u, cellToWorld[0x1001u]);

        Assert.Single(landToWorld);
        Assert.Equal(0x0001u, landToWorld[0x1002u]);

        Assert.Single(cellToRefr);
        Assert.Single(cellToRefr[0x0002u]);
        Assert.Equal(0x2001u, cellToRefr[0x0002u][0]);

        Assert.Single(topicToInfo);
        Assert.Single(topicToInfo[0x0003u]);
        Assert.Equal(0x3001u, topicToInfo[0x0003u][0]);
    }

    [Fact]
    public void BuildAllMaps_UnmatchedRecordTypes_AreIgnored()
    {
        // Only World Children GRUP, but records are WEAP/NPC_ (not CELL/LAND/REFR/INFO)
        var grupHeaders = new List<GrupHeaderInfo>
        {
            MakeGrup(100, 500, 0x0001, groupType: 1)
        };

        var records = new List<ParsedMainRecord>
        {
            MakeRecord("WEAP", 200, 0xF001),
            MakeRecord("NPC_", 300, 0xF002)
        };

        var (cellToWorld, landToWorld, cellToRefr, topicToInfo) =
            EsmFileAnalyzer.BuildAllMaps(records, grupHeaders);

        Assert.Empty(cellToWorld);
        Assert.Empty(landToWorld);
        Assert.Empty(cellToRefr);
        Assert.Empty(topicToInfo);
    }
}
