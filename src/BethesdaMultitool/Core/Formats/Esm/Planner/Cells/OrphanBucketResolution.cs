using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Final disposition of parse-time orphan buckets ("[Virtual gx,gy]" tiles and
///     "[Unresolved ws]" buckets) after <see cref="PersistentCellReparenting" /> has run its
///     container rescue: buckets never emit, and each remaining child is either re-homed to its
///     master-authored cell or dropped with a named diagnostic.
///     <para>
///         Master-home re-homing (USER RULING 2026-09-01): an Override child dying in an orphan
///         bucket is a captured delta of a ref the MASTER already places, and the master's GRUP
///         containment says exactly which cell that is — FormID evidence, no grid heuristic.
///         Re-home it there when that cell is part of the plan. Cells the plan does not touch
///         stay master-preserved (the sparse-cell policy); synthesizing an override for them just
///         to carry one delta would reverse that ruling, so those children still drop. Measured
///         on xex44: 2,593 of 2,602 dying children re-home (all Overrides), leaving only the 9
///         unresolved-parent New records that nothing can place.
///     </para>
/// </summary>
internal static class OrphanBucketResolution
{
    /// <summary>Record-header persistent flag (0x400), mirroring <see cref="PersistentCellReparenting" />.</summary>
    private const uint PersistentHeaderFlag = 0x00000400u;

    public static void ResolveAndRemove(
        ImmutableDictionary<uint, CellPlan>.Builder result,
        IReadOnlyList<uint> orphanBuckets,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? masterRefToCell,
        ImmutableArray<PlanDiagnostic>.Builder diagnosticsBuilder)
    {
        var orphanBucketSet = new HashSet<uint>(orphanBuckets);
        HashSet<uint>? emittedChildFormIds = null;

        bool MasterHomePersistent(RecordPlan child)
        {
            return masterRecordsByFormId.TryGetValue(child.FormId, out var masterChild)
                   && (masterChild.Header.Flags & PersistentHeaderFlag) != 0;
        }

        HashSet<uint> BuildEmittedChildIndex()
        {
            var ids = new HashSet<uint>();
            foreach (var (planCellId, plan) in result)
            {
                if (orphanBucketSet.Contains(planCellId))
                {
                    continue;
                }

                foreach (var child in plan.PersistentChildren
                             .Concat(plan.TemporaryChildren)
                             .Concat(plan.VwdChildren))
                {
                    ids.Add(child.FormId);
                }
            }

            return ids;
        }

        foreach (var formId in orphanBuckets)
        {
            if (result.TryGetValue(formId, out var bucket))
            {
                // The bucket population is heterogeneous and the message must say which class this
                // one is: a true "[Unresolved ws]" bucket (at most one per worldspace, no location
                // evidence) reads very differently in triage from a "[Virtual gx,gy]" tile whose
                // grid and worldspace were fully resolved and whose children died only because the
                // tile is virtual.
                var bucketModel = bucket.CellRecordPlan.Model as CellRecord;
                var bucketKind = bucketModel?.IsUnresolvedBucket == true
                    ? "unresolved-parent bucket"
                    : "virtual orphan tile";
                var bucketLocation = bucketModel is { GridX: not null, GridY: not null }
                    ? $" at ({bucketModel.GridX},{bucketModel.GridY})"
                    : string.Empty;

                foreach (var child in bucket.PersistentChildren
                             .Concat(bucket.TemporaryChildren)
                             .Concat(bucket.VwdChildren))
                {
                    if (child.Disposition == RecordDisposition.Skip)
                    {
                        continue;
                    }

                    string? dropDetail = null;
                    if (child.Disposition == RecordDisposition.Override
                        && masterRefToCell is not null
                        && masterRefToCell.TryGetValue(child.FormId, out var masterHomeCell)
                        && masterHomeCell != formId
                        && !orphanBucketSet.Contains(masterHomeCell))
                    {
                        if (result.TryGetValue(masterHomeCell, out var homePlan))
                        {
                            emittedChildFormIds ??= BuildEmittedChildIndex();
                            if (emittedChildFormIds.Add(child.FormId))
                            {
                                // Persistence class follows the MASTER's filing, keeping the
                                // GRUP/flag agreement the engine requires (a master-temporary ref
                                // must sit in the temp-children GRUP of its own cell).
                                result[masterHomeCell] = MasterHomePersistent(child)
                                    ? homePlan with { PersistentChildren = homePlan.PersistentChildren.Add(child) }
                                    : homePlan with { TemporaryChildren = homePlan.TemporaryChildren.Add(child) };

                                diagnosticsBuilder.Add(new PlanDiagnostic
                                {
                                    Kind = PlanDiagnosticKind.Decision,
                                    Phase = "Cells",
                                    Code = "refr.orphan-rehomed-master-home",
                                    RecordType = child.Type,
                                    FormId = child.FormId,
                                    Message = $"{child.Type} 0x{child.FormId:X8} left doomed {bucketKind} " +
                                              $"0x{formId:X8}{bucketLocation} for its master-authored home cell " +
                                              $"0x{masterHomeCell:X8} (master GRUP containment).",
                                    Metadata = new Dictionary<string, string?>
                                    {
                                        ["bucket"] = $"0x{formId:X8}",
                                        ["masterHomeCell"] = $"0x{masterHomeCell:X8}",
                                        ["childAssignmentSource"] = (child.Model as PlacedReference)?.AssignmentSource
                                    }
                                });
                                continue;
                            }

                            dropDetail = "same FormID already emitted elsewhere";
                        }
                        else
                        {
                            dropDetail = $"master home cell 0x{masterHomeCell:X8} is master-preserved (not in plan)";
                        }
                    }

                    diagnosticsBuilder.Add(new PlanDiagnostic
                    {
                        Kind = PlanDiagnosticKind.Warning,
                        Phase = "Cells",
                        Code = "refr.orphan-bucket-dropped",
                        RecordType = child.Type,
                        FormId = child.FormId,
                        Message = $"{child.Type} 0x{child.FormId:X8} was captured in {bucketKind} " +
                                  $"0x{formId:X8}{bucketLocation} and did not qualify for container rescue" +
                                  (dropDetail is null ? string.Empty : $" ({dropDetail})") +
                                  " — dropped with the bucket.",
                        Metadata = new Dictionary<string, string?>
                        {
                            ["bucket"] = $"0x{formId:X8}",
                            ["bucketKind"] = bucketKind,
                            ["bucketEditorId"] = bucketModel?.EditorId,
                            ["bucketWorldspaceAssignmentSource"] = bucketModel?.WorldspaceAssignmentSource,
                            ["childAssignmentSource"] = (child.Model as PlacedReference)?.AssignmentSource,
                            ["dropDetail"] = dropDetail,
                            ["disposition"] = child.Disposition.ToString()
                        }
                    });
                }
            }

            result.Remove(formId);
        }
    }
}
