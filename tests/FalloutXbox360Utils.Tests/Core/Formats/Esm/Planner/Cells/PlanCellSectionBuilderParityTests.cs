using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;
using CellRecord = FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World.CellRecord;

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
        };
        var childPlan = new RecordPlan
        {
            Type = "REFR",
            Disposition = RecordDisposition.New,
            FormId = 0x01000801,
            Model = placed,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
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
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
            },
            Context = context,
            PersistentChildren = ImmutableArray.Create(childPlan),
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

        // Build the equivalent legacy child bytes via the same primitive path the planner uses.
        var subs = RefrEncoder.EncodeNewPlacedReference(placed, validFormIds: null, remapTable: null);
        Assert.NotEmpty(subs.Subrecords);
        var legacyChildBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "REFR", 0x01000801u, 0u, subs.Subrecords);

        var legacyBundle = new CellOverrideBundle
        {
            CellFormId = 0x000ABCDE,
            Context = context,
            CellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(master),
            PersistentChildRecords = [legacyChildBytes],
            VwdChildRecords = [],
            TemporaryChildRecords = [],
        };
        var legacyBytes = CellGrupBuilder.BuildCellSection(
            [legacyBundle], new Dictionary<uint, ParsedMainRecord>(), null);

        Assert.Equal(legacyBytes, plannerBytes);
    }

    [Fact]
    public void New_Interior_Cell_Emits_Through_Planner_With_Byte_Parity()
    {
        var cellModel = new CellRecord
        {
            FormId = 0x01000801,
            EditorId = "TestNewCell",
            Flags = 0x01, // Interior.
        };
        var context = new PcEsmCellContext
        {
            CellFormId = 0x01000801,
            IsInterior = true,
            BlockGroupType = 2,
            SubblockGroupType = 3,
            BlockLabel = [1, 0, 0, 0],
            SubblockLabel = [2, 0, 0, 0],
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
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
            },
            Context = context,
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
        };

        var plan = MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(0x01000801, cellPlan),
        };

        var builder = new PlanCellSectionBuilder();
        var plannerBytes = builder.BuildCellSection(
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
