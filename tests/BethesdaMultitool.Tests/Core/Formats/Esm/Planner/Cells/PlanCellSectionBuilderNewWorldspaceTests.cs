using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using CellRecord = BethesdaMultitool.Core.Formats.Esm.Models.Records.World.CellRecord;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

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
    public void New_Persistent_Cell_Anchor_Has_Persistent_Record_Flag()
    {
        const uint sourceWrldId = 0x01000900u;
        const uint allocatedWrldId = 0x01000901u;
        const uint persistentCellId = 0x01000801u;

        var cellPlan = new CellPlan
        {
            CellFormId = persistentCellId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.New,
                FormId = persistentCellId,
                Model = new CellRecord
                {
                    FormId = persistentCellId,
                    WorldspaceFormId = sourceWrldId,
                    IsPersistentCell = true
                },
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = new PcEsmCellContext
            {
                CellFormId = persistentCellId,
                IsInterior = false,
                WorldspaceFormId = sourceWrldId,
                BlockGroupType = 0,
                SubblockGroupType = 0,
                BlockLabel = null,
                SubblockLabel = null
            },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
            ParentWorldspaceFormId = sourceWrldId
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
                Model = new WorldspaceRecord { FormId = sourceWrldId, EditorId = "PlanNewWrld" },
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            CellFormIds = ImmutableArray.Create(persistentCellId)
        };
        var plan = PlanTestFactory.EmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(persistentCellId, cellPlan),
            WorldspacesByFormId = ImmutableDictionary<uint, WorldspacePlan>.Empty.Add(sourceWrldId, wrldPlan),
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty.Add(sourceWrldId, allocatedWrldId)
        };

        var bytes = PlanCellSectionBuilder.BuildCellSection(
            CellPlanTestHarness.Settle(plan, new Dictionary<uint, ParsedMainRecord>()),
            new Dictionary<uint, ParsedMainRecord>(),
            new PluginBuildOptions { CompressRecords = false });

        Assert.NotNull(bytes);
        var cellOffset = FindRecord(bytes, "CELL", persistentCellId);
        Assert.True(cellOffset >= 0, "The persistent CELL anchor was not emitted.");
        Assert.Equal(0x00000400u,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cellOffset + 8, 4)));
    }

    [Fact]
    public void New_Worldspace_With_New_Cell_Emits_Through_Planner_With_Byte_Parity()
    {
        const uint sourceWrldId = 0x01000900u;
        const uint allocatedWrldId = 0x01000901u;
        const uint newCellId = 0x01000801u;

        var wrldModel = new WorldspaceRecord
        {
            FormId = sourceWrldId,
            EditorId = "PlanNewWrld"
        };
        var cellModel = new CellRecord
        {
            FormId = newCellId,
            EditorId = "PlanNewCell",
            WorldspaceFormId = sourceWrldId,
            Flags = 0, // Exterior.
            GridX = 0,
            GridY = 0
        };
        var cellContext = new PcEsmCellContext
        {
            CellFormId = newCellId,
            IsInterior = false,
            WorldspaceFormId = sourceWrldId,
            BlockGroupType = 4,
            SubblockGroupType = 5,
            BlockLabel = [0, 0, 0, 0],
            SubblockLabel = [0, 0, 0, 0]
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
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = cellContext,
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
            ParentWorldspaceFormId = sourceWrldId
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
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            CellFormIds = ImmutableArray.Create(newCellId)
        };

        var plan = PlanTestFactory.EmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(newCellId, cellPlan),
            WorldspacesByFormId = ImmutableDictionary<uint, WorldspacePlan>.Empty
                .Add(sourceWrldId, wrldPlan),
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(sourceWrldId, allocatedWrldId)
        };

        var options = new PluginBuildOptions { CompressRecords = false };
        var masters = new Dictionary<uint, ParsedMainRecord>();
        var plannerBytes = PlanCellSectionBuilder.BuildCellSection(
            CellPlanTestHarness.Settle(plan, masters, options), masters, options);

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
            TemporaryChildRecords = []
        };

        var encodedWrld = WrldEncoder.EncodeNew(wrldModel);
        var legacyWrldBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "WRLD", allocatedWrldId, 0u, encodedWrld.Subrecords);
        var legacyNewWorldspaces = new Dictionary<uint, NewWorldspaceEntry>
        {
            [sourceWrldId] = new(allocatedWrldId, legacyWrldBytes)
        };

        var legacyBytes = CellGrupBuilder.BuildCellSection(
            [legacyBundle], new Dictionary<uint, ParsedMainRecord>(), legacyNewWorldspaces);

        Assert.Equal(legacyBytes, plannerBytes);
    }

    private static int FindRecord(byte[] bytes, string signature, uint formId)
    {
        var signatureBytes = Encoding.ASCII.GetBytes(signature);
        for (var i = 0; i <= bytes.Length - 24; i++)
        {
            if (!bytes.AsSpan(i, 4).SequenceEqual(signatureBytes)
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 12, 4)) != formId)
            {
                continue;
            }

            return i;
        }

        return -1;
    }
}