using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Re-parents placed refs captured in GRUPs the FNV loader never reads into the parent
///     worldspace's persistent-container cell. The runtime assigns loaded persistent refs to
///     their spatial grid cell, so DMP captures carry grid-cell parents — but the FILE format
///     keeps every exterior persistent ref in the worldspace's persistent dummy cell, and the
///     loader never reads Persistent-Children GRUPs under exterior grid cells. Emitting
///     records there produces content the engine never loads (in-game proven twice: NEW refs
///     via <c>prid</c> "not found" — Ulysses at Wolfhorn — and Override map
///     markers whose renames/moves never applied while filed in grid cells).
/// </summary>
/// <remarks>
///     Move rules, per exterior non-container cell (master grid cells, new-worldspace proto
///     grid cells, and parse-side orphan buckets alike):
///     <list type="number">
///         <item>Map markers (any bucket, NEW or Override) always move — the engine only
///         shows markers that live in the worldspace persistent cell.</item>
///         <item>Persistent-bucket ACHR/ACRE move regardless of disposition, and Override
///         ACHR/ACRE whose MASTER record carries the persistent flag move from any bucket
///         (runtime captures don't always preserve the flag). Moving applies the captured
///         placement — policy: an actor the DMP places elsewhere is MOVED via
///         a container override, never duplicated with a NEW record, and actors absent from
///         the DMP are left alone.</item>
///         <item>NEW persistent-bucket REFRs move. Override non-marker REFRs
///         stay — moving world-object runtime state is a separate policy question.</item>
///         <item>Parse-side orphan buckets (cells flagged <see cref="CellRecord.IsVirtual" />,
///         synthesized for refs whose runtime parent cell couldn't be resolved) get the same
///         rescue, then the bucket plan itself is REMOVED — a synthesized cell with an
///         invented EditorId must never reach the output ESM.</item>
///     </list>
///     Container resolution per worldspace: the master's persistent cell; else the captured
///     PROTO persistent cell (new worldspaces like TheStripWorld don't exist in master — the
///     DMP usually carries the proto's own grid-less persistent CELL); else a minimal NEW
///     container cell is synthesized, matching what the CK does when a worldspace gains its
///     first persistent ref. Master TEMPORARY actors are never moved: an ESM plugin
///     re-emitting one leaves the form uninitialized at attach (see
///     <c>PlannedPlacedRefEncoder</c>); the verdict pass suppresses those overrides where
///     they stand. Non-marker moves are marked <see cref="RecordPlan.Reparented" /> so the
///     verdict pass applies the override (and the proto's enable-state) instead of
///     sparse-preserving master / flagging a parent-cell mismatch.
/// </remarks>
public static class PersistentCellReparenting
{
    /// <summary>Record-header persistent flag (0x400).</summary>
    private const uint PersistentHeaderFlag = 0x00000400u;

    public static ImmutableDictionary<uint, CellPlan> Apply(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        FormIdAllocator? allocator = null)
    {
        if (cells.IsEmpty)
        {
            return cells;
        }

        // Worldspace → master persistent-container cell.
        var masterContainers = new Dictionary<uint, uint>();
        foreach (var (cellFormId, context) in masterContexts)
        {
            if (context.IsPersistentCellContainer && context.WorldspaceFormId is { } ws)
            {
                masterContainers.TryAdd(ws, cellFormId);
            }
        }

        // Worldspace → captured PROTO persistent-container cell (new worldspaces only).
        // Require the parse-side IsPersistentCell flag as the authority. Synthesized
        // contexts now distinguish grid cells from containers, but the semantic flag also
        // protects this selection if a future input carries unusual grouping metadata.
        var protoContainers = new Dictionary<uint, uint>();
        foreach (var (cellFormId, plan) in cells)
        {
            if (!plan.Context.IsInterior
                && plan.Context.WorldspaceFormId is { } ws
                && !masterContainers.ContainsKey(ws)
                && plan.CellRecordPlan.Model is CellRecord { IsPersistentCell: true, IsVirtual: false }
                && (!protoContainers.TryGetValue(ws, out var existing) || cellFormId < existing))
            {
                protoContainers[ws] = cellFormId;
            }
        }

        uint? ContainerFor(uint ws)
        {
            if (masterContainers.TryGetValue(ws, out var master))
            {
                return master;
            }

            return protoContainers.TryGetValue(ws, out var proto) ? proto : (uint?)null;
        }

        // Enable-parent targets: refs other captured refs gate on via XESP. If the parent
        // never emits, the encoder strips the child's XESP and the gating is lost (street
        // lights always on in TheStripWorld). Parents captured anywhere (including
        // cells that later die at the writer, e.g. cut cells with a missing parent
        // worldspace) get the same container rescue as markers.
        var enableParentTargets = new HashSet<uint>();
        foreach (var plan in cells.Values)
        {
            CollectEnableParents(plan.PersistentChildren, enableParentTargets);
            CollectEnableParents(plan.TemporaryChildren, enableParentTargets);
            CollectEnableParents(plan.VwdChildren, enableParentTargets);
        }

        var movesByWorldspace = new Dictionary<uint, List<RecordPlan>>();
        var orphanBuckets = new List<uint>();
        var result = cells.ToBuilder();

        foreach (var (cellFormId, cellPlan) in cells)
        {
            // Orphan buckets are collected for removal up front — they must disappear even
            // when no container can be resolved to rescue their children into.
            var isOrphanBucket = cellPlan.CellRecordPlan.Model is CellRecord { IsVirtual: true };
            if (isOrphanBucket)
            {
                orphanBuckets.Add(cellFormId);
            }

            if (cellPlan.Context.IsInterior
                || cellPlan.Context.WorldspaceFormId is not { } cellWs
                || ContainerFor(cellWs) == cellFormId
                || (!isOrphanBucket
                    && cellPlan.CellRecordPlan.Master is null
                    && cellPlan.CellRecordPlan.Model is CellRecord { IsPersistentCell: true }))
            {
                continue; // Interiors and the worldspace containers themselves donate nothing.
            }

            bool MasterFilesPersistent(RecordPlan child) =>
                child.Disposition == RecordDisposition.Override
                && masterRecordsByFormId.TryGetValue(child.FormId, out var masterChild)
                && (masterChild.Header.Flags & PersistentHeaderFlag) != 0;

            // Enable parents rescue as NEW refs from any bucket, or as Overrides only when
            // MASTER files them persistent (a master-temporary REFR override routed into the
            // container would violate the GRUP/flag agreement).
            bool IsRescuableEnableParent(RecordPlan child) =>
                child.Type == "REFR"
                && enableParentTargets.Contains(child.SourceFormId ?? child.FormId)
                && (child.Disposition == RecordDisposition.New || MasterFilesPersistent(child));

            bool ShouldMove(RecordPlan child, bool fromPersistentBucket) =>
                child.Disposition != RecordDisposition.Skip
                && (child.Model is PlacedReference { IsMapMarker: true }
                    || (fromPersistentBucket
                        && (child.Type is "ACHR" or "ACRE"
                            || (child.Disposition == RecordDisposition.New && child.Type == "REFR")))
                    || (child.Type is "ACHR" or "ACRE" && MasterFilesPersistent(child))
                    || IsRescuableEnableParent(child));

            var movable = new List<RecordPlan>();
            var persistent = SplitBucket(cellPlan.PersistentChildren, true, ShouldMove, movable);
            var temporary = SplitBucket(cellPlan.TemporaryChildren, false, ShouldMove, movable);
            var vwd = SplitBucket(cellPlan.VwdChildren, false, ShouldMove, movable);
            if (movable.Count == 0)
            {
                continue;
            }

            result[cellFormId] = cellPlan with
            {
                PersistentChildren = persistent,
                TemporaryChildren = temporary,
                VwdChildren = vwd,
            };

            if (!movesByWorldspace.TryGetValue(cellWs, out var list))
            {
                list = [];
                movesByWorldspace[cellWs] = list;
            }

            // Non-marker moves are deliberate reparents — flagged so the verdict pass
            // applies the captured override. Markers keep their dedicated verdict rules
            // (differs-from-master gate + mismatch bypass, v113 behavior).
            foreach (var child in movable)
            {
                list.Add(child.Model is PlacedReference { IsMapMarker: true }
                    ? child
                    : child with { Reparented = true });
            }
        }

        foreach (var (ws, children) in movesByWorldspace)
        {
            var containerFormId = ContainerFor(ws);
            if (containerFormId is null && allocator is null)
            {
                continue; // No container and no allocator to synthesize one — leave in place.
            }

            containerFormId ??= allocator!.Allocate();
            result.TryGetValue(containerFormId.Value, out var existing);

            // The runtime shares persistent refs into every loaded grid cell, so the same
            // FormID can be captured in several source cells — the container hosts one copy.
            var seen = new HashSet<uint>();
            if (existing is not null)
            {
                foreach (var child in existing.PersistentChildren)
                {
                    seen.Add(child.FormId);
                }
            }

            var unique = new List<RecordPlan>(children.Count);
            foreach (var child in children)
            {
                if (seen.Add(child.FormId))
                {
                    unique.Add(child);
                }
            }

            if (unique.Count == 0)
            {
                continue;
            }

            if (existing is not null)
            {
                result[containerFormId.Value] = existing with
                {
                    PersistentChildren = existing.PersistentChildren.AddRange(unique),
                };
                continue;
            }

            result[containerFormId.Value] =
                masterRecordsByFormId.TryGetValue(containerFormId.Value, out var masterCell)
                && masterContexts.TryGetValue(containerFormId.Value, out var containerContext)
                    ? BuildMasterContainerOverridePlan(containerFormId.Value, masterCell, containerContext, unique)
                    : BuildNewContainerPlan(containerFormId.Value, ws, unique);
        }

        // Orphan buckets never emit: any child worth keeping moved above; the bucket cell is
        // a parse-time synthesis whose invented EditorId ("[Virtual x,y]" / "[Unresolved …]")
        // must never reach the output ESM.
        foreach (var formId in orphanBuckets)
        {
            result.Remove(formId);
        }

        return result.ToImmutable();
    }

    /// <summary>
    ///     Override plan for a master worldspace's persistent-container cell that had no
    ///     captured plan of its own — hosts only the moved children (v113 shape).
    /// </summary>
    private static CellPlan BuildMasterContainerOverridePlan(
        uint containerFormId,
        ParsedMainRecord masterCell,
        PcEsmCellContext containerContext,
        List<RecordPlan> children) => new()
    {
        CellFormId = containerFormId,
        CellRecordPlan = new RecordPlan
        {
            Type = "CELL",
            Disposition = RecordDisposition.Override,
            FormId = containerFormId,
            Model = null,
            Master = masterCell,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance
            {
                PolicyId = "PersistentCellReparenting",
                Reason = "Container override created to host re-parented exterior persistent refs + map markers.",
            },
        },
        Context = containerContext,
        PersistentChildren = [.. children],
        VwdChildren = ImmutableArray<RecordPlan>.Empty,
        TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
        ParentWorldspaceFormId = containerContext.WorldspaceFormId,
        // Persistent-only content by construction; carry-forward skips master's own
        // GroupType-8 children, so this override never bloats with master persistents.
        Mode = CellMergeMode.PersistentOnly,
        DropRenderCullingMarkers = false,
    };

    /// <summary>
    ///     Minimal NEW persistent-container cell for a worldspace with neither a master nor
    ///     a captured proto container (matches the CK's behavior when a worldspace gains its
    ///     first persistent ref): no EditorId, exterior, no water claim.
    /// </summary>
    private static CellPlan BuildNewContainerPlan(
        uint containerFormId,
        uint worldspaceFormId,
        List<RecordPlan> children) => new()
    {
        CellFormId = containerFormId,
        CellRecordPlan = new RecordPlan
        {
            Type = "CELL",
            Disposition = RecordDisposition.New,
            FormId = containerFormId,
            Model = new CellRecord
            {
                FormId = containerFormId,
                WorldspaceFormId = worldspaceFormId,
                IsPersistentCell = true,
            },
            Master = null,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance
            {
                PolicyId = "PersistentCellReparenting",
                Reason = "New persistent-container cell synthesized for a worldspace without one.",
            },
        },
        Context = new PcEsmCellContext
        {
            CellFormId = containerFormId,
            IsInterior = false,
            WorldspaceFormId = worldspaceFormId,
            BlockGroupType = 0,
            SubblockGroupType = 0,
            BlockLabel = null,
            SubblockLabel = null,
        },
        PersistentChildren = [.. children],
        VwdChildren = ImmutableArray<RecordPlan>.Empty,
        TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
        ParentWorldspaceFormId = worldspaceFormId,
        Mode = CellMergeMode.LoadedReplacement,
        DropRenderCullingMarkers = false,
    };

    private static void CollectEnableParents(
        ImmutableArray<RecordPlan> bucket, HashSet<uint> targets)
    {
        foreach (var child in bucket)
        {
            if (child.Model is PlacedReference { EnableParentFormId: > 0 } placed)
            {
                targets.Add(placed.EnableParentFormId!.Value);
            }
        }
    }

    /// <summary>
    ///     Partition a child bucket into the refs that stay (returned) and the refs that move
    ///     to the worldspace container (appended to <paramref name="movable" />).
    /// </summary>
    private static ImmutableArray<RecordPlan> SplitBucket(
        ImmutableArray<RecordPlan> bucket,
        bool fromPersistentBucket,
        Func<RecordPlan, bool, bool> shouldMove,
        List<RecordPlan> movable)
    {
        if (bucket.IsEmpty)
        {
            return bucket;
        }

        var stay = ImmutableArray.CreateBuilder<RecordPlan>(bucket.Length);
        foreach (var child in bucket)
        {
            if (shouldMove(child, fromPersistentBucket))
            {
                movable.Add(child);
            }
            else
            {
                stay.Add(child);
            }
        }

        return stay.Count == bucket.Length ? bucket : stay.ToImmutable();
    }
}
