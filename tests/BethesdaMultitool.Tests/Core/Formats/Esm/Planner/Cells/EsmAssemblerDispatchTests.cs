using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Verifies the EsmAssembler dispatch shim. We don't construct a full EsmAssembler
///     instance (it requires a registry + many other dependencies); instead we test the
///     core invariant via the same builders the assembler delegates to: when "CELL" is
///     opted into the planner, PlanCellSectionBuilder.BuildCellSection must produce
///     byte-identical output to legacy CellGrupBuilder.BuildCellSection when fed
///     equivalent inputs.
/// </summary>
public sealed class EsmAssemblerDispatchTests
{


    [Fact]
    public void Planner_And_Legacy_Produce_Equal_Bytes_For_KeepMaster_Cell_With_Child()
    {
        // Build the same one-cell scenario through both writers and assert byte equality.
        // This is the load-bearing invariant: the dispatch shim is safe to enable.
        // The cell carries one new placed ref — a master-anchored cell with NO children is
        // an ITM override that the planner intentionally suppresses (see
        // PlanCellSectionBuilderParityTests.Single_Interior_KeepMaster_Cell_With_No_Children_Is_Suppressed).
        var cellFormId = 0x000ABCDEu;
        var (master, context) = MakeInteriorCellMaster(cellFormId);

        var placed = new PlacedReference
        {
            FormId = 0x01000801,
            BaseFormId = 0x0000001F, // Engine-reserved (RoomMarker) — survives base validation.
            RecordType = "REFR",
            IsPersistent = true
        };
        var childSubs = RefrEncoder.EncodeNewPlacedReference(placed);
        var childBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "REFR", placed.FormId, 0x400u, childSubs.Subrecords); // persistent-bucket flag

        var legacyBundle = new CellOverrideBundle
        {
            CellFormId = cellFormId,
            Context = context,
            CellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(master),
            PersistentChildRecords = [childBytes],
            VwdChildRecords = [],
            TemporaryChildRecords = []
        };
        var legacyBytes = CellGrupBuilder.BuildCellSection(
            [legacyBundle], new Dictionary<uint, ParsedMainRecord>());

        var childPlan = new RecordPlan
        {
            Type = "REFR",
            Disposition = RecordDisposition.New,
            FormId = placed.FormId,
            Model = placed,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
        var cellPlan = new CellPlan
        {
            CellFormId = cellFormId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.KeepMaster,
                FormId = cellFormId,
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
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(cellFormId, cellPlan),
            EmittedFormIds = ImmutableHashSet.Create(placed.FormId)
        };

        var plannerBytes = PlanCellSectionBuilder.BuildCellSection(
            CellPlanTestHarness.Settle(plan, new Dictionary<uint, ParsedMainRecord>()), new Dictionary<uint, ParsedMainRecord>(), new PluginBuildOptions());

        Assert.Equal(legacyBytes, plannerBytes);
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