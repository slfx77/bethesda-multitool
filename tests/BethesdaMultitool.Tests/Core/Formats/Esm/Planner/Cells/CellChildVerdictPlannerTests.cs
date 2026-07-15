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
    private const uint MasterCreatureBaseId = 0x000A4001;
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
        Assert.False(verdict.NewInitiallyDisabled);
    }

    [Fact]
    public void Strip_New_Disabled_Refr_Without_Opposite_State_Emits_Enabled()
    {
        var placed = Ref(NewRefId, MasterStatBaseId) with { IsInitiallyDisabled = true };
        var cells = Apply(MakeCell(
            temporary: [NewChild("REFR", NewRefId, placed)],
            worldspaceFormId: 0x0010B96F));

        Assert.False(cells[CellId].RefDecisions[NewRefId].NewInitiallyDisabled);
    }

    [Fact]
    public void Strip_New_Disabled_Opposite_State_Alternative_Remains_Disabled()
    {
        var placed = Ref(NewRefId, MasterStatBaseId) with
        {
            IsInitiallyDisabled = true,
            EnableParentFormId = 0x01002415,
            EnableParentFlags = 0x01,
        };
        var cells = Apply(MakeCell(
            temporary: [NewChild("REFR", NewRefId, placed)],
            worldspaceFormId: 0x0010B96F));

        Assert.True(cells[CellId].RefDecisions[NewRefId].NewInitiallyDisabled);
    }

    [Theory]
    [InlineData("ACHR")]
    [InlineData("ACRE")]
    public void Strip_New_Disabled_Actors_Retain_Captured_State(string recordType)
    {
        var baseFormId = recordType == "ACHR" ? MasterNpcBaseId : MasterCreatureBaseId;
        var placed = Ref(NewRefId, baseFormId) with
        {
            RecordType = recordType,
            IsInitiallyDisabled = true,
        };
        var cells = Apply(MakeCell(
            temporary: [NewChild(recordType, NewRefId, placed)],
            worldspaceFormId: 0x0010B96F));

        Assert.True(cells[CellId].RefDecisions[NewRefId].NewInitiallyDisabled);
    }

    [Fact]
    public void Other_Worldspace_New_Disabled_Refr_Retains_Captured_State()
    {
        var placed = Ref(NewRefId, MasterStatBaseId) with { IsInitiallyDisabled = true };
        var cells = Apply(MakeCell(
            temporary: [NewChild("REFR", NewRefId, placed)],
            worldspaceFormId: 0x0010B970));

        Assert.True(cells[CellId].RefDecisions[NewRefId].NewInitiallyDisabled);
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
    public void Reparented_Actor_Override_In_PersistentOnly_Cell_Emits()
    {
        // A PersistentCellReparenting move into the worldspace container: the captured
        // override must APPLY — neither sparse-cell master-preservation nor the parent-cell
        // guard may drop a deliberate reparent.
        var actor = Ref(MasterRefId, MasterNpcBaseId) with { RecordType = "ACHR", IsPersistent = true };
        var cells = Apply(
            MakeCell(mode: CellMergeMode.PersistentOnly, temporary:
            [
                OverrideChild("ACHR", MasterRefId, actor, reparented: true)
            ]),
            configureIndex: index =>
            {
                index.ChildLocations[MasterRefId] = new MasterChildLocation(0x000FFFFF, 8, "ACHR");
                index.RefToCell[MasterRefId] = 0x000FFFFF; // master files the actor elsewhere
            },
            extraMaster: ("ACHR", MasterRefId));

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.Equal(8, verdict.TargetGroupType);
        Assert.True(verdict.MarksMasterCovered);
    }

    [Fact]
    public void NonReparented_Actor_Override_In_PersistentOnly_Cell_Is_Sparse_Preserved()
    {
        var actor = Ref(MasterRefId, MasterNpcBaseId) with { RecordType = "ACHR", IsPersistent = true };
        var cells = Apply(
            MakeCell(mode: CellMergeMode.PersistentOnly, temporary:
            [
                OverrideChild("ACHR", MasterRefId, actor)
            ]),
            configureIndex: index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(0x000FFFFF, 8, "ACHR"),
            extraMaster: ("ACHR", MasterRefId));

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, verdict.Verdict);
        Assert.Equal("refr.sparse-cell-master-preserved", verdict.DropReason);
    }

    [Fact]
    public void NonReparented_Actor_Override_In_Foreign_Cell_Is_Parent_Mismatch_Dropped()
    {
        var actor = Ref(MasterRefId, MasterNpcBaseId) with { RecordType = "ACHR", IsPersistent = true };
        var cells = Apply(
            MakeCell(mode: CellMergeMode.LoadedReplacement, temporary:
            [
                OverrideChild("ACHR", MasterRefId, actor)
            ]),
            configureIndex: index =>
            {
                index.ChildLocations[MasterRefId] = new MasterChildLocation(0x000FFFFF, 8, "ACHR");
                index.RefToCell[MasterRefId] = 0x000FFFFF;
            },
            extraMaster: ("ACHR", MasterRefId));

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, verdict.Verdict);
        Assert.Equal("refr.parent-cell-mismatch", verdict.DropReason);
    }

    [Fact]
    public void Reparented_Actor_Override_Applies_Proto_Enable_State()
    {
        // Master parks the cut NPC disabled (header 0x800); the proto capture has her live.
        // The reparented override must force the Initially-Disabled bit off or the move
        // places a disabled actor (EthelPhebus).
        var actor = Ref(MasterRefId, MasterNpcBaseId) with { RecordType = "ACHR", IsPersistent = true };
        var cells = Apply(
            MakeCell(mode: CellMergeMode.PersistentOnly, temporary:
            [
                OverrideChild("ACHR", MasterRefId, actor, reparented: true)
            ]),
            configureIndex: index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(0x000FFFFF, 8, "ACHR"),
            extraMaster: ("ACHR", MasterRefId),
            extraMasterFlags: 0x00000800u | 0x00000400u);

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.False(verdict.OverrideInitiallyDisabled);
    }

    [Fact]
    public void Reparented_NonPersistent_New_Ref_Emits_In_PersistentOnly_Cell()
    {
        // A rescued enable-parent marker captured without the persistent flag lands in the
        // container (PersistentOnly): the move IS the decision to emit it there.
        var marker = Ref(NewRefId, MasterStatBaseId); // IsPersistent = false
        var cells = Apply(MakeCell(mode: CellMergeMode.PersistentOnly, temporary:
        [
            NewChild("REFR", NewRefId, marker, reparented: true)
        ]));

        var verdict = cells[CellId].RefDecisions[NewRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
    }

    [Fact]
    public void Reparented_Master_Temporary_Actor_Is_Still_Suppressed()
    {
        // The engine constraint outranks the move: a master TEMPORARY actor re-emitted by an
        // ESM plugin never initializes at attach, so even a reparent must suppress it.
        var actor = Ref(MasterRefId, MasterNpcBaseId) with { RecordType = "ACHR" };
        var cells = Apply(
            MakeCell(mode: CellMergeMode.PersistentOnly, temporary:
            [
                OverrideChild("ACHR", MasterRefId, actor, reparented: true)
            ]),
            configureIndex: index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(0x000FFFFF, 9, "ACHR"),
            extraMaster: ("ACHR", MasterRefId));

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, verdict.Verdict);
        Assert.Equal("actor.temp-override-suppressed-esm", verdict.DropReason);
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
        (string Type, uint FormId)? extraMaster = null,
        uint extraMasterFlags = 0)
    {
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [CellId] = MakeMasterRecord("CELL", CellId),
            [MasterStatBaseId] = MakeMasterRecord("STAT", MasterStatBaseId),
            [MasterNpcBaseId] = MakeMasterRecord("NPC_", MasterNpcBaseId),
            [MasterCreatureBaseId] = MakeMasterRecord("CREA", MasterCreatureBaseId),
        };
        if (extraMaster is { } extra)
        {
            masterByFormId[extra.FormId] = MakeMasterRecord(extra.Type, extra.FormId, extraMasterFlags);
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
        CellMergeMode? mode = CellMergeMode.LoadedReplacement,
        uint? worldspaceFormId = null)
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
                IsInterior = worldspaceFormId is null,
                WorldspaceFormId = worldspaceFormId,
                BlockGroupType = worldspaceFormId is null ? 2 : 4,
                SubblockGroupType = worldspaceFormId is null ? 3 : 5,
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

    private static RecordPlan NewChild(
        string type, uint formId, PlacedReference model, bool reparented = false) =>
        MakeChild(type, formId, model, RecordDisposition.New, reparented);

    private static RecordPlan OverrideChild(
        string type, uint formId, PlacedReference model, bool reparented = false) =>
        MakeChild(type, formId, model, RecordDisposition.Override, reparented);

    private static RecordPlan MakeChild(
        string type, uint formId, PlacedReference model, RecordDisposition disposition,
        bool reparented = false) => new()
    {
        Type = type,
        Disposition = disposition,
        FormId = formId,
        Model = model,
        References = ImmutableArray<ResolvedRef>.Empty,
        ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
        Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        Reparented = reparented,
    };

    private static ParsedMainRecord MakeMasterRecord(string signature, uint formId, uint flags = 0) => new()
    {
        Header = new MainRecordHeader
        {
            Signature = signature, DataSize = 0, Flags = flags, FormId = formId,
            Timestamp = 0, VcsInfo = 0, Version = 15
        },
        Offset = 0
    };
}
