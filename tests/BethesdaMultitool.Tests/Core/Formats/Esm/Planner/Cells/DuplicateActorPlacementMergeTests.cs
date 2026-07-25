using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Proto-authored duplicate placements of master actors fold onto master's sole ref
///     (move, never duplicate — the 3× Arcade Gannon class). Multi-placement bases are
///     ambiguous and untouched; non-actors and proto-only bases are untouched.
/// </summary>
public sealed class DuplicateActorPlacementMergeTests
{
    private const uint CellId = 0x01000300;
    private const uint NpcBaseId = 0x0011E662;
    private const uint MasterAchrId = 0x000B1000;
    private const uint ProtoAchrId = 0x0010F000;

    [Fact]
    public void New_Actor_With_Sole_Master_Placement_Folds_To_Override()
    {
        var cells = Apply(
            MakeCell(NewActor(ProtoAchrId, NpcBaseId)),
            [(MasterAchrId, NpcBaseId)]);

        var child = Assert.Single(cells[CellId].TemporaryChildren);
        Assert.Equal(RecordDisposition.Override, child.Disposition);
        Assert.Equal(MasterAchrId, child.FormId);
        Assert.Equal(ProtoAchrId, child.SourceFormId);
        Assert.True(child.Reparented);
    }

    [Fact]
    public void Base_With_Multiple_Master_Placements_Is_Left_Alone()
    {
        var cells = Apply(
            MakeCell(NewActor(ProtoAchrId, NpcBaseId)),
            [(MasterAchrId, NpcBaseId), (0x000B1001, NpcBaseId)]);

        var child = Assert.Single(cells[CellId].TemporaryChildren);
        Assert.Equal(RecordDisposition.New, child.Disposition);
        Assert.Equal(ProtoAchrId, child.FormId);
    }

    [Fact]
    public void Base_Without_Master_Placement_Stays_New()
    {
        var cells = Apply(MakeCell(NewActor(ProtoAchrId, NpcBaseId)), []);

        var child = Assert.Single(cells[CellId].TemporaryChildren);
        Assert.Equal(RecordDisposition.New, child.Disposition);
    }

    [Fact]
    public void Second_Proto_Capture_Of_Same_Actor_Is_Skipped()
    {
        // The runtime shares actors into several cells — only ONE capture may fold onto the
        // master ref, or the same FormID emits repeatedly (one converted output emitted
        // 25 duplicate FormIDs).
        var cellA = MakeCell(NewActor(ProtoAchrId, NpcBaseId));
        var cellB = MakeCell(NewActor(0x0010F001, NpcBaseId)) with { CellFormId = CellId + 1 };
        var cells = ImmutableDictionary<uint, CellPlan>.Empty
            .Add(cellA.CellFormId, cellA)
            .Add(cellB.CellFormId, cellB);

        var merged = DuplicateActorPlacementMerge.Apply(cells, MasterDict([(MasterAchrId, NpcBaseId)]));

        var childA = Assert.Single(merged[CellId].TemporaryChildren);
        var childB = Assert.Single(merged[CellId + 1].TemporaryChildren);
        Assert.Equal(RecordDisposition.Override, childA.Disposition);
        Assert.Equal(MasterAchrId, childA.FormId);
        Assert.Equal(RecordDisposition.Skip, childB.Disposition);
    }

    [Fact]
    public void Captured_Master_Override_Consumes_The_Ref()
    {
        // A runtime capture of master's OWN ref already moves the actor — the proto's extra
        // placement must drop, not convert into a second override of the same FormID.
        var capturedOverride = NewActor(MasterAchrId, NpcBaseId) with
        {
            Disposition = RecordDisposition.Override
        };
        var cellA = MakeCell(capturedOverride);
        var cellB = MakeCell(NewActor(ProtoAchrId, NpcBaseId)) with { CellFormId = CellId + 1 };
        var cells = ImmutableDictionary<uint, CellPlan>.Empty
            .Add(cellA.CellFormId, cellA)
            .Add(cellB.CellFormId, cellB);

        var merged = DuplicateActorPlacementMerge.Apply(cells, MasterDict([(MasterAchrId, NpcBaseId)]));

        Assert.Equal(RecordDisposition.Override,
            Assert.Single(merged[CellId].TemporaryChildren).Disposition);
        Assert.Equal(RecordDisposition.Skip,
            Assert.Single(merged[CellId + 1].TemporaryChildren).Disposition);
    }

    [Fact]
    public void Non_Actor_Child_Is_Untouched()
    {
        var refr = NewActor(ProtoAchrId, NpcBaseId) with { Type = "REFR" };
        var cells = Apply(MakeCell(refr), [(MasterAchrId, NpcBaseId)]);

        var child = Assert.Single(cells[CellId].TemporaryChildren);
        Assert.Equal(RecordDisposition.New, child.Disposition);
    }

    // ---- fixture plumbing ----------------------------------------------------------

    private static ImmutableDictionary<uint, CellPlan> Apply(
        CellPlan plan,
        (uint RefId, uint BaseId)[] masterAchrs)
    {
        var cells = ImmutableDictionary<uint, CellPlan>.Empty.Add(plan.CellFormId, plan);
        return DuplicateActorPlacementMerge.Apply(cells, MasterDict(masterAchrs));
    }

    private static Dictionary<uint, ParsedMainRecord> MasterDict((uint RefId, uint BaseId)[] masterAchrs)
    {
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>();
        foreach (var (refId, baseId) in masterAchrs)
        {
            var name = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(name, baseId);
            masterByFormId[refId] = new ParsedMainRecord
            {
                Header = new MainRecordHeader
                {
                    Signature = "ACHR", DataSize = 0, Flags = 0x400, FormId = refId,
                    Timestamp = 0, VcsInfo = 0, Version = 15
                },
                Offset = 0,
                Subrecords = [new ParsedSubrecord { Signature = "NAME", Data = name }]
            };
        }

        return masterByFormId;
    }

    private static CellPlan MakeCell(params RecordPlan[] temporary)
    {
        return new CellPlan
        {
            CellFormId = CellId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.New,
                FormId = CellId,
                Model = new CellRecord { FormId = CellId },
                Master = null,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = new PcEsmCellContext
            {
                CellFormId = CellId,
                IsInterior = false,
                WorldspaceFormId = 0x000DA726,
                BlockGroupType = 4,
                SubblockGroupType = 5,
                BlockLabel = null,
                SubblockLabel = null
            },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [.. temporary],
            ParentWorldspaceFormId = 0x000DA726,
            Mode = CellMergeMode.LoadedReplacement
        };
    }

    private static RecordPlan NewActor(uint formId, uint baseFormId)
    {
        return new RecordPlan
        {
            Type = "ACHR",
            Disposition = RecordDisposition.New,
            FormId = formId,
            Model = new PlacedReference
            {
                FormId = formId, BaseFormId = baseFormId, RecordType = "ACHR", IsPersistent = true
            },
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }
}