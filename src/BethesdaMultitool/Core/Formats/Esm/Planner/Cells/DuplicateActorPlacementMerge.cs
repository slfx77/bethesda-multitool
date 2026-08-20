using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Folds proto-authored duplicate actor placements onto master's own ref. The proto
///     places some retail NPCs with its OWN ACHR FormIDs (different ref, same NPC_ base);
///     emitting those as NEW records puts a second copy of the actor in the world next to
///     master's (seen in-game as 2× Julie Farkas, 3× Arcade Gannon). Policy: never
///     duplicate — MOVE the existing. A NEW ACHR/ACRE whose base is a master actor with
///     exactly ONE master placement becomes an Override of that master ref carrying the
///     captured placement (flagged Reparented so the container move + verdict bypasses
///     apply). Bases master places multiple times are ambiguous and left alone.
/// </summary>
/// <remarks>
///     Master-TEMPORARY refs can't be moved by an ESM (re-emission never initializes — see
///     PlannedPlacedRefEncoder); the converted Override is then suppressed by the verdict
///     pass, which still removes the duplicate: master's copy plays at master's position
///     and the captured position is reported lost. Runs BEFORE
///     <see cref="PersistentCellReparenting" /> so converted overrides ride the normal
///     container routing.
/// </remarks>
public static class DuplicateActorPlacementMerge
{
    public static ImmutableDictionary<uint, CellPlan> Apply(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
    {
        if (cells.IsEmpty)
        {
            return cells;
        }

        var soleMasterPlacementByBase = BuildSoleMasterPlacementIndex(masterRecordsByFormId);
        if (soleMasterPlacementByBase.Count == 0)
        {
            return cells;
        }

        // A master ref may only be overridden ONCE across the whole plan: the runtime shares
        // actors into several cells and the proto can re-place one repeatedly, so without
        // global accounting the same master FormID emits 2-3× (validation on a captured
        // dump found 25 duplicate FormIDs). Master refs already covered by a captured Override anywhere
        // are consumed up front — that capture IS the move; extra proto copies drop.
        var consumedMasterRefs = new HashSet<uint>();
        foreach (var plan in cells.Values)
        {
            CollectExistingOverrides(plan.PersistentChildren, consumedMasterRefs);
            CollectExistingOverrides(plan.TemporaryChildren, consumedMasterRefs);
            CollectExistingOverrides(plan.VwdChildren, consumedMasterRefs);
        }

        var result = cells.ToBuilder();
        var anyChanged = false;

        // Sorted cell order so "first capture wins" is deterministic.
        foreach (var cellFormId in cells.Keys.OrderBy(id => id))
        {
            var cellPlan = cells[cellFormId];
            var mutated = false;
            var persistent = MergeBucket(
                cellPlan.PersistentChildren, soleMasterPlacementByBase, consumedMasterRefs, ref mutated);
            var temporary = MergeBucket(
                cellPlan.TemporaryChildren, soleMasterPlacementByBase, consumedMasterRefs, ref mutated);
            var vwd = MergeBucket(
                cellPlan.VwdChildren, soleMasterPlacementByBase, consumedMasterRefs, ref mutated);
            if (!mutated)
            {
                continue;
            }

            anyChanged = true;
            result[cellFormId] = cellPlan with
            {
                PersistentChildren = persistent,
                TemporaryChildren = temporary,
                VwdChildren = vwd
            };
        }

        return anyChanged ? result.ToImmutable() : cells;
    }

    private static void CollectExistingOverrides(
        ImmutableArray<RecordPlan> bucket, HashSet<uint> consumedMasterRefs)
    {
        foreach (var child in bucket)
        {
            if (child.Disposition == RecordDisposition.Override
                && child.Type is "ACHR" or "ACRE")
            {
                consumedMasterRefs.Add(child.FormId);
            }
        }
    }

    private static ImmutableArray<RecordPlan> MergeBucket(
        ImmutableArray<RecordPlan> bucket,
        Dictionary<uint, uint> soleMasterPlacementByBase,
        HashSet<uint> consumedMasterRefs,
        ref bool mutated)
    {
        if (bucket.IsEmpty)
        {
            return bucket;
        }

        ImmutableArray<RecordPlan>.Builder? builder = null;
        for (var i = 0; i < bucket.Length; i++)
        {
            var child = bucket[i];
            if (child.Disposition != RecordDisposition.New
                || child.Type is not ("ACHR" or "ACRE")
                || child.Model is not PlacedReference placed
                || !soleMasterPlacementByBase.TryGetValue(placed.BaseFormId, out var masterRefId))
            {
                builder?.Add(child);
                continue;
            }

            if (builder is null)
            {
                builder = ImmutableArray.CreateBuilder<RecordPlan>(bucket.Length);
                for (var j = 0; j < i; j++)
                {
                    builder.Add(bucket[j]);
                }
            }

            if (!consumedMasterRefs.Add(masterRefId))
            {
                // The master ref is already covered (captured override or an earlier proto
                // copy won) — this extra placement is the duplicate; drop it.
                builder.Add(child with
                {
                    Disposition = RecordDisposition.Skip,
                    Provenance = new PlanProvenance
                    {
                        PolicyId = "DuplicateActorPlacementMerge",
                        Reason = "Extra proto placement of a master actor already covered by "
                                 + $"another override of 0x{masterRefId:X8}; dropped (never duplicate)."
                    }
                });
                mutated = true;
                continue;
            }

            builder.Add(child with
            {
                Disposition = RecordDisposition.Override,
                FormId = masterRefId,
                SourceFormId = child.SourceFormId ?? child.FormId,
                Reparented = true,
                Provenance = new PlanProvenance
                {
                    PolicyId = "DuplicateActorPlacementMerge",
                    Reason = "Proto re-placed a master actor with its own ref; folded onto "
                             + $"master's sole placement 0x{masterRefId:X8} (move, never duplicate)."
                }
            });
            mutated = true;
        }

        return builder?.ToImmutable() ?? bucket;
    }

    /// <summary>
    ///     Base FormID → the SINGLE master ACHR/ACRE placing it. Bases with two or more
    ///     master placements are excluded (folding would be ambiguous).
    /// </summary>
    private static Dictionary<uint, uint> BuildSoleMasterPlacementIndex(
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
    {
        var soleByBase = new Dictionary<uint, uint>();
        var ambiguous = new HashSet<uint>();

        foreach (var (refFormId, record) in masterRecordsByFormId)
        {
            if (record.Header.Signature is not ("ACHR" or "ACRE"))
            {
                continue;
            }

            var name = record.Subrecords.FirstOrDefault(s =>
                s.Signature == "NAME" && s.Data.Length >= 4);
            if (name is null)
            {
                continue;
            }

            var baseFormId = BinaryPrimitives.ReadUInt32LittleEndian(name.Data.AsSpan(0, 4));
            if (baseFormId == 0 || ambiguous.Contains(baseFormId))
            {
                continue;
            }

            if (soleByBase.Remove(baseFormId))
            {
                ambiguous.Add(baseFormId);
                continue;
            }

            soleByBase[baseFormId] = refFormId;
        }

        return soleByBase;
    }
}
