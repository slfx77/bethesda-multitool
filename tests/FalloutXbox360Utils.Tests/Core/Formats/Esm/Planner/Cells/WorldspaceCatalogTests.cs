using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class WorldspaceCatalogTests
{
    [Fact]
    public void Master_Wrld_With_No_Dmp_Capture_Marked_MasterOnly()
    {
        var cells = new List<CellCatalogEntry>
        {
            new()
            {
                CellFormId = 0x000ABCDE,
                Source = SourceKind.MasterOnly,
                MasterContext = MakeExteriorContext(0x000ABCDE, worldspaceFormId: 0x0000003C),
            },
        };

        var entries = WorldspaceCatalog.Build(
            cells, new List<WorldspaceRecord>(), new HashSet<uint> { 0x0000003C });

        var entry = Assert.Single(entries);
        Assert.Equal(WorldspaceCatalog.WorldspaceCatalogSource.MasterOnly, entry.Source);
        Assert.Single(entry.CellFormIds);
    }

    [Fact]
    public void Dmp_Wrld_With_New_Cell_Marked_DmpNew()
    {
        var dmpCell = new CellRecord { FormId = 0x01000801, WorldspaceFormId = 0x01000800 };
        var cells = new List<CellCatalogEntry>
        {
            new()
            {
                CellFormId = 0x01000801,
                Source = SourceKind.DmpNew,
                DmpModel = dmpCell,
            },
        };

        var wrld = new WorldspaceRecord { FormId = 0x01000800, EditorId = "NewWorldspace" };

        var entries = WorldspaceCatalog.Build(
            cells, new List<WorldspaceRecord> { wrld }, new HashSet<uint>());

        var entry = Assert.Single(entries);
        Assert.Equal(WorldspaceCatalog.WorldspaceCatalogSource.DmpNew, entry.Source);
        Assert.Equal(0x01000801u, Assert.Single(entry.CellFormIds));
    }

    [Fact]
    public void Cells_Without_Worldspace_Skipped()
    {
        var cells = new List<CellCatalogEntry>
        {
            new()
            {
                CellFormId = 0x000ABCDE,
                Source = SourceKind.MasterOnly,
                MasterContext = MakeInteriorContext(0x000ABCDE), // interior, no worldspace
            },
        };

        var entries = WorldspaceCatalog.Build(
            cells, new List<WorldspaceRecord>(), new HashSet<uint>());

        Assert.Empty(entries);
    }

    private static PcEsmCellContext MakeExteriorContext(uint cellFormId, uint worldspaceFormId) => new()
    {
        CellFormId = cellFormId,
        IsInterior = false,
        WorldspaceFormId = worldspaceFormId,
        BlockGroupType = 4,
        SubblockGroupType = 5,
        BlockLabel = [0, 0, 0, 0],
        SubblockLabel = [0, 0, 0, 0],
    };

    private static PcEsmCellContext MakeInteriorContext(uint cellFormId) => new()
    {
        CellFormId = cellFormId,
        IsInterior = true,
        BlockGroupType = 2,
        SubblockGroupType = 3,
        BlockLabel = [1, 0, 0, 0],
        SubblockLabel = [2, 0, 0, 0],
    };
}
