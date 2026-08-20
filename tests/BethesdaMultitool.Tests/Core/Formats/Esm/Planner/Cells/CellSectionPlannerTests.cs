using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     The cell-section planner orchestrates catalog, disposition, child allocation,
///     merge-mode selection, and worldspace planning into the cell-related EmitPlan slices.
/// </summary>
public sealed class CellSectionPlannerTests
{
    [Fact]
    public void Plan_With_One_Master_Cell_Produces_One_KeepMaster_Cell_Plan()
    {
        var masterContext = MakeInteriorContext(0x000ABCDE);

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = masterContext },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE },
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint>());

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
        var masterContext = MakeInteriorContext(0x000ABCDE);
        var dmpCell = new CellRecord { FormId = 0x000ABCDE, EditorId = "TestCell" };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = masterContext },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [dmpCell],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE },
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint>());

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(RecordDisposition.Override, cellPlan.CellRecordPlan.Disposition);
        Assert.Same(dmpCell, cellPlan.CellRecordPlan.Model);
    }

    [Fact]
    public void Plan_With_Dmp_New_Cell_Synthesizes_Context()
    {
        var dmpCell = new CellRecord
        {
            FormId = 0x01000801,
            EditorId = "NewCell",
            WorldspaceFormId = 0x0000003C,
            GridX = 5,
            GridY = -3
        };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext>(),
            new Dictionary<uint, ParsedMainRecord>(),
            [dmpCell],
            [],
            [],
            new HashSet<uint>(),
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint>());

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(RecordDisposition.New, cellPlan.CellRecordPlan.Disposition);
        Assert.False(cellPlan.Context.IsInterior);
        Assert.Equal(0x0000003Cu, cellPlan.Context.WorldspaceFormId);
        Assert.Equal(4, cellPlan.Context.BlockGroupType);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x00, 0x00 }, cellPlan.Context.BlockLabel);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x00, 0x00 }, cellPlan.Context.SubblockLabel);
    }

    [Fact]
    public void Plan_Builds_Worldspace_Plan_From_Cell_Catalog()
    {
        var exteriorContext = MakeExteriorContext(0x000ABCDE, 0x0000003C);
        var master = MakeCellMaster(0x000ABCDE);

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = exteriorContext },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = master },
            [],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE, 0x0000003C },
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint>());

        var wrldPlan = Assert.Single(result.WorldspacesByFormId.Values);
        Assert.Equal(0x0000003Cu, wrldPlan.WorldspaceFormId);
        Assert.Equal(RecordDisposition.KeepMaster, wrldPlan.WorldspaceRecordPlan.Disposition);
        Assert.Equal(0x000ABCDEu, Assert.Single(wrldPlan.CellFormIds));
    }

    [Fact]
    public void Persistent_Placed_Ref_Lands_In_Persistent_Bucket()
    {
        var persistentRef = new PlacedReference
        {
            FormId = 0xAA000001,
            BaseFormId = 0x000ABCDF,
            RecordType = "REFR",
            IsPersistent = true
        };
        var dmpCell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [persistentRef] };
        var masterContext = MakeInteriorContext(0x000ABCDE);

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = masterContext },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [dmpCell],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE },
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint>());

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        var planForRef = Assert.Single(cellPlan.PersistentChildren);
        Assert.Equal(0x01000800u, planForRef.FormId);
        Assert.Equal(RecordDisposition.New, planForRef.Disposition);
        Assert.Empty(cellPlan.TemporaryChildren);
        Assert.Empty(cellPlan.VwdChildren);
    }

    [Fact]
    public void NonPersistent_Placed_Ref_Lands_In_Temporary_Bucket()
    {
        var transientRef = new PlacedReference
        {
            FormId = 0xAA000002,
            BaseFormId = 0x000ABCDF,
            RecordType = "REFR",
            IsPersistent = false
        };
        var dmpCell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [transientRef] };
        var masterContext = MakeInteriorContext(0x000ABCDE);

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = masterContext },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [dmpCell],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE },
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint>());

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Single(cellPlan.TemporaryChildren);
        Assert.Empty(cellPlan.PersistentChildren);
    }

    [Fact]
    public void Master_Placed_Ref_Gets_Override_Disposition()
    {
        var masterRef = new PlacedReference
        {
            FormId = 0x000A0001, // Master-resident.
            BaseFormId = 0x000ABCDF,
            RecordType = "REFR"
        };
        var dmpCell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [masterRef] };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = MakeInteriorContext(0x000ABCDE) },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [dmpCell],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE, 0x000A0001 },
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint> { 0x000A0001 });

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        var planForRef = Assert.Single(cellPlan.TemporaryChildren);
        Assert.Equal(RecordDisposition.Override, planForRef.Disposition);
        Assert.Equal(0x000A0001u, planForRef.FormId);
    }

    [Fact]
    public void New_Navm_Lands_In_Temporary_Bucket()
    {
        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = 0x000ABCDE,
            RawSubrecords = [new NavMeshSubrecord("DATA", [1, 2, 3, 4])]
        };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = MakeInteriorContext(0x000ABCDE) },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [new CellRecord { FormId = 0x000ABCDE }],
            [navm],
            [],
            new HashSet<uint> { 0x000ABCDE },
            new FormIdAllocator(),
            true,
            new HashSet<uint>());

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        var planForNavm = Assert.Single(cellPlan.TemporaryChildren);
        Assert.Equal("NAVM", planForNavm.Type);
        Assert.Equal(RecordDisposition.New, planForNavm.Disposition);
        Assert.Equal(0x01000800u, planForNavm.FormId);
    }

    [Fact]
    public void Master_Cell_Navm_Is_Fully_Suppressed_When_Augmentation_Disabled()
    {
        // The gate must suppress the NAVM RECORD, not just its NAVI row. A NAVM emitted
        // into a master cell without a NAVI entry (and without the TES4 ESM flag, which
        // Tes4HeaderBuilder ties to this option) null-derefs NavMeshInfoMap on cell entry.
        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = 0x000ABCDE,
            RawSubrecords = [new NavMeshSubrecord("DATA", [1, 2, 3, 4])]
        };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = MakeInteriorContext(0x000ABCDE) },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [new CellRecord { FormId = 0x000ABCDE }],
            [navm],
            [],
            new HashSet<uint> { 0x000ABCDE },
            new FormIdAllocator(),
            false,
            new HashSet<uint>());

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Empty(cellPlan.TemporaryChildren);
        Assert.Empty(result.NavmEntries);
        Assert.Empty(result.NavmSourceToEmitted);
    }

    [Fact]
    public void New_Cell_Navm_Survives_When_Augmentation_Disabled()
    {
        // The gate is scoped to MASTER cells; brand-new proto cells keep their navmeshes
        // (and their NAVI rows) regardless.
        var dmpCell = new CellRecord { FormId = 0x01000801, EditorId = "NewCell", Flags = 0x01 };
        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = 0x01000801,
            RawSubrecords = [new NavMeshSubrecord("DATA", [1, 2, 3, 4])]
        };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext>(),
            new Dictionary<uint, ParsedMainRecord>(),
            [dmpCell],
            [navm],
            [],
            new HashSet<uint>(),
            new FormIdAllocator(),
            false,
            new HashSet<uint>());

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        var planForNavm = Assert.Single(cellPlan.TemporaryChildren);
        Assert.Equal("NAVM", planForNavm.Type);
        Assert.Single(result.NavmEntries);
    }

    [Fact]
    public void Plan_Without_MasterRefFormIds_Fails_Before_Producing_An_Incomplete_Cell()
    {
        var dmpCell = new CellRecord { FormId = 0x000ABCDE, EditorId = "TestCell" };

        var exception = Assert.Throws<ArgumentNullException>(() => CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = MakeInteriorContext(0x000ABCDE) },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [dmpCell],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE },
            new FormIdAllocator()));

        Assert.Equal("masterRefFormIds", exception.ParamName);
    }

    [Fact]
    public void Plan_Settles_PersistentOnly_Mode_For_Persistent_Master_Capture()
    {
        // One master-resident PERSISTENT ref, no temporaries ⇒ PersistentOnly; a sparse
        // capture never turns on the replaced-interior marker drop.
        var masterRef = new PlacedReference
        {
            FormId = 0x000A0001, BaseFormId = 0x000ABCDF, RecordType = "REFR", IsPersistent = true
        };
        var dmpCell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [masterRef] };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = MakeInteriorContext(0x000ABCDE) },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [dmpCell],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE, 0x000A0001 },
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint> { 0x000A0001 });

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(CellMergeMode.PersistentOnly, cellPlan.Mode);
        Assert.False(cellPlan.DropRenderCullingMarkers);
    }

    [Fact]
    public void Plan_Settles_LoadedReplacement_And_Interior_Marker_Drop()
    {
        // A non-persistent master-resident capture makes the snapshot authoritative
        // (LoadedReplacement); replaced master INTERIORS drop render-culling markers.
        var masterRef = new PlacedReference
        {
            FormId = 0x000A0001, BaseFormId = 0x000ABCDF, RecordType = "REFR", IsPersistent = false
        };
        var dmpCell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [masterRef] };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = MakeInteriorContext(0x000ABCDE) },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [dmpCell],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE, 0x000A0001 },
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint> { 0x000A0001 });

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(CellMergeMode.LoadedReplacement, cellPlan.Mode);
        Assert.True(cellPlan.DropRenderCullingMarkers);
    }

    [Fact]
    public void Plan_Marker_Drop_Respects_ReplaceCellTemporaries_Diagnostic()
    {
        var masterRef = new PlacedReference
        {
            FormId = 0x000A0001, BaseFormId = 0x000ABCDF, RecordType = "REFR", IsPersistent = false
        };
        var dmpCell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [masterRef] };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext> { [0x000ABCDE] = MakeInteriorContext(0x000ABCDE) },
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = MakeCellMaster(0x000ABCDE) },
            [dmpCell],
            [],
            [],
            new HashSet<uint> { 0x000ABCDE, 0x000A0001 },
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint> { 0x000A0001 },
            replaceCellTemporariesOnOverride: true);

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(CellMergeMode.LoadedReplacement, cellPlan.Mode);
        Assert.False(cellPlan.DropRenderCullingMarkers);
    }

    [Fact]
    public void Plan_Settles_LoadedReplacement_For_New_Cell_Without_Marker_Drop()
    {
        // Brand-new (non-master-anchored) cells emit all children unconditionally and
        // never drop markers (the drop is a replaced-master-interior policy).
        var dmpCell = new CellRecord
        {
            FormId = 0x01000801, EditorId = "NewCell", WorldspaceFormId = 0x0000003C, GridX = 1, GridY = 1
        };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext>(),
            new Dictionary<uint, ParsedMainRecord>(),
            [dmpCell],
            [],
            [],
            new HashSet<uint>(),
            new FormIdAllocator(),
            masterRefFormIds: new HashSet<uint>());

        var cellPlan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(CellMergeMode.LoadedReplacement, cellPlan.Mode);
        Assert.False(cellPlan.DropRenderCullingMarkers);
    }

    private static PcEsmCellContext MakeInteriorContext(uint cellFormId)
    {
        return new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = true,
            BlockGroupType = 2,
            SubblockGroupType = 3,
            BlockLabel = [1, 0, 0, 0],
            SubblockLabel = [2, 0, 0, 0]
        };
    }

    private static PcEsmCellContext MakeExteriorContext(uint cellFormId, uint worldspaceFormId)
    {
        return new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = false,
            WorldspaceFormId = worldspaceFormId,
            BlockGroupType = 4,
            SubblockGroupType = 5,
            BlockLabel = [0, 0, 0, 0],
            SubblockLabel = [0, 0, 0, 0]
        };
    }

    private static ParsedMainRecord MakeCellMaster(uint cellFormId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "CELL",
                DataSize = 0,
                Flags = 0,
                FormId = cellFormId,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 15
            },
            Offset = 0
        };
    }
}