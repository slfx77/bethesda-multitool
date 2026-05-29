using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;
using CellRecord = FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World.CellRecord;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Pins Tier 6.5b: when the plan contains a <see cref="RecordDisposition.New" />
///     worldspace with captured cells, <see cref="PlanCellSectionBuilder" /> encodes the
///     WRLD through <see cref="WrldEncoder.EncodeNew" /> and emits it via the legacy
///     framing's new-worldspace channel. Byte parity vs. constructing the equivalent
///     legacy <see cref="NewWorldspaceEntry" /> by hand.
/// </summary>
public sealed class PlanCellSectionBuilderNewWorldspaceTests
{
    [Fact]
    public void New_Worldspace_With_New_Cell_Emits_Through_Planner_With_Byte_Parity()
    {
        const uint sourceWrldId = 0x01000900u;
        const uint allocatedWrldId = 0x01000901u;
        const uint newCellId = 0x01000801u;

        var wrldModel = new WorldspaceRecord
        {
            FormId = sourceWrldId,
            EditorId = "PlanNewWrld",
        };
        var cellModel = new CellRecord
        {
            FormId = newCellId,
            EditorId = "PlanNewCell",
            WorldspaceFormId = sourceWrldId,
            Flags = 0, // Exterior.
            GridX = 0,
            GridY = 0,
        };
        var cellContext = new PcEsmCellContext
        {
            CellFormId = newCellId,
            IsInterior = false,
            WorldspaceFormId = sourceWrldId,
            BlockGroupType = 4,
            SubblockGroupType = 5,
            BlockLabel = [0, 0, 0, 0],
            SubblockLabel = [0, 0, 0, 0],
        };
        var cellPlan = new CellPlan
        {
            CellFormId = newCellId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.New,
                FormId = newCellId,
                Model = cellModel,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
            },
            Context = cellContext,
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
            ParentWorldspaceFormId = sourceWrldId,
        };
        var wrldPlan = new WorldspacePlan
        {
            WorldspaceFormId = allocatedWrldId,
            WorldspaceRecordPlan = new RecordPlan
            {
                Type = "WRLD",
                Disposition = RecordDisposition.New,
                FormId = allocatedWrldId,
                SourceFormId = sourceWrldId,
                Model = wrldModel,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
            },
            CellFormIds = ImmutableArray.Create(newCellId),
        };

        var plan = MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(newCellId, cellPlan),
            WorldspacesByFormId = ImmutableDictionary<uint, WorldspacePlan>.Empty
                .Add(sourceWrldId, wrldPlan),
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(sourceWrldId, allocatedWrldId),
        };

        var options = new PluginBuildOptions { CompressRecords = false };
        var builder = new PlanCellSectionBuilder();
        var plannerBytes = builder.BuildCellSection(plan, new Dictionary<uint, ParsedMainRecord>(), options);

        // Reconstruct the legacy path: encode the same CELL and WRLD via the primitive
        // encoders and feed them into CellGrupBuilder.BuildCellSection.
        var encodedCell = new CellEncoder().Encode(cellModel);
        var legacyCellBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "CELL", newCellId, 0u, encodedCell.Subrecords);
        var legacyBundle = new CellOverrideBundle
        {
            CellFormId = newCellId,
            Context = cellContext,
            CellRecordBytes = legacyCellBytes,
            PersistentChildRecords = [],
            VwdChildRecords = [],
            TemporaryChildRecords = [],
        };

        var encodedWrld = WrldEncoder.EncodeNew(wrldModel);
        var legacyWrldBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "WRLD", allocatedWrldId, 0u, encodedWrld.Subrecords);
        var legacyNewWorldspaces = new Dictionary<uint, NewWorldspaceEntry>
        {
            [sourceWrldId] = new(allocatedWrldId, legacyWrldBytes),
        };

        var legacyBytes = CellGrupBuilder.BuildCellSection(
            [legacyBundle], new Dictionary<uint, ParsedMainRecord>(), legacyNewWorldspaces);

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
}
