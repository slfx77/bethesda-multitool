using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Repairs prototype/master XTEL identity collisions after Strip-side doors have been
///     cloned. A captured reciprocal DOOR counterpart can share its FormID with a retail
///     STAT-base REFR; this pass clones the captured door and rewrites the pair without
///     mutating the retail static. Unprovable targets lose XTEL with a planner diagnostic.
/// </summary>
internal static class DoorTeleportTargetRescue
{
    private const string TargetRescuePolicyId = "OverrideDoorTeleportTargetRescue";

    public static ImmutableDictionary<uint, CellPlan> Apply(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        FormIdAllocator allocator,
        Dictionary<uint, uint> clonesBySource,
        out ImmutableArray<PlanDiagnostic> diagnostics)
    {
        var diagnosticBuilder = ImmutableArray.CreateBuilder<PlanDiagnostic>();
        var result = RescueCapturedTargets(
            cells, masterRecordsByFormId, allocator, clonesBySource, diagnosticBuilder);
        result = RewriteCloneTeleports(
            result, masterRecordsByFormId, clonesBySource, diagnosticBuilder);
        diagnostics = diagnosticBuilder.ToImmutable();
        return result;
    }

    private static ImmutableDictionary<uint, CellPlan> RescueCapturedTargets(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        FormIdAllocator allocator,
        Dictionary<uint, uint> clonesBySource,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        var incompatibleTargets = EnumerateChildren(cells)
            .Where(entry => entry.Child.Disposition == RecordDisposition.New
                            && entry.Child.SourceFormId is { } source
                            && clonesBySource.ContainsKey(source)
                            && entry.Child.Model is PlacedReference { DestinationDoorFormId: not null })
            .Select(entry => ((PlacedReference)entry.Child.Model!).DestinationDoorFormId!.Value)
            .Where(target => IsIncompatibleMasterDoorTarget(target, masterRecordsByFormId))
            .Distinct()
            .ToArray();

        var rescuedPlans = new Dictionary<uint, (uint EmittedFormId, RecordPlan Child)>();
        foreach (var target in incompatibleTargets)
        {
            if (clonesBySource.ContainsKey(target))
            {
                continue;
            }

            var candidates = EnumerateChildren(cells)
                .Where(entry => entry.Child.FormId == target
                                && CanRescueCapturedDoorTarget(
                                    entry.Cell, entry.Child, masterRecordsByFormId))
                .Select(entry => entry.Child)
                .ToArray();
            if (candidates.Length != 1)
            {
                continue;
            }

            var emittedFormId = allocator.Allocate();
            clonesBySource.Add(target, emittedFormId);
            rescuedPlans.Add(target, (emittedFormId, candidates[0]));
            diagnostics.Add(new PlanDiagnostic
            {
                Kind = PlanDiagnosticKind.Decision,
                Phase = "References",
                Code = "references.repair.xtel-static-target-cloned",
                RecordType = "REFR",
                FormId = target,
                Message = $"Captured XTEL target REFR 0x{target:X8} is a DOOR in the DMP but "
                          + "the same retail FormID has a non-DOOR base; cloned the captured "
                          + $"counterpart as 0x{emittedFormId:X8} and retained the retail record."
            });
        }

        if (rescuedPlans.Count == 0)
        {
            return cells;
        }

        var result = cells.ToBuilder();
        foreach (var (cellFormId, cell) in cells)
        {
            var persistent = RescueBucket(cell.PersistentChildren, rescuedPlans, out var pChanged);
            var vwd = RescueBucket(cell.VwdChildren, rescuedPlans, out var vChanged);
            var temporary = RescueBucket(cell.TemporaryChildren, rescuedPlans, out var tChanged);
            if (pChanged || vChanged || tChanged)
            {
                result[cellFormId] = cell with
                {
                    PersistentChildren = persistent,
                    VwdChildren = vwd,
                    TemporaryChildren = temporary
                };
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<RecordPlan> RescueBucket(
        ImmutableArray<RecordPlan> bucket,
        Dictionary<uint, (uint EmittedFormId, RecordPlan Child)> rescuedPlans,
        out bool changed)
    {
        changed = false;
        if (bucket.IsEmpty)
        {
            return bucket;
        }

        var builder = ImmutableArray.CreateBuilder<RecordPlan>(bucket.Length);
        foreach (var child in bucket)
        {
            if (!rescuedPlans.TryGetValue(child.FormId, out var rescue)
                || !ReferenceEquals(rescue.Child, child))
            {
                builder.Add(child);
                continue;
            }

            builder.Add(child with
            {
                Disposition = RecordDisposition.New,
                FormId = rescue.EmittedFormId,
                SourceFormId = child.FormId,
                Master = null,
                Provenance = new PlanProvenance
                {
                    PolicyId = TargetRescuePolicyId,
                    Reason = "Captured reciprocal XTEL target is a DOOR, while its retail "
                             + $"REFR identity 0x{child.FormId:X8} resolves to a non-DOOR base; "
                             + "cloned as NEW without overriding the retail static placement."
                }
            });
            changed = true;
        }

        return changed ? builder.ToImmutable() : bucket;
    }

    private static ImmutableDictionary<uint, CellPlan> RewriteCloneTeleports(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint> clonesBySource,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        var result = cells.ToBuilder();
        foreach (var (cellFormId, cell) in cells)
        {
            var persistent = RewriteBucket(cell.PersistentChildren, masterRecordsByFormId,
                clonesBySource, diagnostics, out var pChanged);
            var vwd = RewriteBucket(cell.VwdChildren, masterRecordsByFormId,
                clonesBySource, diagnostics, out var vChanged);
            var temporary = RewriteBucket(cell.TemporaryChildren, masterRecordsByFormId,
                clonesBySource, diagnostics, out var tChanged);
            if (pChanged || vChanged || tChanged)
            {
                result[cellFormId] = cell with
                {
                    PersistentChildren = persistent,
                    VwdChildren = vwd,
                    TemporaryChildren = temporary
                };
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<RecordPlan> RewriteBucket(
        ImmutableArray<RecordPlan> bucket,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint> clonesBySource,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics,
        out bool changed)
    {
        changed = false;
        if (bucket.IsEmpty)
        {
            return bucket;
        }

        var builder = ImmutableArray.CreateBuilder<RecordPlan>(bucket.Length);
        foreach (var child in bucket)
        {
            if (child.Disposition != RecordDisposition.New
                || child.SourceFormId is not { } source
                || !clonesBySource.ContainsKey(source)
                || child.Model is not PlacedReference { DestinationDoorFormId: { } target } placed)
            {
                builder.Add(child);
                continue;
            }

            if (clonesBySource.TryGetValue(target, out var emittedTarget))
            {
                builder.Add(child with
                {
                    Model = placed with { DestinationDoorFormId = emittedTarget }
                });
                changed = true;
                continue;
            }

            if (IsLiveMasterDoorReference(target, masterRecordsByFormId)
                || !masterRecordsByFormId.ContainsKey(target))
            {
                // Targets absent from master may still be ordinary DMP-new refs allocated
                // in the general source map; the writer remaps and type-checks those later.
                builder.Add(child);
                continue;
            }

            builder.Add(child with
            {
                Model = placed with
                {
                    DestinationDoorFormId = null,
                    DestinationCellFormId = null,
                    TeleportPosRot = null,
                    TeleportFlags = null
                }
            });
            changed = true;
            diagnostics.Add(new PlanDiagnostic
            {
                Kind = PlanDiagnosticKind.Warning,
                Phase = "References",
                Code = "references.drop.xtel-target-not-door",
                RecordType = "REFR",
                FormId = child.FormId,
                Message = $"Dropped XTEL from cloned door REFR 0x{child.FormId:X8}: target "
                          + $"0x{target:X8} is live in the master but is not a DOOR-base REFR, "
                          + "and no unique captured DOOR counterpart could be rescued."
            });
        }

        return changed ? builder.ToImmutable() : bucket;
    }

    private static IEnumerable<(CellPlan Cell, RecordPlan Child)> EnumerateChildren(
        ImmutableDictionary<uint, CellPlan> cells)
    {
        foreach (var cell in cells.Values)
        {
            foreach (var child in cell.PersistentChildren
                         .Concat(cell.VwdChildren)
                         .Concat(cell.TemporaryChildren))
            {
                yield return (cell, child);
            }
        }
    }

    private static bool CanRescueCapturedDoorTarget(
        CellPlan cell,
        RecordPlan child,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
    {
        return cell.CellRecordPlan.Disposition != RecordDisposition.Skip
               && cell.CellRecordPlan.Model is not CellRecord { IsVirtual: true }
               && child.Disposition == RecordDisposition.Override
               && child.Type == "REFR"
               && child.Model is PlacedReference placed
               && IsDoorBase(placed.BaseFormId, masterRecordsByFormId)
               && IsIncompatibleMasterDoorTarget(child.FormId, masterRecordsByFormId);
    }

    private static bool IsIncompatibleMasterDoorTarget(
        uint refFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
    {
        return masterRecordsByFormId.ContainsKey(refFormId)
               && !IsLiveMasterDoorReference(refFormId, masterRecordsByFormId);
    }

    private static bool IsLiveMasterDoorReference(
        uint refFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
    {
        return masterRecordsByFormId.TryGetValue(refFormId, out var target)
               && target.Header.Signature == "REFR"
               && TryReadNameFormId(target, out var baseFormId)
               && IsDoorBase(baseFormId, masterRecordsByFormId);
    }

    private static bool IsDoorBase(
        uint baseFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
    {
        return masterRecordsByFormId.TryGetValue(baseFormId, out var baseRecord)
               && baseRecord.Header.Signature == "DOOR";
    }

    private static bool TryReadNameFormId(ParsedMainRecord record, out uint formId)
    {
        foreach (var subrecord in record.Subrecords)
        {
            if (subrecord.Signature == "NAME" && subrecord.Data.Length >= 4)
            {
                formId = BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data.AsSpan(0, 4));
                return true;
            }
        }

        formId = 0;
        return false;
    }
}
