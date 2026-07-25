using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class NavmDoorLinkPlannerTests
{
    private const uint DoorBase = 0x0002E6E4;
    private const uint SourceDoorRef = 0x0011662F;
    private const uint EmittedDoorRef = 0x010022E0;

    [Fact]
    public void Build_MapsClonedDoorAndKeepsMasterAndCloneLive()
    {
        var plan = MakePlan(MakeDoorClone(EmittedDoorRef));
        var master = MakeMasterRecords();

        var result = NavmDoorLinkPlanner.Build(plan, master);

        Assert.Equal(EmittedDoorRef, result.SourceToEmittedDoorRef[SourceDoorRef]);
        Assert.Contains(SourceDoorRef, result.ValidDoorRefFormIds);
        Assert.Contains(EmittedDoorRef, result.ValidDoorRefFormIds);
    }

    [Fact]
    public void Build_DroppedCloneIsNotMappedOrLive()
    {
        var clone = MakeDoorClone(EmittedDoorRef);
        var cell = MakeCell([clone]) with
        {
            RefDecisions = ImmutableDictionary<uint, PlacedRefDecision>.Empty.Add(
                EmittedDoorRef,
                new PlacedRefDecision
                {
                    Verdict = PlacedRefEmitVerdict.Drop,
                    DropReason = "test",
                    TargetGroupType = 9
                })
        };
        var plan = MakePlan(cell);

        var result = NavmDoorLinkPlanner.Build(plan, MakeMasterRecords());

        Assert.False(result.SourceToEmittedDoorRef.ContainsKey(SourceDoorRef));
        Assert.DoesNotContain(EmittedDoorRef, result.ValidDoorRefFormIds);
        Assert.Contains(SourceDoorRef, result.ValidDoorRefFormIds);
    }

    [Fact]
    public void Build_AmbiguousDuplicateCloneFails()
    {
        var plan = MakePlan(MakeDoorClone(EmittedDoorRef), MakeDoorClone(EmittedDoorRef + 1));

        var error = Assert.Throws<InvalidOperationException>(() =>
            NavmDoorLinkPlanner.Build(plan, MakeMasterRecords()));

        Assert.Contains($"0x{SourceDoorRef:X8}", error.Message);
    }

    private static EmitPlan MakePlan(params RecordPlan[] children)
    {
        return MakePlan(MakeCell(children));
    }

    private static EmitPlan MakePlan(CellPlan cell)
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
                MasterPath = "master.esm",
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet<string>.Empty
            },
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(cell.CellFormId, cell)
        };
    }

    private static CellPlan MakeCell(IReadOnlyList<RecordPlan> children)
    {
        return new CellPlan
        {
            CellFormId = 0x01000300,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.New,
                FormId = 0x01000300,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = new PcEsmCellContext
            {
                CellFormId = 0x01000300,
                IsInterior = false,
                WorldspaceFormId = 0x01000200,
                BlockGroupType = 4,
                SubblockGroupType = 5,
                BlockLabel = [0, 0, 0, 0],
                SubblockLabel = [0, 0, 0, 0]
            },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [.. children],
            Emits = true
        };
    }

    private static RecordPlan MakeDoorClone(uint emittedFormId)
    {
        return new RecordPlan
        {
            Type = "REFR",
            Disposition = RecordDisposition.New,
            FormId = emittedFormId,
            SourceFormId = SourceDoorRef,
            Model = new PlacedReference
            {
                FormId = SourceDoorRef,
                BaseFormId = DoorBase,
                RecordType = "REFR"
            },
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "OverrideDoorCloning", Reason = "test" }
        };
    }

    private static Dictionary<uint, ParsedMainRecord> MakeMasterRecords()
    {
        return new Dictionary<uint, ParsedMainRecord>
        {
            [DoorBase] = MakeRecord("DOOR", DoorBase),
            [SourceDoorRef] = MakeRecord("REFR", SourceDoorRef, DoorBase)
        };
    }

    private static ParsedMainRecord MakeRecord(string signature, uint formId, uint? baseFormId = null)
    {
        var subrecords = new List<ParsedSubrecord>();
        if (baseFormId.HasValue)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, baseFormId.Value);
            subrecords.Add(new ParsedSubrecord { Signature = "NAME", Data = payload });
        }

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                DataSize = 0,
                Flags = 0,
                FormId = formId,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 15
            },
            Offset = 0,
            Subrecords = subrecords
        };
    }
}