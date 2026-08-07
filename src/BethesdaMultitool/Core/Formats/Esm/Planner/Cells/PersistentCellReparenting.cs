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
        FormIdAllocator? allocator = null,
        IReadOnlyDictionary<uint, uint>? masterRefToCell = null)
    {
        return Apply(cells, masterContexts, masterRecordsByFormId, out _, allocator, masterRefToCell);
    }

    public static ImmutableDictionary<uint, CellPlan> Apply(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        out ImmutableArray<PlanDiagnostic> diagnostics,
        FormIdAllocator? allocator = null,
        IReadOnlyDictionary<uint, uint>? masterRefToCell = null)
    {
        var diagnosticsBuilder = ImmutableArray.CreateBuilder<PlanDiagnostic>();
        diagnostics = ImmutableArray<PlanDiagnostic>.Empty;
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

            // Master-home gate, USER RULING 2026-08-05 (playtest finding 2, option D). The
            // v141 gate required master to file the ref in a non-interior cell OF THE TARGET
            // WORLDSPACE — but the proto predates retail's worldspace re-partition
            // (TheStripWorldNew→TheStripWorld, WastelandNVmini→WastelandNV, …), so that test
            // NEVER held for a proto capture: it blocked all 163 candidates, not the 3 the
            // v141 note counted. Current rules:
            //   1. NEW refs are unconstrained (no master home).
            //   2. Plugin-NEW worldspace containers (master authored no cells for the target
            //      worldspace): allowed unconditionally — no authored placement conflicts.
            //   3. MASTER containers: allowed when master homes the ref in any NON-interior
            //      cell, regardless of worldspace. Interior-homed refs stay blocked — that is
            //      the cow-crash class (xex44.v140, cow TheStripWorldNew → ACCESS_VIOLATION:
            //      ACHR 0x00116834 + ACRE 0x0013BB19/0x0013BB1A, master interiors, pulled
            //      into master container 0x0013B310 while the engine still held the interior
            //      copies).
            var targetIsProtoContainer = !masterContainers.ContainsKey(cellWs);

            bool MasterHomeAllowsMove(RecordPlan child)
            {
                if (child.Disposition != RecordDisposition.Override)
                {
                    return true;
                }

                if (masterRefToCell is null
                    || !masterRefToCell.TryGetValue(child.FormId, out var masterCellFormId))
                {
                    return true; // No master parentage known — leave prior behavior intact.
                }

                if (masterCellFormId == cellFormId || masterCellFormId == ContainerFor(cellWs))
                {
                    return true; // Already at home, or already the container we're moving into.
                }

                if (targetIsProtoContainer)
                {
                    return true;
                }

                return masterContexts.TryGetValue(masterCellFormId, out var masterCellContext)
                       && !masterCellContext.IsInterior;
            }

            // Enable parents rescue as NEW refs from any bucket, or as Overrides only when
            // MASTER files them persistent (a master-temporary REFR override routed into the
            // container would violate the GRUP/flag agreement).
            bool IsRescuableEnableParent(RecordPlan child) =>
                child.Type == "REFR"
                && enableParentTargets.Contains(child.SourceFormId ?? child.FormId)
                && (child.Disposition == RecordDisposition.New || MasterFilesPersistent(child));

            bool ShouldMove(RecordPlan child, bool fromPersistentBucket)
            {
                var qualifies = child.Disposition != RecordDisposition.Skip
                    && (child.Model is PlacedReference { IsMapMarker: true }
                        || (fromPersistentBucket
                            && (child.Type is "ACHR" or "ACRE"
                                || (child.Disposition == RecordDisposition.New && child.Type == "REFR")))
                        || (child.Type is "ACHR" or "ACRE" && MasterFilesPersistent(child))
                        || IsRescuableEnableParent(child));
                if (!qualifies)
                {
                    return false;
                }

                if (MasterHomeAllowsMove(child))
                {
                    return true;
                }

                // A gate refusal is never silent: the 163 refs v141 dropped surfaced only via
                // an in-game playtest ("where did Emily Ortal go?"). Every refusal now names
                // the ref, its master home, and why.
                masterRefToCell!.TryGetValue(child.FormId, out var masterHome);
                diagnosticsBuilder.Add(new PlanDiagnostic
                {
                    Kind = PlanDiagnosticKind.Warning,
                    Phase = "Cells",
                    Code = "refr.rehome-blocked",
                    RecordType = child.Type,
                    FormId = child.FormId,
                    Message = $"{child.Type} 0x{child.FormId:X8} qualifies for container re-homing in " +
                        $"worldspace 0x{cellWs:X8} but master homes it in interior cell 0x{masterHome:X8} " +
                        "— blocked (interior-homed master override; the cow-crash class).",
                    Metadata = new Dictionary<string, string?>
                    {
                        ["worldspace"] = $"0x{cellWs:X8}",
                        ["masterHomeCell"] = $"0x{masterHome:X8}",
                        ["capturedCell"] = $"0x{cellFormId:X8}",
                    },
                });
                return false;
            }

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
            // ProtoWorldspaceRehome additionally marks moves into a plugin-new worldspace's
            // container, where the verdict pass applies the CAPTURED enable-state (USER
            // RULING 2026-08-05 — master authored no cells there, so there is no authored
            // enable-state to preserve).
            foreach (var child in movable)
            {
                list.Add(child.Model is PlacedReference { IsMapMarker: true }
                    ? child
                    : child with { Reparented = true, ProtoWorldspaceRehome = targetIsProtoContainer });
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
                    ? PersistentContainerPlans.BuildMasterContainerOverridePlan(
                        containerFormId.Value, masterCell, containerContext, unique)
                    : PersistentContainerPlans.BuildNewContainerPlan(containerFormId.Value, ws, unique);
        }

        // Orphan buckets never emit: any child worth keeping moved above; the bucket cell is
        // a parse-time synthesis whose invented EditorId ("[Virtual x,y]" / "[Unresolved …]")
        // must never reach the output ESM. Children that did NOT move die with the bucket —
        // each death is named (these removals were fully silent until 2026-08-05).
        foreach (var formId in orphanBuckets)
        {
            if (result.TryGetValue(formId, out var bucket))
            {
                foreach (var child in bucket.PersistentChildren
                             .Concat(bucket.TemporaryChildren)
                             .Concat(bucket.VwdChildren))
                {
                    if (child.Disposition == RecordDisposition.Skip)
                    {
                        continue;
                    }

                    diagnosticsBuilder.Add(new PlanDiagnostic
                    {
                        Kind = PlanDiagnosticKind.Warning,
                        Phase = "Cells",
                        Code = "refr.orphan-bucket-dropped",
                        RecordType = child.Type,
                        FormId = child.FormId,
                        Message = $"{child.Type} 0x{child.FormId:X8} was captured in unresolved-parent " +
                            $"bucket 0x{formId:X8} and did not qualify for container rescue — dropped " +
                            "with the bucket.",
                        Metadata = new Dictionary<string, string?>
                        {
                            ["bucket"] = $"0x{formId:X8}",
                            ["disposition"] = child.Disposition.ToString(),
                        },
                    });
                }
            }

            result.Remove(formId);
        }

        diagnostics = diagnosticsBuilder.ToImmutable();
        return result.ToImmutable();
    }

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
