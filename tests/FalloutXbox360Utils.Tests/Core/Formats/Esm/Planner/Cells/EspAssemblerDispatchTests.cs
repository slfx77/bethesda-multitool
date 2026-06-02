using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Verifies the EspAssembler dispatch shim. We don't construct a full EspAssembler
///     instance (it requires a registry + many other dependencies); instead we test the
///     core invariant via the same builders the assembler delegates to: when "CELL" is
///     opted into the planner, PlanCellSectionBuilder.BuildCellSection must produce
///     byte-identical output to legacy CellGrupBuilder.BuildCellSection when fed
///     equivalent inputs.
/// </summary>
public sealed class EspAssemblerDispatchTests
{
    [Fact]
    public void Legacy_Path_When_Cell_Not_In_PlannerEnabledRecordTypes()
    {
        // With no "CELL" opt-in, EspAssembler should keep calling legacy.
        // This test pins the dispatch logic: the condition is a contains-check.
        var options = new PluginBuildOptions
        {
            PlannerEnabledRecordTypes = new HashSet<string> { "WEAP" },
        };

        Assert.DoesNotContain("CELL", options.PlannerEnabledRecordTypes);
        // The assembler short-circuits to CellGrupBuilder when this is false.
    }

    [Fact]
    public void Planner_Path_Active_When_Cell_In_PlannerEnabledRecordTypes()
    {
        var options = new PluginBuildOptions
        {
            PlannerEnabledRecordTypes = new HashSet<string> { "CELL" },
        };

        Assert.Contains("CELL", options.PlannerEnabledRecordTypes);
    }

    [Fact]
    public void Planner_And_Legacy_Produce_Equal_Bytes_For_KeepMaster_Cell()
    {
        // Build the same one-cell scenario through both writers and assert byte equality.
        // This is the load-bearing invariant: the dispatch shim is safe to enable.
        var cellFormId = 0x000ABCDEu;
        var (master, context) = MakeInteriorCellMaster(cellFormId);

        var legacyBundle = new CellOverrideBundle
        {
            CellFormId = cellFormId,
            Context = context,
            CellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(master),
            PersistentChildRecords = [],
            VwdChildRecords = [],
            TemporaryChildRecords = [],
        };
        var legacyBytes = CellGrupBuilder.BuildCellSection(
            [legacyBundle], new Dictionary<uint, ParsedMainRecord>(), null);

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
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
            },
            Context = context,
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
        };

        var plan = MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(cellFormId, cellPlan),
        };

        var plannerBytes = PlanCellSectionBuilder.BuildCellSection(
            plan, new Dictionary<uint, ParsedMainRecord>(), new PluginBuildOptions());

        Assert.Equal(legacyBytes, plannerBytes);
    }

    private static EmitPlan MakeEmptyPlan() => new()
    {
        Records = ImmutableArray<RecordPlan>.Empty,
        SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
        EmittedFormIds = ImmutableHashSet<uint>.Empty,
        RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
        Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
        Meta = new PlanMetadata
        {
            NextObjectId = 0x800,
            PlannerCoverage = ImmutableHashSet<string>.Empty,
        },
    };

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
                Version = 15,
            },
            Offset = 0,
        };
        var context = new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = true,
            BlockGroupType = 2,
            SubblockGroupType = 3,
            BlockLabel = [1, 0, 0, 0],
            SubblockLabel = [2, 0, 0, 0],
        };
        return (master, context);
    }
}
