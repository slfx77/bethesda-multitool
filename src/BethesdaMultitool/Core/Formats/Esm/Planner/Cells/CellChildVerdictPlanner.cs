using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Inputs for the plan-time per-ref verdict pass: the master index (child locations,
///     ref→cell parents, EditorID stems), the DMP base-type map, and the option switches
///     the decision chain consults. Deliberately NOT the whole options bag — the planner
///     stays decoupled from the writer's configuration surface.
/// </summary>
public sealed record CellVerdictInputs
{
    public required MasterRecordIndex MasterIndex { get; init; }
    public IReadOnlyDictionary<uint, string>? DmpBaseTypes { get; init; }
    public bool RecoverLeveledSpawnActors { get; init; }
    public bool EnableRefrBaseEditorIdRemap { get; init; }
    public bool DiagnosticSkipCellNewRefs { get; init; }
    public bool DiagnosticSkipCellNavm { get; init; }
}

/// <summary>
///     Phase F for cells: settle every placed-ref (REFR/ACHR/ACRE) emit-or-drop verdict at
///     plan time — base remap/validation, leveled-spawn recovery, EditorID-stem rescue,
///     marker drops, sparse-cell preservation, parent-cell guard, and child-GRUP routing —
///     populating <see cref="CellPlan.RefDecisions" />. Runs AFTER top-level FormID
///     allocation (verdicts need the complete source→emitted map), which is why this is a
///     post-pass in <c>EsmPlanner.Build</c> rather than part of <c>CellSectionPlanner</c>.
///     The writer then serializes verdicts instead of re-deriving them.
/// </summary>
public static class CellChildVerdictPlanner
{
    /// <summary>
    ///     Rewrite each planned cell with its children's verdicts. A missing
    ///     <see cref="CellPlan.Mode" /> is an incomplete plan and fails immediately;
    ///     the writer has no fallback decision path.
    /// </summary>
    public static ImmutableDictionary<uint, CellPlan> Apply(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        IReadOnlySet<uint> emittedFormIds,
        CellVerdictInputs inputs)
    {
        if (cells.IsEmpty)
        {
            return cells;
        }

        // Phase 1 (sorted for deterministic phase-2 claiming): settle every verdict except
        // cross-cell moves; own-cell Override emits register their FormIDs so a home-cell
        // capture always beats a neighbor-cell capture of the same ref.
        var result = cells.ToBuilder();
        var verdictBuilders = new Dictionary<uint, ImmutableDictionary<uint, PlacedRefDecision>.Builder>();
        var emittedOverrideFormIds = new HashSet<uint>();
        var moveCandidates = new List<CrossCellMoveCandidate>();
        foreach (var (cellFormId, cellPlan) in cells.OrderBy(static kv => kv.Key))
        {
            if (cellPlan.Mode is null)
            {
                throw new InvalidOperationException(
                    $"CELL 0x{cellFormId:X8} reached verdict planning without a settled merge mode.");
            }

            var verdicts = ImmutableDictionary.CreateBuilder<uint, PlacedRefDecision>();
            DecideBucket(cellPlan.PersistentChildren, 8, cellPlan, masterByFormId,
                sourceToEmitted, emittedFormIds, inputs, verdicts, emittedOverrideFormIds, moveCandidates);
            DecideBucket(cellPlan.VwdChildren, 10, cellPlan, masterByFormId,
                sourceToEmitted, emittedFormIds, inputs, verdicts, emittedOverrideFormIds, moveCandidates);
            DecideBucket(cellPlan.TemporaryChildren, 9, cellPlan, masterByFormId,
                sourceToEmitted, emittedFormIds, inputs, verdicts, emittedOverrideFormIds, moveCandidates);
            verdictBuilders[cellFormId] = verdicts;
        }

        // Phase 2: cross-cell moves (USER POLICY 2026-07-20, Goodsprings cemetery: the
        // capture's parent cell is the proto-authored placement — "true to xex.dmp").
        // First claim per FormID wins; the rest are duplicate captures of the same ref.
        foreach (var candidate in moveCandidates
                     .OrderBy(static c => c.CellFormId).ThenBy(static c => c.ChildFormId))
        {
            var verdicts = verdictBuilders[candidate.CellFormId];
            if (!emittedOverrideFormIds.Add(candidate.ChildFormId))
            {
                verdicts[candidate.ChildFormId] = Drop("refr.cross-cell-duplicate");
                continue;
            }

            verdicts[candidate.ChildFormId] = new PlacedRefDecision
            {
                Verdict = PlacedRefEmitVerdict.Emit,
                TargetGroupType = candidate.TargetGroupType,
                MarksMasterCovered = true,
                AuxStatCode = "refr.cross-cell-move",
                OverrideInitiallyDisabled = candidate.OverrideInitiallyDisabled
            };
        }

        foreach (var (cellFormId, verdicts) in verdictBuilders)
        {
            var withVerdicts = cells[cellFormId] with { RefDecisions = verdicts.ToImmutable() };
            result[cellFormId] = PlanCellGates(withVerdicts, inputs);
        }

        return result.ToImmutable();
    }

    /// <summary>
    ///     Settle the cell-level emission gates from the per-ref verdicts, mirroring the
    ///     writer's post-encode sequence exactly: (1) a master-cell bundle whose genuine
    ///     children are navmeshes only discards the NAVM prefix; (2) a master-anchored
    ///     bundle with no surviving genuine children is an ITM override and is suppressed;
    ///     (3) a master INTERIOR with no NEW children is a visited-but-unchanged base cell
    ///     and is suppressed (the fragile master seek/scan attach path). Emit-verdict counts
    ///     stand in for encode success — serialization of a planned emit never fails in
    ///     practice, and the byte-parity gate would surface it if it ever did.
    /// </summary>
    private static CellPlan PlanCellGates(CellPlan cellPlan, CellVerdictInputs inputs)
    {
        var isMasterAnchored = cellPlan.CellRecordPlan.Master is not null;
        var (emitRefs, emitNewRefs) = CountEmittedRefs(cellPlan);
        var plannedNavms = inputs.DiagnosticSkipCellNavm
            ? 0
            : cellPlan.TemporaryChildren.Count(c =>
                c.Type == "NAVM"
                && c.Disposition == RecordDisposition.New
                && c.Model is NavMeshRecord);

        var genuine = emitRefs + plannedNavms;
        var navmOnly = isMasterAnchored && genuine > 0 && genuine == plannedNavms;
        if (navmOnly)
        {
            genuine = 0;
        }

        // Planned LAND is genuine content for the ITM gate (legacy hasCapturedTerrain
        // parity: a master cell whose only change is terrain still emits), but it never
        // enters the genuine/navm counts — those gates reason about placed children only.
        var hasPlannedLand =
            cellPlan.TemporaryChildren.Any(static c => c.Type == "LAND" && c.Model is CellLandDecision);

        var (emits, suppressReason) = (true, (string?)null);
        if (isMasterAnchored && genuine == 0 && !hasPlannedLand)
        {
            (emits, suppressReason) = (false, "cell.itm-override-suppressed");
        }
        else if (isMasterAnchored && cellPlan.Context.IsInterior && emitNewRefs == 0)
        {
            (emits, suppressReason) = (false, "cell.interior-no-new-content-suppressed");
        }

        return cellPlan with
        {
            Emits = emits,
            SuppressReason = suppressReason,
            NavmOnlySuppressed = navmOnly
        };
    }

    /// <summary>
    ///     Per-INSTANCE emit counts (a duplicated FormID within a cell encodes once per
    ///     child in the writer, so counting unique verdicts would under-count).
    /// </summary>
    private static (int EmitRefs, int EmitNewRefs) CountEmittedRefs(CellPlan cellPlan)
    {
        var emitRefs = 0;
        var emitNewRefs = 0;
        foreach (var bucket in new[]
                 {
                     cellPlan.PersistentChildren, cellPlan.VwdChildren, cellPlan.TemporaryChildren
                 })
        {
            foreach (var child in bucket)
            {
                if (child.Type is not ("REFR" or "ACHR" or "ACRE" or "PGRE")
                    || !cellPlan.RefDecisions.TryGetValue(child.FormId, out var verdict)
                    || verdict.Verdict != PlacedRefEmitVerdict.Emit)
                {
                    continue;
                }

                emitRefs++;
                if (child.Disposition == RecordDisposition.New)
                {
                    emitNewRefs++;
                }
            }
        }

        return (emitRefs, emitNewRefs);
    }

    private static void DecideBucket(
        ImmutableArray<RecordPlan> children,
        int plannedGroupType,
        CellPlan cellPlan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        IReadOnlySet<uint> emittedFormIds,
        CellVerdictInputs inputs,
        ImmutableDictionary<uint, PlacedRefDecision>.Builder verdicts,
        HashSet<uint> emittedOverrideFormIds,
        List<CrossCellMoveCandidate> moveCandidates)
    {
        foreach (var child in children)
        {
            if (child.Type is not ("REFR" or "ACHR" or "ACRE" or "PGRE")
                || child.Model is not PlacedReference placed)
            {
                continue; // NAVM etc. are not placed refs; models other than PlacedReference
                // never encode — the writer handles both without a verdict.
            }

            var verdict = Decide(
                child, placed, plannedGroupType, cellPlan, masterByFormId,
                sourceToEmitted, emittedFormIds, inputs, moveCandidates);
            if (verdict is null)
            {
                continue; // Deferred cross-cell move; phase 2 fills the verdict.
            }

            verdicts[child.FormId] = verdict;
            if (child.Disposition == RecordDisposition.Override
                && verdict.Verdict == PlacedRefEmitVerdict.Emit)
            {
                emittedOverrideFormIds.Add(child.FormId);
            }
        }
    }

    private static PlacedRefDecision? Decide(
        RecordPlan child,
        PlacedReference placed,
        int plannedGroupType,
        CellPlan cellPlan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        IReadOnlySet<uint> emittedFormIds,
        CellVerdictInputs inputs,
        List<CrossCellMoveCandidate> moveCandidates)
    {
        if (RuntimeStateRecordPolicy.IsRuntimeStateFormId(placed.FormId))
        {
            return Drop("runtime-state.skip-ref");
        }

        if (child.Disposition == RecordDisposition.New && inputs.DiagnosticSkipCellNewRefs)
        {
            return Drop("refr.diagnostic-skip-new");
        }

        return child.Disposition switch
        {
            RecordDisposition.New => DecideNew(
                child, placed, plannedGroupType, cellPlan, masterByFormId,
                sourceToEmitted, emittedFormIds, inputs),
            RecordDisposition.Override => DecideOverride(
                child, placed, plannedGroupType, cellPlan, masterByFormId, inputs, moveCandidates),
            // Skip = pre-settled drop; its Provenance names the policy, so no stats reason.
            RecordDisposition.Skip => new PlacedRefDecision { Verdict = PlacedRefEmitVerdict.Drop },
            // Nothing routes any other disposition here today — name the drop, don't hide it.
            _ => Drop("refr.verdict-unhandled-disposition")
        };
    }

    /// <summary>Mirrors the writer's new-ref chain: structural-marker drop, render-culling
    /// drop, sparse-cell gate, then remap→validate→recover→rescue on the NAME base.</summary>
    private static PlacedRefDecision DecideNew(
        RecordPlan child,
        PlacedReference placed,
        int plannedGroupType,
        CellPlan cellPlan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        IReadOnlySet<uint> emittedFormIds,
        CellVerdictInputs inputs)
    {
        if (PlacedReferenceAnalysis.IsRuntimeStructuralMarkerPlacement(placed, masterByFormId, out _))
        {
            return Drop("refr.runtime-structural-marker");
        }

        if (cellPlan.DropRenderCullingMarkers
            && (PlacedRefBaseRules.RenderCullingEngineBases.Contains(placed.BaseFormId)
                || (masterByFormId.TryGetValue(placed.BaseFormId, out var newBaseRecord)
                    && CellStructuralReferencePreserver.IsStructuralMarkerBase(newBaseRecord))))
        {
            return Drop("cell.render-culling-marker-dropped");
        }

        // Reparented children were deliberately routed into the container (e.g. rescued
        // enable-parent markers captured without the persistent flag) — the move IS the
        // decision to emit them there.
        if (cellPlan.Mode == CellMergeMode.PersistentOnly
            && !placed.IsPersistent
            && !placed.IsMapMarker
            && !child.Reparented)
        {
            return Drop("cell.persistent-only-nonpersistent-ref");
        }

        var baseFormId = placed.BaseFormId;
        if (sourceToEmitted.TryGetValue(baseFormId, out var remappedBase))
        {
            baseFormId = remappedBase;
        }

        string? auxStatCode = null;
        if (!PlacedRefBaseRules.IsValidPlacedBase(
                child.Type, baseFormId, masterByFormId, emittedFormIds, out var knownBaseRecordType))
        {
            if (inputs.RecoverLeveledSpawnActors
                && child.Type is "ACHR" or "ACRE"
                && placed.BaseFormId >= 0xFF000000u
                && PlacedRefBaseRules.TryRecoverLeveledSpawnBase(
                    placed, child.Type, sourceToEmitted, masterByFormId, emittedFormIds,
                    inputs.DmpBaseTypes, out var recoveredBase))
            {
                auxStatCode = "refr.leveled-recovered";
                baseFormId = recoveredBase;
            }
            else if (knownBaseRecordType is null
                     && baseFormId < 0xFF000000u
                     && inputs.EnableRefrBaseEditorIdRemap
                     && !string.IsNullOrEmpty(placed.BaseEditorId))
            {
                var rescue = ReferenceBaseRemapper.TryRescueBaseByEditorIdStem(
                    child.Type, baseFormId, placed.BaseEditorId,
                    inputs.MasterIndex.StemToFormIdsByType, inputs.DmpBaseTypes);
                if (rescue.Outcome == ReferenceBaseRemapper.StemRescueOutcome.Rescued
                    && !sourceToEmitted.ContainsKey(rescue.WinningFormId)
                    && PlacedRefBaseRules.IsValidPlacedBase(
                        child.Type, rescue.WinningFormId, masterByFormId, emittedFormIds, out _))
                {
                    auxStatCode = "refr.editorid-remap";
                    baseFormId = rescue.WinningFormId;
                }
                else
                {
                    var ambiguous = rescue.Outcome
                        is ReferenceBaseRemapper.StemRescueOutcome.Ambiguous
                        or ReferenceBaseRemapper.StemRescueOutcome.CrossTypeAmbiguous;
                    return Drop("refr.dangling-base",
                        ambiguous ? "refr.editorid-remap-ambiguous" : null);
                }
            }
            else
            {
                return Drop(knownBaseRecordType is null
                    ? "refr.dangling-base"
                    : "refr.base-type-mismatch");
            }
        }

        return new PlacedRefDecision
        {
            Verdict = PlacedRefEmitVerdict.Emit,
            FinalBaseFormId = baseFormId,
            TargetGroupType = plannedGroupType,
            AuxStatCode = auxStatCode,
            NewInitiallyDisabled = StripStreetLightPolicy.SelectNewInitiallyDisabled(child, placed, cellPlan),
            NewEnableParentFormId = StripStreetLightPolicy.SelectNewEnableParent(placed, cellPlan)
        };
    }

    /// <summary>Mirrors the writer's override chain: temp-actor suppression, render-culling
    /// drop, sparse-cell preservation, cross-cell-move deferral, then master-bucket routing.
    /// Returns null for a deferred cross-cell move (phase 2 settles it).</summary>
    private static PlacedRefDecision? DecideOverride(
        RecordPlan child,
        PlacedReference placed,
        int plannedGroupType,
        CellPlan cellPlan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        CellVerdictInputs inputs,
        List<CrossCellMoveCandidate> moveCandidates)
    {
        if (!masterByFormId.TryGetValue(child.FormId, out var masterRecord))
        {
            // No master to merge onto. (override-verdict-without-master = Emit lost its master.)
            return Drop("refr.override-no-master-record");
        }

        // Master TEMPORARY actors must never be re-emitted by an ESM-flagged plugin; the
        // master's own copy streams at attach. Marking the ref covered keeps carry-forward
        // and tombstone synthesis away from it. (Full rationale in the writer encoder.)
        if (child.Type is "ACHR" or "ACRE"
            && inputs.MasterIndex.ChildLocations.TryGetValue(child.FormId, out var actorLocation)
            && actorLocation.GroupType == 9)
        {
            return Drop("actor.temp-override-suppressed-esm", marksMasterCovered: true);
        }

        if (cellPlan.DropRenderCullingMarkers
            && CellStructuralReferencePreserver.IsRenderCullingMarker(masterRecord, masterByFormId))
        {
            return Drop("cell.render-culling-marker-dropped");
        }

        // Reparented children are deliberate moves into the worldspace container
        // (PersistentCellReparenting): the captured override must APPLY, so neither the
        // sparse-cell master-preservation nor the parent-cell guard may drop it — the
        // "mismatched" parent is the whole point of the move.
        if (cellPlan.Mode == CellMergeMode.PersistentOnly
            && !child.Reparented
            && !(placed.IsMapMarker && PlacedReferenceAnalysis.MapMarkerDiffersFromMaster(placed, masterRecord)))
        {
            return Drop("refr.sparse-cell-master-preserved");
        }

        // Overrides re-bucket to master's original child GRUP; the record-header persistent
        // flag (inherited verbatim from the master header) wins over any bucket-lookup miss.
        var routeGroupType = plannedGroupType;
        if (inputs.MasterIndex.ChildLocations.TryGetValue(child.FormId, out var location)
            && location.GroupType is 8 or 9 or 10)
        {
            routeGroupType = location.GroupType;
        }

        if ((masterRecord.Header.Flags & 0x00000400u) != 0)
        {
            routeGroupType = 8;
        }

        var isForeignParent = inputs.MasterIndex.RefToCell.TryGetValue(child.FormId, out var masterParentCell)
                              && masterParentCell != 0
                              && masterParentCell != cellPlan.CellFormId
                              && !placed.IsMapMarker
                              && !child.Reparented;

        // A cross-cell-MOVED ref applies the proto's authored enable-state (EthelPhebus: retail
        // parks the cut NPC disabled in another worldspace, the proto shipped her live).
        // Deliberately NOT extended to plain child.Reparented — PersistentCellReparenting is a
        // file-format normalization, not evidence of authored enable-state, and a DMP is a
        // mid-session snapshot in which quest-gated actors read back enabled. In-game proven
        // (xex44.v140): 105 master-disabled refs re-enabled incl. GSJoeCobb 0x00104C68 and
        // ChavezPowderGanger* 0x000F1971-73. USER POLICY 2026-08-04, matching NAM6/AIDT:
        // runtime state never overwrites authored file state.
        // ONE scoped exception (USER RULING 2026-08-05): ProtoWorldspaceRehome — re-homed
        // into a PLUGIN-NEW worldspace's container, where master authored no cells at all, so
        // no authored enable-state exists to preserve (Emily Ortal / Fretwell class).
        // Master-container re-homes never set the flag; Powder-Ganger overlap measured 0.
        bool? overrideInitiallyDisabled = null;
        if ((isForeignParent || child.ProtoWorldspaceRehome)
            && (masterRecord.Header.Flags & 0x00000800u) != 0 != placed.IsInitiallyDisabled)
        {
            overrideInitiallyDisabled = placed.IsInitiallyDisabled;
        }

        if (isForeignParent)
        {
            // USER POLICY (2026-07-20, Goodsprings cemetery): in a master-anchored capture
            // cell the DMP's parent assignment IS the proto-authored placement (retail
            // relocated the content; the proto cell was even named GoodspringsCemeteryOLD
            // in interim builds) — emit the override HERE, true to the dump.
            // Exclusions keep the legacy drop: NEW-worldspace capture cells (retail records
            // stay untouched there; doors clone as fresh NEW refs via OverrideDoorCloning)
            // and persistent-routed refs (exterior grid-cell Persistent GRUPs never load —
            // persistent moves are PersistentCellReparenting's domain).
            if (routeGroupType == 8 || cellPlan.CellRecordPlan.Master is null)
            {
                return Drop("refr.parent-cell-mismatch");
            }

            moveCandidates.Add(new CrossCellMoveCandidate(
                cellPlan.CellFormId, child.FormId, routeGroupType, overrideInitiallyDisabled));
            return null;
        }

        return new PlacedRefDecision
        {
            Verdict = PlacedRefEmitVerdict.Emit,
            TargetGroupType = routeGroupType,
            MarksMasterCovered = true,
            OverrideInitiallyDisabled = overrideInitiallyDisabled
        };
    }

    private static PlacedRefDecision Drop(string reason, string? aux = null, bool marksMasterCovered = false) =>
        new()
        {
            Verdict = PlacedRefEmitVerdict.Drop,
            DropReason = reason,
            AuxStatCode = aux,
            MarksMasterCovered = marksMasterCovered
        };

    /// <summary>
    ///     A captured master ref parented to a master-anchored cell that is NOT its master
    ///     parent — the DMP moved it there (Goodsprings cemetery class). Deferred to phase 2
    ///     so home-cell captures win the FormID before any cross-cell move claims it.
    /// </summary>
    private sealed record CrossCellMoveCandidate(
        uint CellFormId, uint ChildFormId, int TargetGroupType, bool? OverrideInitiallyDisabled);
}
