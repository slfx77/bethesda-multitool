using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Tier 6.1: the cell-section planner orchestrates catalog → disposition → child
///     allocation → worldspace catalog, producing the cell-related EmitPlan slices.
///     Children arrays stay empty until Tier 6.1b populates them; this test pins the
///     orchestration shape.
/// </summary>
public sealed class CellSectionPlannerTests
{
    [Fact]
    public void Plan_With_One_Master_Cell_Produces_One_KeepMaster_Cell_Plan()
    {
        var planner = new CellSectionPlanner();
        var masterContext = MakeInteriorContext(0x000ABCDE);

        var result = planner.Plan(
            masterContexts: new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = masterContext },
            masterRecordsByFormId: new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            dmpCells: [],
            dmpNavmeshes: [],
            dmpWorldspaces: [],
            masterFormIds: new HashSet<uint> { 0x000ABCDE },
            allocator: new FormIdAllocator(0x800));

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(0x000ABCDEu, cellPlan.CellFormId);
        Assert.Equal(RecordDisposition.KeepMaster, cellPlan.CellRecordPlan.Disposition);
        Assert.Empty(cellPlan.PersistentChildren);
        Assert.Empty(cellPlan.VwdChildren);
        Assert.Empty(cellPlan.TemporaryChildren);
    }

    [Fact]
    public void Plan_With_Dmp_Override_Marks_Cell_Override()
    {
        var planner = new CellSectionPlanner();
        var masterContext = MakeInteriorContext(0x000ABCDE);
        var dmpCell = new CellRecord { FormId = 0x000ABCDE, EditorId = "TestCell" };

        var result = planner.Plan(
            masterContexts: new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = masterContext },
            masterRecordsByFormId: new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            dmpCells: [dmpCell],
            dmpNavmeshes: [],
            dmpWorldspaces: [],
            masterFormIds: new HashSet<uint> { 0x000ABCDE },
            allocator: new FormIdAllocator(0x800));

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(RecordDisposition.Override, cellPlan.CellRecordPlan.Disposition);
        Assert.Same(dmpCell, cellPlan.CellRecordPlan.Model);
    }

    [Fact]
    public void Plan_With_Dmp_New_Cell_Synthesizes_Context()
    {
        var planner = new CellSectionPlanner();
        var dmpCell = new CellRecord
        {
            FormId = 0x01000801,
            EditorId = "NewCell",
            WorldspaceFormId = 0x0000003C,
            GridX = 5,
            GridY = -3,
        };

        var result = planner.Plan(
            masterContexts: new Dictionary<uint, PcEsmCellContext>(),
            masterRecordsByFormId: new Dictionary<uint, ParsedMainRecord>(),
            dmpCells: [dmpCell],
            dmpNavmeshes: [],
            dmpWorldspaces: [],
            masterFormIds: new HashSet<uint>(),
            allocator: new FormIdAllocator(0x800));

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(RecordDisposition.New, cellPlan.CellRecordPlan.Disposition);
        Assert.False(cellPlan.Context.IsInterior);
        Assert.Equal(0x0000003Cu, cellPlan.Context.WorldspaceFormId);
        Assert.Equal(4, cellPlan.Context.BlockGroupType);
    }

    [Fact]
    public void Plan_Builds_Worldspace_Plan_From_Cell_Catalog()
    {
        var planner = new CellSectionPlanner();
        var exteriorContext = MakeExteriorContext(0x000ABCDE, worldspaceFormId: 0x0000003C);
        var master = MakeCellMaster(0x000ABCDE);

        var result = planner.Plan(
            masterContexts: new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = exteriorContext },
            masterRecordsByFormId: new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = master },
            dmpCells: [],
            dmpNavmeshes: [],
            dmpWorldspaces: [],
            masterFormIds: new HashSet<uint> { 0x000ABCDE, 0x0000003C },
            allocator: new FormIdAllocator(0x800));

        var wrldPlan = Assert.Single(result.WorldspacesByFormId.Values);
        Assert.Equal(0x0000003Cu, wrldPlan.WorldspaceFormId);
        Assert.Equal(RecordDisposition.KeepMaster, wrldPlan.WorldspaceRecordPlan.Disposition);
        Assert.Equal(0x000ABCDEu, Assert.Single(wrldPlan.CellFormIds));
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

    private static ParsedMainRecord MakeCellMaster(uint cellFormId) => new()
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
