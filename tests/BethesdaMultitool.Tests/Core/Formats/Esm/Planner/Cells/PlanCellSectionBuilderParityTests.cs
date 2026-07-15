using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;
using CellRecord = BethesdaMultitool.Core.Formats.Esm.Models.Records.World.CellRecord;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Parity tests for <see cref="PlanCellSectionBuilder" />: when fed a plan with N
///     KeepMaster cells, the output must match what legacy <see cref="CellGrupBuilder" />
///     produces from the equivalent bundles.
/// </summary>
public sealed class PlanCellSectionBuilderParityTests
{
    [Fact]
    public void Empty_Plan_Returns_Null()
    {
        var plan = MakeEmptyPlan();
        var bytes = PlanCellSectionBuilder.BuildCellSection(
            plan, new Dictionary<uint, ParsedMainRecord>(), new PluginBuildOptions());

        Assert.Null(bytes);
    }

    [Fact]
    public void Single_Interior_KeepMaster_Cell_With_No_Children_Is_Suppressed()
    {
        // A master-anchored cell with no surviving children is a byte-identical (ITM)
        // override: zero information, but it would make this plugin the cell's winning
        // file and risk the master's temporary children in-game (Doc Mitchell's-house
        // blanking class). It must not emit at all.
        var (master, context) = MakeInteriorCellMaster(0x000ABCDE);
        var cellPlan = new CellPlan
        {
            CellFormId = 0x000ABCDE,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.KeepMaster,
                FormId = 0x000ABCDE,
                Master = master,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = context,
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty
        };

        var plan = MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(0x000ABCDE, cellPlan)
        };

        var stats = new ConversionPipelineStats();
        var plannerBytes = PlanCellSectionBuilder.BuildCellSection(
            plan, new Dictionary<uint, ParsedMainRecord>(), new PluginBuildOptions(), stats);

        Assert.Null(plannerBytes);
        Assert.Equal(1, stats.DropReasonCounts["cell.itm-override-suppressed"]);
    }

    [Fact]
    public void Cell_With_New_Placed_Ref_Emits_Through_Planner_With_Byte_Parity()
    {
        var (master, context) = MakeInteriorCellMaster(0x000ABCDE);
        var placed = new PlacedReference
        {
            FormId = 0x01000801,
            BaseFormId = 0x000ABCDF,
            RecordType = "REFR",
            IsPersistent = true,
            IsInitiallyDisabled = true
        };
        var childPlan = new RecordPlan
        {
            Type = "REFR",
            Disposition = RecordDisposition.New,
            FormId = 0x01000801,
            Model = placed,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
        var cellPlan = new CellPlan
        {
            CellFormId = 0x000ABCDE,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.KeepMaster,
                FormId = 0x000ABCDE,
                Master = master,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = context,
            PersistentChildren = ImmutableArray.Create(childPlan),
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty
        };

        var plan = MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(0x000ABCDE, cellPlan),
            // The ref's base must be resolvable or the dangling-base guard drops the ref.
            EmittedFormIds = ImmutableHashSet.Create(0x000ABCDFu, 0x01000801u)
        };

        var plannerBytes = PlanCellSectionBuilder.BuildCellSection(
            plan, new Dictionary<uint, ParsedMainRecord>(), new PluginBuildOptions());

        // Build the equivalent legacy child bytes via the same primitive path the planner uses.
        var subs = RefrEncoder.EncodeNewPlacedReference(placed);
        Assert.NotEmpty(subs.Subrecords);
        var legacyChildBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "REFR", 0x01000801u, 0xC00u, subs.Subrecords); // persistent + initially disabled

        var legacyBundle = new CellOverrideBundle
        {
            CellFormId = 0x000ABCDE,
            Context = context,
            CellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(master),
            PersistentChildRecords = [legacyChildBytes],
            VwdChildRecords = [],
            TemporaryChildRecords = []
        };
        var legacyBytes = CellGrupBuilder.BuildCellSection(
            [legacyBundle], new Dictionary<uint, ParsedMainRecord>());

        Assert.Equal(legacyBytes, plannerBytes);
    }

    [Fact]
    public void New_Interior_Cell_Emits_Through_Planner_With_Byte_Parity()
    {
        var cellModel = new CellRecord
        {
            FormId = 0x01000801,
            EditorId = "TestNewCell",
            Flags = 0x01 // Interior.
        };
        var context = new PcEsmCellContext
        {
            CellFormId = 0x01000801,
            IsInterior = true,
            BlockGroupType = 2,
            SubblockGroupType = 3,
            BlockLabel = [1, 0, 0, 0],
            SubblockLabel = [2, 0, 0, 0]
        };
        var cellPlan = new CellPlan
        {
            CellFormId = 0x01000801,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.New,
                FormId = 0x01000801,
                Model = cellModel,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = context,
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty
        };

        var plan = MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(0x01000801, cellPlan)
        };

        var plannerBytes = PlanCellSectionBuilder.BuildCellSection(
            plan, new Dictionary<uint, ParsedMainRecord>(), new PluginBuildOptions { CompressRecords = false });

        // Build the equivalent legacy bytes by encoding the CELL through the same primitives.
        var encoded = new CellEncoder().Encode(cellModel);
        var legacyCellBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "CELL", 0x01000801u, 0u, encoded.Subrecords);
        var legacyBundle = new CellOverrideBundle
        {
            CellFormId = 0x01000801,
            Context = context,
            CellRecordBytes = legacyCellBytes,
            PersistentChildRecords = [],
            VwdChildRecords = [],
            TemporaryChildRecords = []
        };
        var legacyBytes = CellGrupBuilder.BuildCellSection(
            [legacyBundle], new Dictionary<uint, ParsedMainRecord>());

        Assert.Equal(legacyBytes, plannerBytes);
    }

    [Fact]
    public void New_Placed_Ref_With_Runtime_Dynamic_Base_Is_Dropped()
    {
        // 0xFFxxxxxx bases are engine-created runtime clones (leveled spawns); they can
        // never resolve from a plugin file. Emitting the ref dangling tears the cell down
        // at load, so the guard drops it and the cell emits with no children.
        var stats = new ConversionPipelineStats();
        var bytes = BuildSectionWithSinglePlacedRef(
            baseFormId: 0xFF000B78, recordType: "ACHR", stats: stats);

        // Dropping the only child leaves an ITM cell override, which is suppressed too.
        Assert.Null(bytes);
        Assert.Equal(1, stats.DropReasonCounts["refr.dangling-base"]);
        Assert.Equal(1, stats.SkippedByType["ACHR"]);
        Assert.Equal(1, stats.DropReasonCounts["cell.itm-override-suppressed"]);
    }

    [Fact]
    public void New_Placed_Ref_With_Phantom_Master_Base_Is_Dropped()
    {
        // Proto FormIDs in the master range that the final master cut resolve to nothing
        // at load — the phantom-master crash class. Same drop as runtime-dynamic bases.
        var stats = new ConversionPipelineStats();
        var bytes = BuildSectionWithSinglePlacedRef(
            baseFormId: 0x0013D2F0, recordType: "REFR", stats: stats);

        Assert.Null(bytes);
        Assert.Equal(1, stats.DropReasonCounts["refr.dangling-base"]);
    }

    [Fact]
    public void New_Placed_Ref_With_Engine_Reserved_Base_Is_Kept()
    {
        // Sub-0x800 bases (DoorMarker, RoomMarker, XMarkerHeading, …) are EXE-baked, not
        // authored in any ESM; dropping refs to them corrupts the cell's reference graph.
        var placed = new PlacedReference
        {
            FormId = 0x01000801,
            BaseFormId = 0x0000001F,
            RecordType = "REFR",
            IsPersistent = true
        };

        var bytes = BuildSectionWithSinglePlacedRef(placed);

        var subs = RefrEncoder.EncodeNewPlacedReference(placed);
        var expectedChild = PluginRecordByteBuilder.BuildNewRecordBytes(
            "REFR", 0x01000801u, 0x400u, subs.Subrecords); // persistent-bucket ref carries flag 0x400
        Assert.Equal(BuildExpectedCellOnlySection(persistentChildren: [expectedChild]), bytes);
    }

    [Theory]
    [InlineData(true, false, 0x400u)]
    [InlineData(false, true, 0xC00u)]
    public void New_Placed_Ref_Emits_Planner_Selected_Disabled_Header_Flag(
        bool capturedDisabled,
        bool plannedDisabled,
        uint expectedFlags)
    {
        var placed = new PlacedReference
        {
            FormId = 0x01000801,
            BaseFormId = 0x0000001F,
            RecordType = "REFR",
            IsPersistent = true,
            IsInitiallyDisabled = capturedDisabled,
        };
        var verdict = new PlacedRefDecision
        {
            Verdict = PlacedRefEmitVerdict.Emit,
            FinalBaseFormId = placed.BaseFormId,
            TargetGroupType = 8,
            NewInitiallyDisabled = plannedDisabled,
        };

        var bytes = BuildSectionWithSinglePlacedRef(placed, verdict: verdict);

        var subs = RefrEncoder.EncodeNewPlacedReference(placed);
        var expectedChild = PluginRecordByteBuilder.BuildNewRecordBytes(
            "REFR", placed.FormId, expectedFlags, subs.Subrecords);
        Assert.Equal(BuildExpectedCellOnlySection(persistentChildren: [expectedChild]), bytes);
    }

    [Fact]
    public void New_Placed_Ref_Base_Is_Remapped_Through_Source_To_Emitted_Map()
    {
        // A base pointing at another planner-allocated record must emit the EMITTED
        // FormID, not the proto source FormID (which would dangle).
        var placed = new PlacedReference
        {
            FormId = 0x01000801,
            BaseFormId = 0xAA000010,
            RecordType = "REFR",
            IsPersistent = true
        };

        var bytes = BuildSectionWithSinglePlacedRef(
            placed,
            sourceToEmitted: ImmutableDictionary<uint, uint>.Empty.Add(0xAA000010, 0x01000900),
            emittedFormIds: ImmutableHashSet.Create(0x01000801u, 0x01000900u));

        var subs = RefrEncoder.EncodeNewPlacedReference(placed with { BaseFormId = 0x01000900 });
        var expectedChild = PluginRecordByteBuilder.BuildNewRecordBytes(
            "REFR", 0x01000801u, 0x400u, subs.Subrecords); // persistent-bucket ref carries flag 0x400
        Assert.Equal(BuildExpectedCellOnlySection(persistentChildren: [expectedChild]), bytes);
    }

    [Fact]
    public void New_Placed_Ref_With_Dangling_Enable_Parent_Surfaces_Planner_Diagnostic()
    {
        const uint missingParent = 0xAA00DEAD;
        var placed = new PlacedReference
        {
            FormId = 0x01000801,
            BaseFormId = 0x0000001F,
            RecordType = "REFR",
            IsPersistent = true,
            EnableParentFormId = missingParent
        };
        var stats = new ConversionPipelineStats();

        var bytes = BuildSectionWithSinglePlacedRef(placed, stats: stats);

        Assert.NotNull(bytes);
        Assert.Equal(1, stats.PlannerXespObserved);
        Assert.Equal(0, stats.PlannerXespEmitted);
        Assert.Equal(1, stats.PlannerXespSkipped);
        Assert.Equal([missingParent], stats.PlannerXespMissingParentFormIds);
        Assert.Equal(1, stats.DropReasonCounts["refr.xesp-dangling"]);
        var observation = Assert.Single(stats.PlannerXespObservations);
        Assert.Equal(PlannerXespParentStatus.Absent, observation.ParentStatus);
        Assert.Equal("absent-from-master-and-capture", observation.Reason);
    }

    [Fact]
    public void New_Achr_With_Wrong_Master_Base_Type_Is_Dropped()
    {
        // ACHR must point at NPC_; a master base of any other type is the
        // base-type-mismatch class (the v20.5 ANAM/Robot lesson, placed-ref edition).
        var stats = new ConversionPipelineStats();
        var bytes = BuildSectionWithSinglePlacedRef(
            baseFormId: 0x000ABCDE, recordType: "ACHR", stats: stats); // Base = the CELL master.

        Assert.Null(bytes);
        Assert.Equal(1, stats.DropReasonCounts["refr.base-type-mismatch"]);
    }

    private static byte[]? BuildSectionWithSinglePlacedRef(
        uint baseFormId, string recordType, ConversionPipelineStats? stats = null)
    {
        var placed = new PlacedReference
        {
            FormId = 0x01000801,
            BaseFormId = baseFormId,
            RecordType = recordType,
            IsPersistent = true
        };
        return BuildSectionWithSinglePlacedRef(placed, stats: stats);
    }

    private static byte[]? BuildSectionWithSinglePlacedRef(
        PlacedReference placed,
        ImmutableDictionary<uint, uint>? sourceToEmitted = null,
        ImmutableHashSet<uint>? emittedFormIds = null,
        ConversionPipelineStats? stats = null,
        PlacedRefDecision? verdict = null)
    {
        var (master, context) = MakeInteriorCellMaster(0x000ABCDE);
        var childPlan = new RecordPlan
        {
            Type = placed.RecordType,
            Disposition = RecordDisposition.New,
            FormId = placed.FormId,
            Model = placed,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
        var cellPlan = new CellPlan
        {
            CellFormId = 0x000ABCDE,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.KeepMaster,
                FormId = 0x000ABCDE,
                Master = master,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = context,
            PersistentChildren = ImmutableArray.Create(childPlan),
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
            RefDecisions = verdict is null
                ? ImmutableDictionary<uint, PlacedRefDecision>.Empty
                : ImmutableDictionary<uint, PlacedRefDecision>.Empty.Add(placed.FormId, verdict),
        };

        var plan = MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(0x000ABCDE, cellPlan),
            SourceToEmittedFormId = sourceToEmitted ?? ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = emittedFormIds ?? ImmutableHashSet.Create(placed.FormId)
        };

        return PlanCellSectionBuilder.BuildCellSection(
            plan,
            new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = master },
            new PluginBuildOptions(),
            stats);
    }

    private static byte[]? BuildExpectedCellOnlySection(List<byte[]>? persistentChildren = null)
    {
        var (master, context) = MakeInteriorCellMaster(0x000ABCDE);
        var legacyBundle = new CellOverrideBundle
        {
            CellFormId = 0x000ABCDE,
            Context = context,
            CellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(master),
            PersistentChildRecords = persistentChildren ?? [],
            VwdChildRecords = [],
            TemporaryChildRecords = []
        };
        return CellGrupBuilder.BuildCellSection(
            [legacyBundle], new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = master });
    }

    private static EmitPlan MakeEmptyPlan()
    {
        return new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet<uint>.Empty,
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet<string>.Empty
            }
        };
    }

    private static (ParsedMainRecord Master, PcEsmCellContext Context) MakeInteriorCellMaster(uint cellFormId)
    {
        var master = new ParsedMainRecord
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

        var context = new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = true,
            BlockGroupType = 2,
            SubblockGroupType = 3,
            BlockLabel = [1, 0, 0, 0],
            SubblockLabel = [2, 0, 0, 0]
        };

        return (master, context);
    }
}
