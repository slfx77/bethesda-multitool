using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class DoorTeleportTargetRescueTests
{
    private const uint ProtoWorldspace = 0x0010B96F;
    private const uint RetailWorldspace = 0x000DA726;
    private const uint ProtoCell = 0x01000300;
    private const uint RetailExteriorCell = 0x000E1948;
    private const uint InteriorCell = 0x0010EDCD;
    private const uint DoorBase = 0x000463CD;
    private const uint StaticBase = 0x0004E7F4;
    private const uint SourceDoorRef = 0x001130DF;
    private const uint TargetDoorRef = 0x0010F076;

    [Fact]
    public void Captured_Door_Counterpart_Is_Cloned_And_Reciprocal_Xtels_Are_Rewritten()
    {
        var source = DoorChild(SourceDoorRef, TargetDoorRef, 3);
        var target = DoorChild(TargetDoorRef, SourceDoorRef, 7);
        var cells = ImmutableDictionary<uint, CellPlan>.Empty
            .Add(ProtoCell, Cell(ProtoCell, ProtoWorldspace, false, null, source))
            .Add(InteriorCell, Cell(InteriorCell, 0, true,
                Record("CELL", InteriorCell), target));

        var result = OverrideDoorCloning.Apply(
            cells, MasterContexts(), MasterRecords(), MasterRefToCell(), new FormIdAllocator(),
            out var clonedFormIds, out var diagnostics);

        var sourceClone = Assert.Single(result[ProtoCell].TemporaryChildren);
        var targetClone = Assert.Single(result[InteriorCell].TemporaryChildren);
        Assert.Equal(RecordDisposition.New, sourceClone.Disposition);
        Assert.Equal(RecordDisposition.New, targetClone.Disposition);
        Assert.Equal(SourceDoorRef, sourceClone.SourceFormId);
        Assert.Equal(TargetDoorRef, targetClone.SourceFormId);
        Assert.Equal(targetClone.FormId,
            Assert.IsType<PlacedReference>(sourceClone.Model).DestinationDoorFormId);
        Assert.Equal(sourceClone.FormId,
            Assert.IsType<PlacedReference>(targetClone.Model).DestinationDoorFormId);
        Assert.Equal((byte?)3, Assert.IsType<PlacedReference>(sourceClone.Model).TeleportFlags);
        Assert.Equal((byte?)7, Assert.IsType<PlacedReference>(targetClone.Model).TeleportFlags);
        Assert.Equal([sourceClone.FormId, targetClone.FormId], clonedFormIds.Order());
        Assert.Contains(diagnostics,
            diagnostic => diagnostic.Code == "references.repair.xtel-static-target-cloned"
                          && diagnostic.FormId == TargetDoorRef);

        // The retail target is still the original static-base REFR; the repair did not
        // repurpose or delete it.
        Assert.Equal(StaticBase, ReadName(MasterRecords()[TargetDoorRef]));
    }

    [Fact]
    public void Missing_Captured_Counterpart_Drops_Incompatible_Xtel_With_Diagnostic()
    {
        var source = DoorChild(SourceDoorRef, TargetDoorRef, 3);
        var cells = ImmutableDictionary<uint, CellPlan>.Empty
            .Add(ProtoCell, Cell(ProtoCell, ProtoWorldspace, false, null, source));

        var result = OverrideDoorCloning.Apply(
            cells, MasterContexts(), MasterRecords(), MasterRefToCell(), new FormIdAllocator(),
            out var clonedFormIds, out var diagnostics);

        var sourceClone = Assert.Single(result[ProtoCell].TemporaryChildren);
        var placed = Assert.IsType<PlacedReference>(sourceClone.Model);
        Assert.Null(placed.DestinationDoorFormId);
        Assert.Null(placed.TeleportFlags);
        Assert.Single(clonedFormIds);
        Assert.Contains(diagnostics,
            diagnostic => diagnostic.Code == "references.drop.xtel-target-not-door"
                          && diagnostic.FormId == sourceClone.FormId);
    }

    private static CellPlan Cell(
        uint formId,
        uint worldspace,
        bool isInterior,
        ParsedMainRecord? master,
        params RecordPlan[] temporary)
    {
        return new CellPlan
        {
            CellFormId = formId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = master is null ? RecordDisposition.New : RecordDisposition.Override,
                FormId = formId,
                Model = new CellRecord { FormId = formId },
                Master = master,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = Provenance()
            },
            Context = new PcEsmCellContext
            {
                CellFormId = formId,
                IsInterior = isInterior,
                WorldspaceFormId = worldspace,
                BlockGroupType = isInterior ? 2 : 4,
                SubblockGroupType = isInterior ? 3 : 5
            },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [.. temporary],
            ParentWorldspaceFormId = worldspace,
            Mode = CellMergeMode.LoadedReplacement
        };
    }

    private static RecordPlan DoorChild(uint formId, uint destination, byte teleportFlags)
    {
        return new RecordPlan
        {
            Type = "REFR",
            Disposition = RecordDisposition.Override,
            FormId = formId,
            Model = new PlacedReference
            {
                FormId = formId,
                BaseFormId = DoorBase,
                RecordType = "REFR",
                DestinationDoorFormId = destination,
                TeleportFlags = teleportFlags,
                X = formId == SourceDoorRef ? 100f : 200f
            },
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = Provenance()
        };
    }

    private static Dictionary<uint, PcEsmCellContext> MasterContexts()
    {
        return new Dictionary<uint, PcEsmCellContext>
        {
            [RetailExteriorCell] = new()
            {
                CellFormId = RetailExteriorCell,
                IsInterior = false,
                WorldspaceFormId = RetailWorldspace,
                BlockGroupType = 4,
                SubblockGroupType = 5
            },
            [InteriorCell] = new()
            {
                CellFormId = InteriorCell,
                IsInterior = true,
                BlockGroupType = 2,
                SubblockGroupType = 3
            }
        };
    }

    private static Dictionary<uint, uint> MasterRefToCell()
    {
        return new Dictionary<uint, uint>
        {
            [SourceDoorRef] = RetailExteriorCell,
            [TargetDoorRef] = InteriorCell
        };
    }

    private static Dictionary<uint, ParsedMainRecord> MasterRecords()
    {
        return new Dictionary<uint, ParsedMainRecord>
        {
            [DoorBase] = Record("DOOR", DoorBase),
            [StaticBase] = Record("STAT", StaticBase),
            [RetailExteriorCell] = Record("CELL", RetailExteriorCell),
            [InteriorCell] = Record("CELL", InteriorCell),
            [SourceDoorRef] = Record("REFR", SourceDoorRef, DoorBase, 5000f),
            [TargetDoorRef] = Record("REFR", TargetDoorRef, StaticBase, 200f)
        };
    }

    private static ParsedMainRecord Record(
        string signature,
        uint formId,
        uint? name = null,
        float? x = null)
    {
        var subrecords = new List<ParsedSubrecord>();
        if (name is { } baseFormId)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, baseFormId);
            subrecords.Add(new ParsedSubrecord { Signature = "NAME", Data = bytes });
        }

        if (x is { } positionX)
        {
            var bytes = new byte[24];
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), positionX);
            subrecords.Add(new ParsedSubrecord { Signature = "DATA", Data = bytes });
        }

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = signature, FormId = formId },
            Subrecords = subrecords
        };
    }

    private static uint ReadName(ParsedMainRecord record)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(
            Assert.Single(record.Subrecords, subrecord => subrecord.Signature == "NAME").Data);
    }

    private static PlanProvenance Provenance()
    {
        return new PlanProvenance { PolicyId = "test", Reason = "test" };
    }
}