using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class CellCatalogTests
{
    [Fact]
    public void Pairs_Master_Cell_With_Dmp_Override()
    {
        var ctx = MakeInteriorContext(0x000ABCDE);
        var masterRec = MakeMasterCellRecord(0x000ABCDE);
        var dmp = new CellRecord { FormId = 0x000ABCDE, EditorId = "TestCell" };

        var entries = CellCatalog.Build(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = ctx },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = masterRec },
            new List<CellRecord> { dmp });

        var entry = Assert.Single(entries);
        Assert.Equal(SourceKind.DmpOverride, entry.Source);
        Assert.Same(ctx, entry.MasterContext);
        Assert.Same(dmp, entry.DmpModel);
    }

    [Fact]
    public void Master_Only_When_No_Dmp_Match()
    {
        var ctx = MakeInteriorContext(0x000ABCDE);
        var masterRec = MakeMasterCellRecord(0x000ABCDE);

        var entries = CellCatalog.Build(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = ctx },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = masterRec },
            new List<CellRecord>());

        var entry = Assert.Single(entries);
        Assert.Equal(SourceKind.MasterOnly, entry.Source);
        Assert.NotNull(entry.MasterContext);
        Assert.Null(entry.DmpModel);
    }

    [Fact]
    public void Dmp_New_When_Not_In_Master()
    {
        var dmp = new CellRecord { FormId = 0x01000801, EditorId = "BrandNewCell" };

        var entries = CellCatalog.Build(
            new Dictionary<uint, PcEsmCellContext>(),
            new Dictionary<uint, ParsedMainRecord>(),
            new List<CellRecord> { dmp });

        var entry = Assert.Single(entries);
        Assert.Equal(SourceKind.DmpNew, entry.Source);
        Assert.Null(entry.MasterContext);
        Assert.Null(entry.MasterRecord);
        Assert.Same(dmp, entry.DmpModel);
    }

    private static PcEsmCellContext MakeInteriorContext(uint cellFormId) => new()
    {
        CellFormId = cellFormId,
        IsInterior = true,
        BlockGroupType = 2,
        SubblockGroupType = 3,
        BlockLabel = [1, 0, 0, 0],
        SubblockLabel = [2, 0, 0, 0],
    };

    private static ParsedMainRecord MakeMasterCellRecord(uint cellFormId) => new()
    {
        Header = new MainRecordHeader
        {
            Signature = "CELL",
            DataSize = 0,
            Flags = 0,
            FormId = cellFormId,
            Timestamp = 0,
            VcsInfo = 0,
            Version = 15,
        },
        Offset = 0,
    };
}
