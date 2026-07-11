using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Phase F verdict pass: per-placed-ref emit/drop decisions and the cell-emission
///     gates are settled at plan time (<see cref="CellChildVerdictPlanner" />), mirroring
///     the writer's decision chain. These tests pin the decision surface; the byte-parity
///     harness proves planner == writer on real dumps.
/// </summary>
public sealed class CellChildVerdictPlannerTests
{
    private const uint CellId = 0x000ABCDE;
    private const uint MasterStatBaseId = 0x000A2001;
    private const uint MasterNpcBaseId = 0x000A3001;
    private const uint NewRefId = 0x01000901;
    private const uint ActorRefId = 0x01000AAA;
    private const uint MasterRefId = 0x000B0001;
    private const uint RuntimeCloneBase = 0xFF000A48;

    [Fact]
    public void New_Ref_With_Master_Base_Gets_Emit_Verdict_With_Bucket()
    {
        var cells = Apply(MakeCell(
            temporary: [NewChild("REFR", NewRefId, Ref(NewRefId, MasterStatBaseId))]));

        var verdict = cells[CellId].RefDecisions[NewRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.Equal(MasterStatBaseId, verdict.FinalBaseFormId);
        Assert.Equal(9, verdict.TargetGroupType);
    }

    [Fact]
    public void New_Ref_With_Dangling_Base_Gets_Drop_Verdict()
    {
        var cells = Apply(MakeCell(
            temporary: [NewChild("REFR", NewRefId, Ref(NewRefId, 0x00DEAD01))]));

        var verdict = cells[CellId].RefDecisions[NewRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, verdict.Verdict);
        Assert.Equal("refr.dangling-base", verdict.DropReason);
    }

    [Fact]
    public void Leveled_Spawn_Actor_Recovers_With_Aux_Stat()
    {
        var actor = Ref(ActorRefId, RuntimeCloneBase) with
        {
            RecordType = "ACHR",
            LeveledCreatureOriginalBaseFormId = MasterNpcBaseId,
        };
        var cells = Apply(MakeCell(temporary: [NewChild("ACHR", ActorRefId, actor)]));

        var verdict = cells[CellId].RefDecisions[ActorRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.Equal(MasterNpcBaseId, verdict.FinalBaseFormId);
        Assert.Equal("refr.leveled-recovered", verdict.AuxStatCode);
    }

    [Fact]
    public void Master_Temporary_Actor_Override_Is_Suppressed_And_Covered()
    {
        var cells = Apply(
            MakeCell(temporary:
            [
                OverrideChild("ACHR", MasterRefId, Ref(MasterRefId, MasterNpcBaseId) with { RecordType = "ACHR" })
            ]),
            configureIndex: index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(CellId, 9, "ACHR"),
            extraMaster: ("ACHR", MasterRefId));

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, verdict.Verdict);
        Assert.Equal("actor.temp-override-suppressed-esm", verdict.DropReason);
        Assert.True(verdict.MarksMasterCovered);
    }

    [Fact]
    public void Override_Routes_To_Master_Bucket_And_Marks_Covered()
    {
        var cells = Apply(
            MakeCell(mode: CellMergeMode.LoadedReplacement, temporary:
            [
                OverrideChild("REFR", MasterRefId, Ref(MasterRefId, MasterStatBaseId))
            ]),
            configureIndex: index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(CellId, 8, "REFR"),
            extraMaster: ("REFR", MasterRefId));

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.Equal(8, verdict.TargetGroupType);
        Assert.True(verdict.MarksMasterCovered);
    }

    [Fact]
    public void Gate_Interior_With_No_New_Content_Suppresses()
    {
        // Only an override emit, zero NEW children ⇒ the interior stability gate fires.
        var cells = Apply(
            MakeCell(mode: CellMergeMode.LoadedReplacement, temporary:
            [
                OverrideChild("REFR", MasterRefId, Ref(MasterRefId, MasterStatBaseId))
            ]),
            extraMaster: ("REFR", MasterRefId));

        var plan = cells[CellId];
        Assert.False(plan.Emits);
        Assert.Equal("cell.interior-no-new-content-suppressed", plan.SuppressReason);
    }

    [Fact]
    public void Gate_All_Children_Dropped_Is_Itm_Suppressed()
    {
        var cells = Apply(MakeCell(
            temporary: [NewChild("REFR", NewRefId, Ref(NewRefId, 0x00DEAD01))]));

        var plan = cells[CellId];
        Assert.False(plan.Emits);
        Assert.Equal("cell.itm-override-suppressed", plan.SuppressReason);
        Assert.False(plan.NavmOnlySuppressed);
    }

    [Fact]
    public void Gate_New_Content_Cell_Emits()
    {
        var cells = Apply(MakeCell(
            temporary: [NewChild("REFR", NewRefId, Ref(NewRefId, MasterStatBaseId))]));

        var plan = cells[CellId];
        Assert.True(plan.Emits);
        Assert.Null(plan.SuppressReason);
    }

    [Fact]
    public void Gate_Navmesh_Only_Master_Cell_Is_Suppressed_With_Navm_Flag()
    {
        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = CellId,
            RawSubrecords = [new NavMeshSubrecord("DATA", [1, 2, 3, 4])]
        };
        var navmPlan = new RecordPlan
        {
            Type = "NAVM",
            Disposition = RecordDisposition.New,
            FormId = 0x01000BBB,
            Model = navm,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };

        var cells = Apply(MakeCell(temporary: [navmPlan]));

        var plan = cells[CellId];
        Assert.True(plan.NavmOnlySuppressed);
        Assert.False(plan.Emits);
        Assert.Equal("cell.itm-override-suppressed", plan.SuppressReason);
    }

    [Fact]
    public void Cell_Without_Planned_Mode_Is_Left_Untouched()
    {
        var cells = Apply(MakeCell(
            mode: null,
            temporary: [NewChild("REFR", NewRefId, Ref(NewRefId, MasterStatBaseId))]));

        var plan = cells[CellId];
        Assert.Empty(plan.RefDecisions);
        Assert.Null(plan.Emits);
    }

    // ---- fixture plumbing ----------------------------------------------------------

    private static ImmutableDictionary<uint, CellPlan> Apply(
        CellPlan cellPlan,
        Action<MasterRecordIndex>? configureIndex = null,
        (string Type, uint FormId)? extraMaster = null)
    {
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [CellId] = MakeMasterRecord("CELL", CellId),
            [MasterStatBaseId] = MakeMasterRecord("STAT", MasterStatBaseId),
            [MasterNpcBaseId] = MakeMasterRecord("NPC_", MasterNpcBaseId),
        };
        if (extraMaster is { } extra)
        {
            masterByFormId[extra.FormId] = MakeMasterRecord(extra.Type, extra.FormId);
        }

        var index = new MasterRecordIndex
        {
            Records = masterByFormId.Values.ToList(),
            RecordsByFormId = masterByFormId,
            FormIds = [.. masterByFormId.Keys],
            FormIdsByType = [],
            EditorIdToFormIdByType = [],
            StemToFormIdsByType = [],
            ChildLocations = [],
            RefToCell = [],
            RefsByCell = [],
            NavmsByCell = [],
            LandsByCell = [],
            CellContexts = [],
        };
        configureIndex?.Invoke(index);

        return CellChildVerdictPlanner.Apply(
            ImmutableDictionary<uint, CellPlan>.Empty.Add(CellId, cellPlan),
            masterByFormId,
            ImmutableDictionary<uint, uint>.Empty,
            masterByFormId.Keys.ToImmutableHashSet(),
            new CellVerdictInputs { MasterIndex = index, RecoverLeveledSpawnActors = true });
    }

    private static CellPlan MakeCell(
        RecordPlan[]? temporary = null,
        CellMergeMode? mode = CellMergeMode.LoadedReplacement)
    {
        var masterCell = MakeMasterRecord("CELL", CellId);
        var dmpCell = new CellRecord { FormId = CellId };
        return new CellPlan
        {
            CellFormId = CellId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.Override,
                FormId = CellId,
                Model = dmpCell,
                Master = masterCell,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = new PcEsmCellContext
            {
                CellFormId = CellId,
                IsInterior = true,
                BlockGroupType = 2,
                SubblockGroupType = 3,
                BlockLabel = [1, 0, 0, 0],
                SubblockLabel = [2, 0, 0, 0]
            },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = temporary is null ? ImmutableArray<RecordPlan>.Empty : [.. temporary],
            Mode = mode,
        };
    }

    private static PlacedReference Ref(uint formId, uint baseFormId) => new()
    {
        FormId = formId,
        BaseFormId = baseFormId,
        RecordType = "REFR",
        IsPersistent = false,
    };

    private static RecordPlan NewChild(string type, uint formId, PlacedReference model) =>
        MakeChild(type, formId, model, RecordDisposition.New);

    private static RecordPlan OverrideChild(string type, uint formId, PlacedReference model) =>
        MakeChild(type, formId, model, RecordDisposition.Override);

    private static RecordPlan MakeChild(
        string type, uint formId, PlacedReference model, RecordDisposition disposition) => new()
    {
        Type = type,
        Disposition = disposition,
        FormId = formId,
        Model = model,
        References = ImmutableArray<ResolvedRef>.Empty,
        ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
        Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
    };

    private static ParsedMainRecord MakeMasterRecord(string signature, uint formId) => new()
    {
        Header = new MainRecordHeader
        {
            Signature = signature, DataSize = 0, Flags = 0, FormId = formId,
            Timestamp = 0, VcsInfo = 0, Version = 15
        },
        Offset = 0
    };
}
