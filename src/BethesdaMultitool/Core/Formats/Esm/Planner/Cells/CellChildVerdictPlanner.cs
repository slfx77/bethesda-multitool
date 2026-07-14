using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
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
    ///     Rewrite each planned cell with its children's verdicts. Cells whose
    ///     <see cref="CellPlan.Mode" /> is null (mode-planning didn't run) are left
    ///     untouched — the writer's fallback owns their decisions.
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

        var result = cells.ToBuilder();
        foreach (var (cellFormId, cellPlan) in cells)
        {
            if (cellPlan.Mode is null)
            {
                continue;
            }

            var verdicts = ImmutableDictionary.CreateBuilder<uint, PlacedRefDecision>();
            DecideBucket(cellPlan.PersistentChildren, 8, cellPlan, masterByFormId,
                sourceToEmitted, emittedFormIds, inputs, verdicts);
            DecideBucket(cellPlan.VwdChildren, 10, cellPlan, masterByFormId,
                sourceToEmitted, emittedFormIds, inputs, verdicts);
            DecideBucket(cellPlan.TemporaryChildren, 9, cellPlan, masterByFormId,
                sourceToEmitted, emittedFormIds, inputs, verdicts);

            var withVerdicts = cellPlan with { RefDecisions = verdicts.ToImmutable() };
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

        var (emits, suppressReason) = (true, (string?)null);
        if (isMasterAnchored && genuine == 0)
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
            NavmOnlySuppressed = navmOnly,
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
                if (child.Type is not ("REFR" or "ACHR" or "ACRE")
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
        ImmutableDictionary<uint, PlacedRefDecision>.Builder verdicts)
    {
        foreach (var child in children)
        {
            if (child.Type is not ("REFR" or "ACHR" or "ACRE")
                || child.Model is not PlacedReference placed)
            {
                continue; // NAVM etc. are not placed refs; models other than PlacedReference
                          // never encode — the writer handles both without a verdict.
            }

            verdicts[child.FormId] = Decide(
                child, placed, plannedGroupType, cellPlan, masterByFormId,
                sourceToEmitted, emittedFormIds, inputs);
        }
    }

    private static PlacedRefDecision Decide(
        RecordPlan child,
        PlacedReference placed,
        int plannedGroupType,
        CellPlan cellPlan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        IReadOnlySet<uint> emittedFormIds,
        CellVerdictInputs inputs)
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
                child, placed, plannedGroupType, cellPlan, masterByFormId, inputs),
            // Other dispositions never encode (legacy returned null silently): drop with
            // no reason so the writer skips the stats counters.
            _ => new PlacedRefDecision { Verdict = PlacedRefEmitVerdict.Drop },
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
        if (PluginBuilder.IsRuntimeStructuralMarkerPlacement(placed, masterByFormId, out _))
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
                        aux: ambiguous ? "refr.editorid-remap-ambiguous" : null);
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
        };
    }

    /// <summary>Mirrors the writer's override chain: temp-actor suppression, render-culling
    /// drop, sparse-cell preservation, parent-cell guard, then master-bucket routing.</summary>
    private static PlacedRefDecision DecideOverride(
        RecordPlan child,
        PlacedReference placed,
        int plannedGroupType,
        CellPlan cellPlan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        CellVerdictInputs inputs)
    {
        if (!masterByFormId.TryGetValue(child.FormId, out var masterRecord))
        {
            // No master record to merge onto — silently not emitted (no stats), legacy parity.
            return new PlacedRefDecision { Verdict = PlacedRefEmitVerdict.Drop };
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
            && !(placed.IsMapMarker && PluginBuilder.MapMarkerDiffersFromMaster(placed, masterRecord)))
        {
            return Drop("refr.sparse-cell-master-preserved");
        }

        if (inputs.MasterIndex.RefToCell.TryGetValue(child.FormId, out var masterParentCell)
            && masterParentCell != 0
            && masterParentCell != cellPlan.CellFormId
            && !placed.IsMapMarker
            && !child.Reparented)
        {
            return Drop("refr.parent-cell-mismatch");
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

        // A reparented actor applies the proto's authored enable-state: master's copy of a
        // cut NPC is often disabled (EthelPhebus — retail parks her disabled in another
        // worldspace) while the proto shipped her live. Without this the move emits a
        // disabled actor and changes nothing in-game.
        bool? overrideInitiallyDisabled = null;
        if (child.Reparented
            && (masterRecord.Header.Flags & 0x00000800u) != 0 != placed.IsInitiallyDisabled)
        {
            overrideInitiallyDisabled = placed.IsInitiallyDisabled;
        }

        return new PlacedRefDecision
        {
            Verdict = PlacedRefEmitVerdict.Emit,
            TargetGroupType = routeGroupType,
            MarksMasterCovered = true,
            OverrideInitiallyDisabled = overrideInitiallyDisabled,
        };
    }

    private static PlacedRefDecision Drop(string reason, string? aux = null, bool marksMasterCovered = false) =>
        new()
        {
            Verdict = PlacedRefEmitVerdict.Drop,
            DropReason = reason,
            AuxStatCode = aux,
            MarksMasterCovered = marksMasterCovered,
        };
}
