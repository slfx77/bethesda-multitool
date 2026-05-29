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
        var builder = new PlanCellSectionBuilder();

        var bytes = builder.BuildCellSection(
            plan, new Dictionary<uint, ParsedMainRecord>(), new PluginBuildOptions());

        Assert.Null(bytes);
    }

    [Fact]
    public void Single_Interior_KeepMaster_Cell_Matches_Legacy()
    {
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
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
            },
            Context = context,
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
        };

        var plan = MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(0x000ABCDE, cellPlan),
        };

        var builder = new PlanCellSectionBuilder();
        var plannerBytes = builder.BuildCellSection(
            plan, new Dictionary<uint, ParsedMainRecord>(), new PluginBuildOptions());

        var legacyBundle = new CellOverrideBundle
        {
            CellFormId = 0x000ABCDE,
            Context = context,
            CellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(master),
            PersistentChildRecords = [],
            VwdChildRecords = [],
            TemporaryChildRecords = [],
        };
        var legacyBytes = CellGrupBuilder.BuildCellSection(
            [legacyBundle], new Dictionary<uint, ParsedMainRecord>(), null);

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
