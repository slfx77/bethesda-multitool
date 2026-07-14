using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm;
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
///     Exterior persistents must move to the worldspace's persistent-container cell — the
///     FNV loader never reads Persistent-Children GRUPs under exterior grid cells (in-game
///     proven: prid on a NEW ref there reports "not found"; Override marker renames never
///     applied). NEW refs and ALL persistent actors (move policy: the DMP placement wins)
///     move; non-actor overrides and interior persistents stay put. Parse-side orphan
///     buckets (IsVirtual) get the same actor rescue and are then removed — their invented
///     EditorIds must never reach the output.
/// </summary>
public sealed class PersistentCellReparentingTests
{
    private const uint WorldspaceId = 0x000DA726;
    private const uint ContainerCellId = 0x000DA727;
    private const uint GridCellId = 0x000E1948;
    private const uint InteriorCellId = 0x000ABCDE;
    private const uint OrphanBucketId = 0xFE000123;
    private const uint NewActorRefId = 0x01001F6F;
    private const uint MasterRefId = 0x000B0001;

    [Fact]
    public void New_Persistent_Actor_In_Master_Grid_Cell_Moves_To_Container()
    {
        var cells = Apply(MakeGridCellPlan(
            NewChild("ACHR", NewActorRefId), OverrideChild("REFR", MasterRefId)));

        var grid = cells[GridCellId];
        var container = cells[ContainerCellId];

        Assert.DoesNotContain(grid.PersistentChildren, c => c.FormId == NewActorRefId);
        Assert.Contains(grid.PersistentChildren, c => c.FormId == MasterRefId); // override stays
        Assert.Contains(container.PersistentChildren, c => c.FormId == NewActorRefId);
        Assert.Equal(RecordDisposition.Override, container.CellRecordPlan.Disposition);
        Assert.True(container.Context.IsPersistentCellContainer);
        Assert.Equal(CellMergeMode.PersistentOnly, container.Mode);
    }

    [Fact]
    public void Map_Marker_Override_In_Grid_Cell_Moves_To_Container()
    {
        // A renamed/moved master marker is an Override captured in its grid cell; the loader
        // only shows markers from the worldspace persistent cell, so it must move — unlike a
        // plain override, which stays (form pre-exists via per-FormID merge).
        var marker = OverrideMarker("REFR", MasterRefId);
        var plainOverride = OverrideChild("REFR", 0x000B0002);
        var grid = MakeCellPlan(GridCellId, MakeExteriorContext(GridCellId), [marker, plainOverride]);

        var cells = Apply(grid);

        Assert.DoesNotContain(cells[GridCellId].PersistentChildren, c => c.FormId == MasterRefId);
        Assert.Contains(cells[GridCellId].PersistentChildren, c => c.FormId == 0x000B0002); // plain override stays
        Assert.Contains(cells[ContainerCellId].PersistentChildren, c => c.FormId == MasterRefId);
    }

    [Fact]
    public void Map_Marker_In_Temporary_Bucket_Also_Moves()
    {
        // Runtime capture may not flag a marker persistent; it still must reach the container.
        var grid = new CellPlan
        {
            CellFormId = GridCellId,
            CellRecordPlan = MakeCellPlan(GridCellId, MakeExteriorContext(GridCellId), []).CellRecordPlan,
            Context = MakeExteriorContext(GridCellId),
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [NewMarker("REFR", NewActorRefId)],
            Mode = CellMergeMode.LoadedReplacement,
        };

        var cells = Apply(grid);

        Assert.Empty(cells[GridCellId].TemporaryChildren);
        Assert.Contains(cells[ContainerCellId].PersistentChildren, c => c.FormId == NewActorRefId);
    }

    [Fact]
    public void Override_Persistent_Actor_In_Master_Grid_Cell_Moves_To_Container_Reparented()
    {
        // Move policy: an actor the DMP places elsewhere is MOVED — the master ACHR's
        // override goes to the container (where the loader reads it), flagged Reparented so
        // the verdict pass applies it. Non-actor overrides stay put.
        var cells = Apply(MakeGridCellPlan(
            OverrideChild("ACHR", MasterRefId), OverrideChild("REFR", 0x000B0002)));

        var grid = cells[GridCellId];
        var container = cells[ContainerCellId];

        Assert.DoesNotContain(grid.PersistentChildren, c => c.FormId == MasterRefId);
        Assert.Contains(grid.PersistentChildren, c => c.FormId == 0x000B0002);
        var moved = Assert.Single(container.PersistentChildren, c => c.FormId == MasterRefId);
        Assert.True(moved.Reparented);
    }

    [Fact]
    public void Orphan_Bucket_Actors_Rescued_And_Bucket_Removed()
    {
        var bucket = MakeOrphanBucketPlan(
            persistent: [OverrideChild("ACHR", MasterRefId), NewChild("ACHR", NewActorRefId)],
            temporary: [OverrideChild("ACHR", 0x000B0003)]);

        var cells = Apply(bucket);

        Assert.False(cells.ContainsKey(OrphanBucketId));
        var container = cells[ContainerCellId];
        Assert.Contains(container.PersistentChildren, c => c.FormId == MasterRefId && c.Reparented);
        Assert.Contains(container.PersistentChildren, c => c.FormId == NewActorRefId && c.Reparented);
        // Temporary-bucket actors are engine-immovable — they die with the bucket.
        Assert.DoesNotContain(container.PersistentChildren, c => c.FormId == 0x000B0003);
    }

    [Fact]
    public void Orphan_Bucket_Without_Container_Is_Still_Removed()
    {
        // A synthesized bucket cell (invented "[Virtual x,y]" EditorId) must never emit,
        // even when its worldspace has no master container to rescue children into.
        var bucket = MakeOrphanBucketPlan(
            persistent: [OverrideChild("ACHR", MasterRefId)],
            worldspaceId: 0x00DEAD00);

        var cells = Apply(bucket);

        Assert.False(cells.ContainsKey(OrphanBucketId));
        Assert.False(cells.ContainsKey(ContainerCellId));
    }

    [Fact]
    public void Duplicate_Captures_Of_Same_Actor_Dedup_In_Container()
    {
        // The runtime shares persistents into every loaded grid cell — two captures of the
        // same FormID must produce ONE container child.
        const uint otherGridId = 0x000E1949;
        var gridA = MakeCellPlan(GridCellId, MakeExteriorContext(GridCellId),
            [OverrideChild("ACHR", MasterRefId)]);
        var gridB = MakeCellPlan(otherGridId, MakeExteriorContext(otherGridId),
            [OverrideChild("ACHR", MasterRefId)]);

        var cells = Apply(gridA, gridB);

        Assert.Single(cells[ContainerCellId].PersistentChildren, c => c.FormId == MasterRefId);
    }

    [Fact]
    public void Master_Persistent_Actor_In_Temporary_Bucket_Moves()
    {
        // Runtime captures don't always preserve the persistent flag; when MASTER files the
        // actor persistent (header 0x400), the move applies from any bucket.
        var grid = MakeGridCellPlan() with
        {
            TemporaryChildren = [OverrideChild("ACHR", MasterRefId)],
        };

        var cells = Apply([grid], masterPersistentRefs: [MasterRefId]);

        Assert.Empty(cells[GridCellId].TemporaryChildren);
        var moved = Assert.Single(cells[ContainerCellId].PersistentChildren, c => c.FormId == MasterRefId);
        Assert.True(moved.Reparented);
    }

    [Fact]
    public void New_Worldspace_Actors_Route_To_Captured_Proto_Container()
    {
        // A proto worldspace (absent from master, e.g. TheStripWorld) routes its exterior
        // persistents into the CAPTURED proto persistent cell, not a master container.
        const uint protoWs = 0x0010B96F;
        const uint protoContainerId = 0x01000200;
        const uint protoGridId = 0x01000201;

        var container = MakeNewWorldspaceCellPlan(protoContainerId, protoWs, isPersistentCell: true);
        var grid = MakeNewWorldspaceCellPlan(protoGridId, protoWs, isPersistentCell: false) with
        {
            PersistentChildren = [OverrideChild("ACHR", MasterRefId)],
        };

        var cells = Apply(container, grid);

        Assert.Empty(cells[protoGridId].PersistentChildren);
        var moved = Assert.Single(cells[protoContainerId].PersistentChildren, c => c.FormId == MasterRefId);
        Assert.True(moved.Reparented);
    }

    [Fact]
    public void New_Worldspace_Without_Captured_Container_Synthesizes_One()
    {
        const uint protoWs = 0x0010B96F;
        const uint protoGridId = 0x01000201;
        var grid = MakeNewWorldspaceCellPlan(protoGridId, protoWs, isPersistentCell: false) with
        {
            PersistentChildren = [NewChild("ACHR", NewActorRefId)],
        };

        var cells = Apply([grid], allocator: new FormIdAllocator());

        Assert.Empty(cells[protoGridId].PersistentChildren);
        var synthesized = Assert.Single(cells.Values, plan =>
            plan.CellFormId != protoGridId
            && plan.ParentWorldspaceFormId == protoWs
            && plan.CellRecordPlan.Disposition == RecordDisposition.New);
        Assert.True(synthesized.CellRecordPlan.Model is CellRecord { IsPersistentCell: true, EditorId: null });
        Assert.Contains(synthesized.PersistentChildren, c => c.FormId == NewActorRefId);
    }

    [Fact]
    public void Enable_Parent_Target_New_Ref_Rescued_From_Temporary_Bucket()
    {
        // A street light gates on a proto enable marker (XESP). If the marker's cell dies,
        // the encoder strips the light's XESP and the gating is lost — the marker must be
        // rescued into the container from ANY bucket.
        const uint markerSourceId = 0x00FF1234;
        const uint markerEmittedId = 0x01002000;
        var marker = MakeChild("REFR", markerEmittedId, RecordDisposition.New) with
        {
            SourceFormId = markerSourceId,
            Model = new PlacedReference
            {
                FormId = markerSourceId, BaseFormId = 0x000A3001, RecordType = "REFR", IsPersistent = false
            },
        };
        var light = MakeChild("REFR", 0x01002001, RecordDisposition.New) with
        {
            Model = new PlacedReference
            {
                FormId = 0x01002001, BaseFormId = 0x000A3002, RecordType = "REFR",
                EnableParentFormId = markerSourceId
            },
        };
        var grid = MakeCellPlan(GridCellId, MakeExteriorContext(GridCellId), []) with
        {
            TemporaryChildren = [marker, light],
        };

        var cells = Apply(grid);

        // The marker moved (flagged Reparented); the light stays in place with its XESP.
        var moved = Assert.Single(cells[ContainerCellId].PersistentChildren, c => c.FormId == markerEmittedId);
        Assert.True(moved.Reparented);
        Assert.Contains(cells[GridCellId].TemporaryChildren, c => c.FormId == 0x01002001);
        Assert.DoesNotContain(cells[GridCellId].TemporaryChildren, c => c.FormId == markerEmittedId);
    }

    [Fact]
    public void Interior_Persistent_Children_Are_Untouched()
    {
        var interior = MakeCellPlan(
            InteriorCellId, MakeInteriorContext(InteriorCellId),
            [NewChild("ACHR", NewActorRefId)]);

        var cells = Apply(interior);

        Assert.Contains(cells[InteriorCellId].PersistentChildren, c => c.FormId == NewActorRefId);
        Assert.False(cells.ContainsKey(ContainerCellId));
    }

    [Fact]
    public void Moves_Append_To_Existing_Container_Plan()
    {
        var existingContainer = MakeCellPlan(
            ContainerCellId, MakeContainerContext(), [OverrideChild("REFR", MasterRefId)]);

        var cells = Apply(MakeGridCellPlan(NewChild("ACHR", NewActorRefId)), existingContainer);

        var container = cells[ContainerCellId];
        Assert.Contains(container.PersistentChildren, c => c.FormId == MasterRefId);
        Assert.Contains(container.PersistentChildren, c => c.FormId == NewActorRefId);
    }

    // ---- fixture plumbing ----------------------------------------------------------

    private static ImmutableDictionary<uint, CellPlan> Apply(params CellPlan[] plans) =>
        Apply(plans, masterPersistentRefs: null, allocator: null);

    private static ImmutableDictionary<uint, CellPlan> Apply(
        CellPlan[] plans,
        uint[]? masterPersistentRefs = null,
        FormIdAllocator? allocator = null)
    {
        var masterContexts = new Dictionary<uint, PcEsmCellContext>
        {
            [ContainerCellId] = MakeContainerContext(),
            [GridCellId] = MakeExteriorContext(GridCellId),
            [InteriorCellId] = MakeInteriorContext(InteriorCellId),
        };
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [ContainerCellId] = MakeMasterRecord("CELL", ContainerCellId),
            [GridCellId] = MakeMasterRecord("CELL", GridCellId),
            [InteriorCellId] = MakeMasterRecord("CELL", InteriorCellId),
        };
        foreach (var refId in masterPersistentRefs ?? [])
        {
            masterByFormId[refId] = MakeMasterRecord("ACHR", refId, flags: 0x00000400u);
        }

        var cells = ImmutableDictionary.CreateBuilder<uint, CellPlan>();
        foreach (var plan in plans)
        {
            cells.Add(plan.CellFormId, plan);
        }

        return PersistentCellReparenting.Apply(cells.ToImmutable(), masterContexts, masterByFormId, allocator);
    }

    private static CellPlan MakeGridCellPlan(params RecordPlan[] persistentChildren) =>
        MakeCellPlan(GridCellId, MakeExteriorContext(GridCellId), persistentChildren);

    /// <summary>
    ///     Parse-side orphan bucket: a synthesized exterior cell (IsVirtual model, no master
    ///     anchor, null block labels — the shape SynthesizeContext produces).
    /// </summary>
    private static CellPlan MakeOrphanBucketPlan(
        RecordPlan[] persistent,
        RecordPlan[]? temporary = null,
        uint worldspaceId = WorldspaceId)
    {
        var context = new PcEsmCellContext
        {
            CellFormId = OrphanBucketId,
            IsInterior = false,
            WorldspaceFormId = worldspaceId,
            BlockGroupType = 4,
            SubblockGroupType = 5,
            BlockLabel = null,
            SubblockLabel = null,
        };
        return new CellPlan
        {
            CellFormId = OrphanBucketId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.New,
                FormId = OrphanBucketId,
                Model = new CellRecord
                {
                    FormId = OrphanBucketId, EditorId = "[Virtual 1,2 Test]", IsVirtual = true
                },
                Master = null,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = context,
            PersistentChildren = [.. persistent],
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = temporary is null ? ImmutableArray<RecordPlan>.Empty : [.. temporary],
            ParentWorldspaceFormId = worldspaceId,
            Mode = CellMergeMode.LoadedReplacement,
        };
    }

    private static CellPlan MakeCellPlan(
        uint cellFormId, PcEsmCellContext context, RecordPlan[] persistentChildren) => new()
    {
        CellFormId = cellFormId,
        CellRecordPlan = new RecordPlan
        {
            Type = "CELL",
            Disposition = RecordDisposition.Override,
            FormId = cellFormId,
            Model = new CellRecord { FormId = cellFormId },
            Master = MakeMasterRecord("CELL", cellFormId),
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        },
        Context = context,
        PersistentChildren = [.. persistentChildren],
        VwdChildren = ImmutableArray<RecordPlan>.Empty,
        TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
        ParentWorldspaceFormId = context.WorldspaceFormId,
        Mode = CellMergeMode.PersistentOnly,
    };

    private static RecordPlan NewChild(string type, uint formId) =>
        MakeChild(type, formId, RecordDisposition.New);

    private static RecordPlan OverrideChild(string type, uint formId) =>
        MakeChild(type, formId, RecordDisposition.Override);

    private static RecordPlan MakeChild(string type, uint formId, RecordDisposition disposition) => new()
    {
        Type = type,
        Disposition = disposition,
        FormId = formId,
        Model = new PlacedReference
        {
            FormId = formId, BaseFormId = 0x000A3001, RecordType = type, IsPersistent = true
        },
        References = ImmutableArray<ResolvedRef>.Empty,
        ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
        Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
    };

    private static RecordPlan NewMarker(string type, uint formId) => MakeMarker(type, formId, RecordDisposition.New);

    private static RecordPlan OverrideMarker(string type, uint formId) => MakeMarker(type, formId, RecordDisposition.Override);

    private static RecordPlan MakeMarker(string type, uint formId, RecordDisposition disposition) => new()
    {
        Type = type,
        Disposition = disposition,
        FormId = formId,
        Model = new PlacedReference
        {
            FormId = formId, BaseFormId = 0x00000010, RecordType = type, IsMapMarker = true
        },
        References = ImmutableArray<ResolvedRef>.Empty,
        ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
        Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
    };

    private static PcEsmCellContext MakeContainerContext() => new()
    {
        CellFormId = ContainerCellId,
        IsInterior = false,
        WorldspaceFormId = WorldspaceId,
        BlockGroupType = 0,
        SubblockGroupType = 0,
        BlockLabel = null,
        SubblockLabel = null,
    };

    private static PcEsmCellContext MakeExteriorContext(uint cellFormId) => new()
    {
        CellFormId = cellFormId,
        IsInterior = false,
        WorldspaceFormId = WorldspaceId,
        BlockGroupType = 4,
        SubblockGroupType = 5,
        BlockLabel = [0, 0, 0, 0],
        SubblockLabel = [0, 0, 0, 0],
    };

    private static PcEsmCellContext MakeInteriorContext(uint cellFormId) => new()
    {
        CellFormId = cellFormId,
        IsInterior = true,
        BlockGroupType = 2,
        SubblockGroupType = 3,
        BlockLabel = [1, 0, 0, 0],
        SubblockLabel = [2, 0, 0, 0],
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

    /// <summary>Cell plan of a proto (new) worldspace: no master anchor, synthesized-shape context.</summary>
    private static CellPlan MakeNewWorldspaceCellPlan(uint cellFormId, uint worldspaceId, bool isPersistentCell)
    {
        var context = new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = false,
            WorldspaceFormId = worldspaceId,
            BlockGroupType = 4,
            SubblockGroupType = 5,
            BlockLabel = null,
            SubblockLabel = null,
        };
        return new CellPlan
        {
            CellFormId = cellFormId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.New,
                FormId = cellFormId,
                Model = new CellRecord
                {
                    FormId = cellFormId, WorldspaceFormId = worldspaceId, IsPersistentCell = isPersistentCell
                },
                Master = null,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = context,
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
            ParentWorldspaceFormId = worldspaceId,
            Mode = CellMergeMode.LoadedReplacement,
        };
    }
}
