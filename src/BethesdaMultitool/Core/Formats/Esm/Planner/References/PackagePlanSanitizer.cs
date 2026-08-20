using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References;

/// <summary>
///     Fail-closed planner pass for structural PACK location/target references. It runs
///     after all top-level and cell-child allocations are known, but before the live PACK
///     set is exposed to NPC PKID encoding.
/// </summary>
internal static class PackagePlanSanitizer
{
    internal static ImmutableHashSet<uint> BuildValidPackageFormIds(
        IReadOnlyList<ParsedMainRecord> masterRecords,
        ImmutableArray<RecordPlan> plannedRecords)
    {
        return masterRecords
            .Where(record => record.Header.Signature == "PACK")
            .Select(record => record.Header.FormId)
            .Concat(plannedRecords.Where(record => record.Type == "PACK").Select(record => record.FormId))
            .ToImmutableHashSet();
    }

    internal static PackagePlanSanitizationResult Apply(
        ImmutableArray<RecordPlan> records,
        IReadOnlySet<uint> emittedFormIds,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        IReadOnlyList<ParsedMainRecord> masterRecords,
        CellSectionPlanner.CellSectionResult? cellSection)
    {
        ArgumentNullException.ThrowIfNull(emittedFormIds);
        ArgumentNullException.ThrowIfNull(sourceToEmitted);
        ArgumentNullException.ThrowIfNull(masterRecords);

        var liveTypes = BuildLiveTypeIndex(records, masterRecords, cellSection);
        var updated = records.ToBuilder();
        var suppressedNewFormIds = ImmutableHashSet.CreateBuilder<uint>();
        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();

        for (var i = 0; i < updated.Count; i++)
        {
            var record = updated[i];
            if (record.Type != "PACK"
                || record.Disposition is RecordDisposition.KeepMaster or RecordDisposition.Skip
                || record.Model is not PackageRecord package)
            {
                continue;
            }

            var sanitation = PackageReferenceIntegrity.Sanitize(
                package, emittedFormIds, sourceToEmitted);
            var issue = sanitation.Issue;
            if (sanitation.IsValid)
            {
                issue = ValidateTargetTypes(sanitation.Package, liveTypes);
            }

            if (issue is null)
            {
                // Store the settled aliases in the model. PlanWriter's generic remapper is
                // idempotent for these final IDs, while direct encoder consumers no longer
                // need to rediscover the same source alias.
                updated[i] = record with { Model = sanitation.Package };
                continue;
            }

            var replacementDisposition = record.Master is null
                ? RecordDisposition.Skip
                : RecordDisposition.KeepMaster;
            if (replacementDisposition == RecordDisposition.Skip)
            {
                suppressedNewFormIds.Add(record.FormId);
            }

            var action = replacementDisposition == RecordDisposition.Skip
                ? "suppressed the new package"
                : "retained the master package without an override";
            var reason =
                $"{issue.FieldPath} Type {issue.UnionType} FormID 0x{issue.FormId:X8}: {issue.Reason}; " +
                $"{action} instead of changing its target/location semantics.";

            updated[i] = record with
            {
                Disposition = replacementDisposition,
                Provenance = new PlanProvenance
                {
                    PolicyId = "PackagePlanSanitizer.FailClosedStructuralReference",
                    Reason = reason
                }
            };
            diagnostics.Add(new PlanDiagnostic
            {
                Kind = PlanDiagnosticKind.Warning,
                Phase = "References",
                Code = replacementDisposition == RecordDisposition.Skip
                    ? "references.skip.pack-invalid-target"
                    : "references.keep-master.pack-invalid-target",
                RecordType = "PACK",
                FormId = record.FormId,
                Message = $"PACK 0x{record.FormId:X8} {reason}"
            });
        }

        return new PackagePlanSanitizationResult(
            updated.ToImmutable(), suppressedNewFormIds.ToImmutable(), diagnostics.ToImmutable());
    }

    private static PackageReferenceIssue? ValidateTargetTypes(
        PackageRecord package,
        IReadOnlyDictionary<uint, string> liveTypes)
    {
        var issue = ValidateLocation(package.Location, "PLDT", liveTypes);
        issue ??= ValidateTarget(package.Target, "PTDT", liveTypes);
        issue ??= ValidateLocation(package.Location2, "PLD2", liveTypes);
        issue ??= ValidateTarget(package.Target2, "PTD2", liveTypes);
        return issue;
    }

    private static PackageReferenceIssue? ValidateLocation(
        PackageLocation? location,
        string field,
        IReadOnlyDictionary<uint, string> liveTypes)
    {
        if (location is null || !PackageReferenceIntegrity.LocationTypeIsFormId(location.Type))
        {
            return null;
        }

        if (!liveTypes.TryGetValue(location.Union, out var targetType))
        {
            return new PackageReferenceIssue(
                $"{field}.Union", location.Union, location.Type,
                "live target has no emitted/master record type");
        }

        return PackageReferenceIntegrity.IsLocationTargetTypeAllowed(location.Type, targetType)
            ? null
            : new PackageReferenceIssue(
                $"{field}.Union", location.Union, location.Type,
                $"target resolves to {targetType}, which is invalid for this union arm");
    }

    private static PackageReferenceIssue? ValidateTarget(
        PackageTarget? target,
        string field,
        IReadOnlyDictionary<uint, string> liveTypes)
    {
        if (target is null || !PackageReferenceIntegrity.TargetTypeIsFormId(target.Type))
        {
            return null;
        }

        if (!liveTypes.TryGetValue(target.FormIdOrType, out var targetType))
        {
            return new PackageReferenceIssue(
                $"{field}.FormIdOrType", target.FormIdOrType, target.Type,
                "live target has no emitted/master record type");
        }

        return PackageReferenceIntegrity.IsPackageTargetTypeAllowed(target.Type, targetType)
            ? null
            : new PackageReferenceIssue(
                $"{field}.FormIdOrType", target.FormIdOrType, target.Type,
                $"target resolves to {targetType}, which is invalid for this union arm");
    }

    private static ImmutableDictionary<uint, string> BuildLiveTypeIndex(
        ImmutableArray<RecordPlan> records,
        IReadOnlyList<ParsedMainRecord> masterRecords,
        CellSectionPlanner.CellSectionResult? cellSection)
    {
        var types = ImmutableDictionary.CreateBuilder<uint, string>();
        foreach (var master in masterRecords)
        {
            types[master.Header.FormId] = master.Header.Signature;
        }

        foreach (var record in records)
        {
            AddPlanType(types, record);
        }

        if (cellSection is not null)
        {
            foreach (var cell in cellSection.CellsByFormId.Values)
            {
                AddPlanType(types, cell.CellRecordPlan);
                foreach (var child in cell.PersistentChildren)
                {
                    AddPlanType(types, child);
                }

                foreach (var child in cell.VwdChildren)
                {
                    AddPlanType(types, child);
                }

                foreach (var child in cell.TemporaryChildren)
                {
                    AddPlanType(types, child);
                }
            }
        }

        // PlayerRef is an engine-owned TESObjectREFR and therefore has no master record.
        types[0x00000014u] = "PLYR";
        return types.ToImmutable();
    }

    private static void AddPlanType(
        ImmutableDictionary<uint, string>.Builder types,
        RecordPlan record)
    {
        if (record.Disposition != RecordDisposition.Skip)
        {
            types[record.FormId] = record.Type;
        }
    }
}

internal sealed record PackagePlanSanitizationResult(
    ImmutableArray<RecordPlan> Records,
    ImmutableHashSet<uint> SuppressedNewFormIds,
    ImmutableArray<PlanDiagnostic> Diagnostics);
