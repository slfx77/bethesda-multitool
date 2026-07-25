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

/// <summary>
///     Master doors the proto re-placed in NEW-worldspace cells clone as fresh NEW REFRs
///     (placement + teleport preserved) instead of dying at the parent-cell guard — unless
///     the captured placement overlaps master's own placement in the same worldspace.
/// </summary>
public sealed class OverrideDoorCloningTests
{
    private const uint ProtoWorldspaceId = 0x0010B96F;
    private const uint ProtoCellId = 0x01000300;
    private const uint MasterWorldspaceId = 0x000DA726;
    private const uint MasterCellId = 0x000E1948;
    private const uint DoorBaseId = 0x000A4001;
    private const uint StatBaseId = 0x000A2001;
    private const uint DoorRefId = 0x000B0010;

    [Fact]
    public void Door_Override_In_New_Worldspace_Cell_Clones_As_New()
    {
        var original = OverrideDoorChild(DoorRefId, 100f);
        var cells = Apply(MakeProtoCell(original));

        var child = Assert.Single(cells[ProtoCellId].TemporaryChildren);
        Assert.Equal(RecordDisposition.New, child.Disposition);
        Assert.True(child.FormId >= 0x01000000u); // fresh plugin-range FormID
        Assert.Equal(DoorRefId, child.SourceFormId);
        Assert.Null(child.Master);
        Assert.Same(original.Model, child.Model); // captured placement + XTEL kept verbatim
    }

    [Fact]
    public void Overlapping_Same_Worldspace_Door_Stays_Override()
    {
        // Master places the same door REFR in the SAME worldspace within 1 unit of the
        // captured position — a clone would double the door.
        var cell = MakeCell(ProtoCellId, MasterWorldspaceId, OverrideDoorChild(DoorRefId, 100f));

        var cells = Apply(cell, (DoorRefId, 100.4f));

        var child = Assert.Single(cells[ProtoCellId].TemporaryChildren);
        Assert.Equal(RecordDisposition.Override, child.Disposition);
        Assert.Equal(DoorRefId, child.FormId);
    }

    [Fact]
    public void Distant_Same_Worldspace_Door_Clones()
    {
        var cell = MakeCell(ProtoCellId, MasterWorldspaceId, OverrideDoorChild(DoorRefId, 100f));

        var cells = Apply(cell, (DoorRefId, 5000f));

        var child = Assert.Single(cells[ProtoCellId].TemporaryChildren);
        Assert.Equal(RecordDisposition.New, child.Disposition);
    }

    [Fact]
    public void Non_Door_Override_Is_Untouched()
    {
        var statChild = MakeChild("REFR", DoorRefId, StatBaseId, RecordDisposition.Override, 100f);
        var cells = Apply(MakeProtoCell(statChild));

        var child = Assert.Single(cells[ProtoCellId].TemporaryChildren);
        Assert.Equal(RecordDisposition.Override, child.Disposition);
        Assert.Equal(DoorRefId, child.FormId);
    }

    [Fact]
    public void Master_Anchored_Cell_Is_Untouched()
    {
        var cell = MakeCell(MasterCellId, MasterWorldspaceId, OverrideDoorChild(DoorRefId, 100f)) with
        {
            CellRecordPlan = MakeCellRecordPlan(MasterCellId, MakeMasterRecord("CELL", MasterCellId))
        };

        var cells = Apply(cell);

        var child = Assert.Single(cells[MasterCellId].TemporaryChildren);
        Assert.Equal(RecordDisposition.Override, child.Disposition);
    }

    // ---- fixture plumbing ----------------------------------------------------------

    private static ImmutableDictionary<uint, CellPlan> Apply(
        CellPlan plan,
        (uint RefId, float MasterX)? masterRefPosition = null)
    {
        var masterContexts = new Dictionary<uint, PcEsmCellContext>
        {
            [MasterCellId] = new()
            {
                CellFormId = MasterCellId,
                IsInterior = false,
                WorldspaceFormId = MasterWorldspaceId,
                BlockGroupType = 4,
                SubblockGroupType = 5,
                BlockLabel = [0, 0, 0, 0],
                SubblockLabel = [0, 0, 0, 0]
            }
        };
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [DoorBaseId] = MakeMasterRecord("DOOR", DoorBaseId),
            [StatBaseId] = MakeMasterRecord("STAT", StatBaseId),
            [MasterCellId] = MakeMasterRecord("CELL", MasterCellId),
            [DoorRefId] = MakeMasterRefr(DoorRefId, masterRefPosition?.MasterX ?? 0f)
        };
        var masterRefToCell = new Dictionary<uint, uint> { [DoorRefId] = MasterCellId };

        var cells = ImmutableDictionary<uint, CellPlan>.Empty.Add(plan.CellFormId, plan);
        return OverrideDoorCloning.Apply(
            cells, masterContexts, masterByFormId, masterRefToCell, new FormIdAllocator());
    }

    private static CellPlan MakeProtoCell(params RecordPlan[] temporary)
    {
        return MakeCell(ProtoCellId, ProtoWorldspaceId, temporary);
    }

    private static CellPlan MakeCell(uint cellFormId, uint worldspaceId, params RecordPlan[] temporary)
    {
        return new CellPlan
        {
            CellFormId = cellFormId,
            CellRecordPlan = MakeCellRecordPlan(cellFormId, null),
            Context = new PcEsmCellContext
            {
                CellFormId = cellFormId,
                IsInterior = false,
                WorldspaceFormId = worldspaceId,
                BlockGroupType = 4,
                SubblockGroupType = 5,
                BlockLabel = null,
                SubblockLabel = null
            },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [.. temporary],
            ParentWorldspaceFormId = worldspaceId,
            Mode = CellMergeMode.LoadedReplacement
        };
    }

    private static RecordPlan MakeCellRecordPlan(uint cellFormId, ParsedMainRecord? master)
    {
        return new RecordPlan
        {
            Type = "CELL",
            Disposition = master is null ? RecordDisposition.New : RecordDisposition.Override,
            FormId = cellFormId,
            Model = new CellRecord { FormId = cellFormId },
            Master = master,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static RecordPlan OverrideDoorChild(uint formId, float x)
    {
        return MakeChild("REFR", formId, DoorBaseId, RecordDisposition.Override, x);
    }

    private static RecordPlan MakeChild(
        string type, uint formId, uint baseFormId, RecordDisposition disposition, float x)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = disposition,
            FormId = formId,
            Model = new PlacedReference
            {
                FormId = formId, BaseFormId = baseFormId, RecordType = type, X = x, Y = 0f, Z = 0f
            },
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static ParsedMainRecord MakeMasterRecord(string signature, uint formId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature, DataSize = 0, Flags = 0, FormId = formId,
                Timestamp = 0, VcsInfo = 0, Version = 15
            },
            Offset = 0
        };
    }

    private static ParsedMainRecord MakeMasterRefr(uint formId, float x)
    {
        var data = new byte[24];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0, 4), x);
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR", DataSize = 0, Flags = 0, FormId = formId,
                Timestamp = 0, VcsInfo = 0, Version = 15
            },
            Offset = 0,
            Subrecords = [new ParsedSubrecord { Signature = "DATA", Data = data }]
        };
    }
}