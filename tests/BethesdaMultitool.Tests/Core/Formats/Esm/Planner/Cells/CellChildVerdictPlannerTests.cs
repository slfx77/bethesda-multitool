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
    private const uint MasterProjBaseId = 0x000A5001;
    private const uint NewRefId = 0x01000901;
    private const uint ActorRefId = 0x01000AAA;
    private const uint MasterRefId = 0x000B0001;
    private const uint RuntimeCloneBase = 0xFF000A48;

    [Fact]
    public void New_Ref_With_Master_Base_Gets_Emit_Verdict_With_Bucket()
    {
        var cells = Apply(MakeCell(
            [NewChild("REFR", NewRefId, Ref(NewRefId, MasterStatBaseId))]));

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
            [NewChild("REFR", NewRefId, placed)],
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
            EnableParentFlags = 0x01
        };
        var cells = Apply(MakeCell(
            [NewChild("REFR", NewRefId, placed)],
            worldspaceFormId: 0x0010B96F));

        Assert.True(cells[CellId].RefDecisions[NewRefId].NewInitiallyDisabled);
    }

    [Theory]
    [InlineData(0x00127EA0u, 0x0013BAE3u)] // First -> retail One
    [InlineData(0x00127E9Fu, 0x0013BAE2u)] // South -> retail Two
    [InlineData(0x00127EA1u, 0x0013BAE1u)] // North -> retail Three
    public void Strip_Staged_Light_Retains_Disabled_State_And_Uses_Retail_Parent(
        uint prototypeParent,
        uint retailParent)
    {
        var placed = Ref(NewRefId, MasterStatBaseId) with
        {
            IsInitiallyDisabled = true,
            EnableParentFormId = prototypeParent
        };
        var cells = Apply(MakeCell(
            [NewChild("REFR", NewRefId, placed)],
            worldspaceFormId: 0x0010B96F));

        var verdict = cells[CellId].RefDecisions[NewRefId];
        Assert.True(verdict.NewInitiallyDisabled);
        Assert.Equal(retailParent, verdict.NewEnableParentFormId);
    }

    [Theory]
    [InlineData(0x00127E9Fu)]
    [InlineData(0x00127EA0u)]
    [InlineData(0x00127EA1u)]
    public void Strip_Prototype_Starter_Marker_Retains_Disabled_State(uint markerFormId)
    {
        var placed = Ref(markerFormId, MasterStatBaseId) with { IsInitiallyDisabled = true };
        var cells = Apply(MakeCell(
            [NewChild("REFR", markerFormId, placed)],
            worldspaceFormId: 0x0010B96F));

        var verdict = cells[CellId].RefDecisions[markerFormId];
        Assert.True(verdict.NewInitiallyDisabled);
        Assert.Null(verdict.NewEnableParentFormId);
    }

    [Fact]
    public void Strip_Captured_Light_Corpus_Maps_98_Staged_Refs_And_Only_Force_Enables_18_Parentless()
    {
        var children = new List<RecordPlan>();
        var stagedIds = new List<uint>();
        var parentlessIds = new List<uint>();
        uint nextFormId = 0x01010000;

        void AddStaged(uint prototypeParent, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var formId = nextFormId++;
                stagedIds.Add(formId);
                children.Add(NewChild("REFR", formId, Ref(formId, MasterStatBaseId) with
                {
                    IsInitiallyDisabled = true,
                    EnableParentFormId = prototypeParent
                }));
            }
        }

        AddStaged(0x00127E9F, 22); // South -> retail Two
        AddStaged(0x00127EA0, 52); // First -> retail One
        AddStaged(0x00127EA1, 24); // North -> retail Three

        for (var i = 0; i < 18; i++)
        {
            var formId = nextFormId++;
            parentlessIds.Add(formId);
            children.Add(NewChild("REFR", formId,
                Ref(formId, MasterStatBaseId) with { IsInitiallyDisabled = true }));
        }

        uint[] markerIds = [0x00127E9F, 0x00127EA0, 0x00127EA1];
        foreach (var markerId in markerIds)
        {
            children.Add(NewChild("REFR", markerId,
                Ref(markerId, MasterStatBaseId) with { IsInitiallyDisabled = true }));
        }

        var cells = Apply(MakeCell(
            [.. children],
            worldspaceFormId: 0x0010B96F));
        var verdicts = cells[CellId].RefDecisions;
        var staged = stagedIds.Select(id => verdicts[id]).ToArray();
        var parentless = parentlessIds.Select(id => verdicts[id]).ToArray();

        Assert.Equal(98, staged.Length);
        Assert.All(staged, verdict => Assert.True(verdict.NewInitiallyDisabled));
        Assert.Equal(52, staged.Count(verdict => verdict.NewEnableParentFormId == 0x0013BAE3));
        Assert.Equal(22, staged.Count(verdict => verdict.NewEnableParentFormId == 0x0013BAE2));
        Assert.Equal(24, staged.Count(verdict => verdict.NewEnableParentFormId == 0x0013BAE1));

        Assert.Equal(18, parentless.Length);
        Assert.All(parentless, verdict =>
        {
            Assert.False(verdict.NewInitiallyDisabled);
            Assert.Null(verdict.NewEnableParentFormId);
        });
        Assert.All(markerIds, markerId => Assert.True(verdicts[markerId].NewInitiallyDisabled));
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
            IsInitiallyDisabled = true
        };
        var cells = Apply(MakeCell(
            [NewChild(recordType, NewRefId, placed)],
            worldspaceFormId: 0x0010B96F));

        Assert.True(cells[CellId].RefDecisions[NewRefId].NewInitiallyDisabled);
    }

    [Fact]
    public void Other_Worldspace_New_Disabled_Refr_Retains_Captured_State()
    {
        var placed = Ref(NewRefId, MasterStatBaseId) with { IsInitiallyDisabled = true };
        var cells = Apply(MakeCell(
            [NewChild("REFR", NewRefId, placed)],
            worldspaceFormId: 0x0010B970));

        Assert.True(cells[CellId].RefDecisions[NewRefId].NewInitiallyDisabled);
    }

    [Fact]
    public void New_Ref_With_Dangling_Base_Gets_Drop_Verdict()
    {
        var cells = Apply(MakeCell(
            [NewChild("REFR", NewRefId, Ref(NewRefId, 0x00DEAD01))]));

        var verdict = cells[CellId].RefDecisions[NewRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, verdict.Verdict);
        Assert.Equal("refr.dangling-base", verdict.DropReason);
    }

    [Fact]
    public void New_Pgre_With_Master_Proj_Base_Gets_Emit_Verdict()
    {
        // Captured placed grenades (mines) ride the placed-ref pipeline since 2026-08-10.
        var placed = Ref(NewRefId, MasterProjBaseId) with { RecordType = "PGRE" };
        var cells = Apply(MakeCell([NewChild("PGRE", NewRefId, placed)]));

        var verdict = cells[CellId].RefDecisions[NewRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.Equal(MasterProjBaseId, verdict.FinalBaseFormId);
        Assert.Equal(9, verdict.TargetGroupType);
    }

    [Fact]
    public void New_Pgre_With_NonProj_Base_Is_Type_Mismatch_Dropped()
    {
        // PGRE may only reference PROJ bases (all 174 retail PGREs do); a STAT base is a
        // capture artifact and must not emit.
        var placed = Ref(NewRefId, MasterStatBaseId) with { RecordType = "PGRE" };
        var cells = Apply(MakeCell([NewChild("PGRE", NewRefId, placed)]));

        var verdict = cells[CellId].RefDecisions[NewRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, verdict.Verdict);
        Assert.Equal("refr.base-type-mismatch", verdict.DropReason);
    }

    [Fact]
    public void Leveled_Spawn_Actor_Recovers_With_Aux_Stat()
    {
        var actor = Ref(ActorRefId, RuntimeCloneBase) with
        {
            RecordType = "ACHR",
            LeveledCreatureOriginalBaseFormId = MasterNpcBaseId
        };
        var cells = Apply(MakeCell([NewChild("ACHR", ActorRefId, actor)]));

        var verdict = cells[CellId].RefDecisions[ActorRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.Equal(MasterNpcBaseId, verdict.FinalBaseFormId);
        Assert.Equal("refr.leveled-recovered", verdict.AuxStatCode);
    }

    [Fact]
    public void Master_Temporary_Actor_Override_Is_Suppressed_And_Covered()
    {
        var cells = Apply(
            MakeCell([
                OverrideChild("ACHR", MasterRefId, Ref(MasterRefId, MasterNpcBaseId) with { RecordType = "ACHR" })
            ]),
            index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(CellId, 9, "ACHR"),
            ("ACHR", MasterRefId));

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
            index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(CellId, 8, "REFR"),
            ("REFR", MasterRefId));

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
                OverrideChild("ACHR", MasterRefId, actor, true)
            ]),
            index =>
            {
                index.ChildLocations[MasterRefId] = new MasterChildLocation(0x000FFFFF, 8, "ACHR");
                index.RefToCell[MasterRefId] = 0x000FFFFF; // master files the actor elsewhere
            },
            ("ACHR", MasterRefId));

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
            index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(0x000FFFFF, 8, "ACHR"),
            ("ACHR", MasterRefId));

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
            index =>
            {
                index.ChildLocations[MasterRefId] = new MasterChildLocation(0x000FFFFF, 8, "ACHR");
                index.RefToCell[MasterRefId] = 0x000FFFFF;
            },
            ("ACHR", MasterRefId));

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, verdict.Verdict);
        Assert.Equal("refr.parent-cell-mismatch", verdict.DropReason);
    }

    [Fact]
    public void Foreign_Parent_Temp_Override_In_Master_Cell_Moves_With_Capture()
    {
        // USER POLICY 2026-07-20 (Goodsprings cemetery): the capture's parent cell is the
        // proto-authored placement — the override emits HERE instead of dropping. Exterior
        // cell: the interior no-new-content gate must not eat a move-only override.
        var captured = Ref(MasterRefId, MasterStatBaseId);
        var cells = Apply(
            MakeCell(
                [OverrideChild("REFR", MasterRefId, captured)],
                worldspaceFormId: 0x000DA726),
            index =>
            {
                index.ChildLocations[MasterRefId] = new MasterChildLocation(0x000FFFFF, 9, "REFR");
                index.RefToCell[MasterRefId] = 0x000FFFFF;
            },
            ("REFR", MasterRefId));

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.Equal(9, verdict.TargetGroupType);
        Assert.True(verdict.MarksMasterCovered);
        Assert.Equal("refr.cross-cell-move", verdict.AuxStatCode);
        Assert.True(cells[CellId].Emits);
    }

    [Fact]
    public void CrossCell_Move_Applies_Proto_Enable_State()
    {
        // Master parks the moved ref disabled; the proto capture has it live — the move
        // must carry the proto state or nothing changes in-game.
        var captured = Ref(MasterRefId, MasterStatBaseId);
        var cells = Apply(
            MakeCell([OverrideChild("REFR", MasterRefId, captured)]),
            index =>
            {
                index.ChildLocations[MasterRefId] = new MasterChildLocation(0x000FFFFF, 9, "REFR");
                index.RefToCell[MasterRefId] = 0x000FFFFF;
            },
            ("REFR", MasterRefId),
            0x00000800);

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.False(verdict.OverrideInitiallyDisabled);
    }

    [Fact]
    public void Foreign_Parent_Override_In_New_Worldspace_Cell_Still_Drops()
    {
        // NEW-worldspace capture cells keep the drop — retail records stay untouched there
        // (doors clone as fresh NEW refs via OverrideDoorCloning instead).
        var captured = Ref(MasterRefId, MasterStatBaseId);
        var cells = Apply(
            MakeCell(
                [OverrideChild("REFR", MasterRefId, captured)],
                worldspaceFormId: 0x01000FFF,
                masterAnchored: false),
            index =>
            {
                index.ChildLocations[MasterRefId] = new MasterChildLocation(0x000FFFFF, 9, "REFR");
                index.RefToCell[MasterRefId] = 0x000FFFFF;
            },
            ("REFR", MasterRefId));

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, verdict.Verdict);
        Assert.Equal("refr.parent-cell-mismatch", verdict.DropReason);
    }

    [Fact]
    public void Home_Cell_Capture_Beats_CrossCell_Move()
    {
        const uint homeCellId = 0x000ABD00;
        var captured = Ref(MasterRefId, MasterStatBaseId);
        var foreignCell = MakeCell([OverrideChild("REFR", MasterRefId, captured)]);
        var homeCell = MakeCell(
            [OverrideChild("REFR", MasterRefId, captured)], cellFormId: homeCellId);

        var cells = ApplyCells(
            [foreignCell, homeCell],
            index =>
            {
                index.ChildLocations[MasterRefId] = new MasterChildLocation(homeCellId, 9, "REFR");
                index.RefToCell[MasterRefId] = homeCellId;
            },
            [("REFR", MasterRefId, 0u)]);

        // The home cell's own capture wins the FormID even though the foreign cell sorts
        // first; the foreign copy is a duplicate capture of the same ref.
        Assert.Equal(PlacedRefEmitVerdict.Emit, cells[homeCellId].RefDecisions[MasterRefId].Verdict);
        Assert.Null(cells[homeCellId].RefDecisions[MasterRefId].AuxStatCode);
        var foreignVerdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, foreignVerdict.Verdict);
        Assert.Equal("refr.cross-cell-duplicate", foreignVerdict.DropReason);
    }

    [Fact]
    public void CrossCell_Move_Duplicate_Capture_First_Cell_Claims()
    {
        const uint otherCellId = 0x000ABD00; // Sorts after CellId → CellId claims first.
        var captured = Ref(MasterRefId, MasterStatBaseId);
        var cellA = MakeCell([OverrideChild("REFR", MasterRefId, captured)]);
        var cellB = MakeCell(
            [OverrideChild("REFR", MasterRefId, captured)], cellFormId: otherCellId);

        var cells = ApplyCells(
            [cellB, cellA],
            index =>
            {
                index.ChildLocations[MasterRefId] = new MasterChildLocation(0x000FFFFF, 9, "REFR");
                index.RefToCell[MasterRefId] = 0x000FFFFF;
            },
            [("REFR", MasterRefId, 0u)]);

        Assert.Equal(PlacedRefEmitVerdict.Emit, cells[CellId].RefDecisions[MasterRefId].Verdict);
        var loser = cells[otherCellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Drop, loser.Verdict);
        Assert.Equal("refr.cross-cell-duplicate", loser.DropReason);
    }

    [Fact]
    public void Reparented_Actor_Override_Preserves_Master_Enable_State()
    {
        // USER POLICY 2026-08-04: a DMP is a mid-session snapshot, so a quest-gated actor the
        // player already unlocked reads back enabled. PersistentCellReparenting is only a
        // file-format normalization (exterior persistents must live in the worldspace
        // container), so it is NOT evidence the proto authored a different enable-state —
        // master's Initially-Disabled bit must survive. Regression guard for Powder Gangers
        // active in Goodsprings from game start (GSJoeCobb 0x00104C68 et al).
        // Genuine relocation (isForeignParent — master files the ref in a DIFFERENT cell) still
        // applies the proto's enable-state; that is the EthelPhebus path and is unchanged.
        var actor = Ref(MasterRefId, MasterNpcBaseId) with { RecordType = "ACHR", IsPersistent = true };
        var cells = Apply(
            MakeCell(mode: CellMergeMode.PersistentOnly, temporary:
            [
                OverrideChild("ACHR", MasterRefId, actor, true)
            ]),
            index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(0x000FFFFF, 8, "ACHR"),
            ("ACHR", MasterRefId),
            0x00000800u | 0x00000400u);

        var verdict = cells[CellId].RefDecisions[MasterRefId];
        Assert.Equal(PlacedRefEmitVerdict.Emit, verdict.Verdict);
        Assert.Null(verdict.OverrideInitiallyDisabled);
    }

    [Fact]
    public void Reparented_NonPersistent_New_Ref_Emits_In_PersistentOnly_Cell()
    {
        // A rescued enable-parent marker captured without the persistent flag lands in the
        // container (PersistentOnly): the move IS the decision to emit it there.
        var marker = Ref(NewRefId, MasterStatBaseId); // IsPersistent = false
        var cells = Apply(MakeCell(mode: CellMergeMode.PersistentOnly, temporary:
        [
            NewChild("REFR", NewRefId, marker, true)
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
                OverrideChild("ACHR", MasterRefId, actor, true)
            ]),
            index => index.ChildLocations[MasterRefId] =
                new MasterChildLocation(0x000FFFFF, 9, "ACHR"),
            ("ACHR", MasterRefId));

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
            [NewChild("REFR", NewRefId, Ref(NewRefId, 0x00DEAD01))]));

        var plan = cells[CellId];
        Assert.False(plan.Emits);
        Assert.Equal("cell.itm-override-suppressed", plan.SuppressReason);
        Assert.False(plan.NavmOnlySuppressed);
    }

    [Fact]
    public void Gate_New_Content_Cell_Emits()
    {
        var cells = Apply(MakeCell(
            [NewChild("REFR", NewRefId, Ref(NewRefId, MasterStatBaseId))]));

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

        var cells = Apply(MakeCell([navmPlan]));

        var plan = cells[CellId];
        Assert.True(plan.NavmOnlySuppressed);
        Assert.False(plan.Emits);
        Assert.Equal("cell.itm-override-suppressed", plan.SuppressReason);
    }

    [Fact]
    public void Gate_Land_Only_Master_Cell_Emits()
    {
        // Legacy hasCapturedTerrain parity: a master exterior cell whose only captured
        // change is terrain must not be ITM-suppressed — the LAND override IS the content.
        var landPlan = new RecordPlan
        {
            Type = "LAND",
            Disposition = RecordDisposition.Override,
            FormId = 0x000AB001,
            Model = new CellLandDecision
            {
                CellSourceFormId = CellId,
                Heightmap = new LandHeightmap { HeightDeltas = new sbyte[33 * 33] },
                HeightSource = CellLandHeightSource.CapturedHeightmap,
                MasterLandFormId = 0x000AB001
            },
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };

        var cells = Apply(MakeCell([landPlan], worldspaceFormId: 0x000DA726));

        var plan = cells[CellId];
        Assert.True(plan.Emits);
        Assert.Null(plan.SuppressReason);
        Assert.False(plan.NavmOnlySuppressed);
    }

    [Fact]
    public void Cell_Without_Planned_Mode_Fails_Before_Verdict_Emission()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Apply(MakeCell(
            mode: null,
            temporary: [NewChild("REFR", NewRefId, Ref(NewRefId, MasterStatBaseId))])));

        Assert.Contains($"0x{CellId:X8}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("without a settled merge mode", exception.Message, StringComparison.Ordinal);
    }

    // ---- fixture plumbing ----------------------------------------------------------

    private static ImmutableDictionary<uint, CellPlan> Apply(
        CellPlan cellPlan,
        Action<MasterRecordIndex>? configureIndex = null,
        (string Type, uint FormId)? extraMaster = null,
        uint extraMasterFlags = 0)
    {
        return ApplyCells(
            [cellPlan], configureIndex,
            extraMaster is { } extra ? [(extra.Type, extra.FormId, extraMasterFlags)] : []);
    }

    private static ImmutableDictionary<uint, CellPlan> ApplyCells(
        CellPlan[] cellPlans,
        Action<MasterRecordIndex>? configureIndex = null,
        (string Type, uint FormId, uint Flags)[]? extraMasters = null)
    {
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [MasterStatBaseId] = MakeMasterRecord("STAT", MasterStatBaseId),
            [MasterNpcBaseId] = MakeMasterRecord("NPC_", MasterNpcBaseId),
            [MasterCreatureBaseId] = MakeMasterRecord("CREA", MasterCreatureBaseId),
            [MasterProjBaseId] = MakeMasterRecord("PROJ", MasterProjBaseId)
        };
        var cells = ImmutableDictionary.CreateBuilder<uint, CellPlan>();
        foreach (var cellPlan in cellPlans)
        {
            cells.Add(cellPlan.CellFormId, cellPlan);
            if (cellPlan.CellRecordPlan.Master is not null)
            {
                masterByFormId[cellPlan.CellFormId] = MakeMasterRecord("CELL", cellPlan.CellFormId);
            }
        }

        foreach (var (type, formId, flags) in extraMasters ?? [])
        {
            masterByFormId[formId] = MakeMasterRecord(type, formId, flags);
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
            CellContexts = []
        };
        configureIndex?.Invoke(index);

        return CellChildVerdictPlanner.Apply(
            cells.ToImmutable(),
            masterByFormId,
            ImmutableDictionary<uint, uint>.Empty,
            masterByFormId.Keys.ToImmutableHashSet(),
            new CellVerdictInputs { MasterIndex = index, RecoverLeveledSpawnActors = true });
    }

    private static CellPlan MakeCell(
        RecordPlan[]? temporary = null,
        CellMergeMode? mode = CellMergeMode.LoadedReplacement,
        uint? worldspaceFormId = null,
        uint cellFormId = CellId,
        bool masterAnchored = true)
    {
        var masterCell = MakeMasterRecord("CELL", cellFormId);
        var dmpCell = new CellRecord { FormId = cellFormId };
        return new CellPlan
        {
            CellFormId = cellFormId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = masterAnchored ? RecordDisposition.Override : RecordDisposition.New,
                FormId = cellFormId,
                Model = dmpCell,
                Master = masterAnchored ? masterCell : null,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = new PcEsmCellContext
            {
                CellFormId = cellFormId,
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
            Mode = mode
        };
    }

    private static PlacedReference Ref(uint formId, uint baseFormId)
    {
        return new PlacedReference
        {
            FormId = formId,
            BaseFormId = baseFormId,
            RecordType = "REFR",
            IsPersistent = false
        };
    }

    private static RecordPlan NewChild(
        string type, uint formId, PlacedReference model, bool reparented = false)
    {
        return MakeChild(type, formId, model, RecordDisposition.New, reparented);
    }

    private static RecordPlan OverrideChild(
        string type, uint formId, PlacedReference model, bool reparented = false)
    {
        return MakeChild(type, formId, model, RecordDisposition.Override, reparented);
    }

    private static RecordPlan MakeChild(
        string type, uint formId, PlacedReference model, RecordDisposition disposition,
        bool reparented = false)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = disposition,
            FormId = formId,
            Model = model,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
            Reparented = reparented
        };
    }

    private static ParsedMainRecord MakeMasterRecord(string signature, uint formId, uint flags = 0)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature, DataSize = 0, Flags = flags, FormId = formId,
                Timestamp = 0, VcsInfo = 0, Version = 15
            },
            Offset = 0
        };
    }
}