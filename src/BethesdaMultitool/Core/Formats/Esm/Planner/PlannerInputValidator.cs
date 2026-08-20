using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;

namespace BethesdaMultitool.Core.Formats.Esm.Planner;

/// <summary>
///     Validates cross-pipeline master aliases and diagnostic directives before planning
///     begins. Keeping these input-only checks separate leaves <see cref="EsmPlanner" />
///     focused on phase coordination.
/// </summary>
internal static class PlannerInputValidator
{
    internal static IReadOnlyDictionary<uint, uint> ValidateCapturedMasterAliases(
        IReadOnlyList<ParsedMainRecord> masterRecords,
        DmpRecordSource dmpSource,
        IReadOnlyDictionary<uint, uint>? aliases)
    {
        if (aliases is null || aliases.Count == 0)
        {
            return ImmutableDictionary<uint, uint>.Empty;
        }

        var masterTypes = masterRecords
            .Where(static record => record.Header.Signature != "TES4")
            .GroupBy(static record => record.Header.FormId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(record => record.Header.Signature)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
        var capturedTypes = dmpSource.EnumerateAll()
            .Where(entry => aliases.ContainsKey(entry.FormId))
            .GroupBy(static entry => entry.FormId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(entry => entry.Type)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
        var validated = ImmutableDictionary.CreateBuilder<uint, uint>();
        foreach (var (sourceFormId, masterFormId) in aliases)
        {
            if (sourceFormId == 0 || sourceFormId == masterFormId
                                  || !capturedTypes.TryGetValue(sourceFormId, out var sourceTypes))
            {
                continue;
            }

            if (!masterTypes.TryGetValue(masterFormId, out var targetTypes))
            {
                throw new InvalidOperationException(
                    $"DMP FormID alias 0x{sourceFormId:X8} targets missing master " +
                    $"0x{masterFormId:X8}.");
            }

            if (sourceTypes.Length != 1 || targetTypes.Length != 1
                                        || !string.Equals(sourceTypes[0], targetTypes[0], StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"DMP FormID alias 0x{sourceFormId:X8} has captured type(s) " +
                    $"{string.Join(',', sourceTypes)}, but master 0x{masterFormId:X8} has type(s) " +
                    $"{string.Join(',', targetTypes)}; aliases must preserve one exact record type.");
            }

            validated.Add(sourceFormId, masterFormId);
        }

        return validated.ToImmutable();
    }

    internal static void ValidateDiagnosticDirectives(
        IReadOnlyList<ParsedMainRecord> masterRecords,
        IReadOnlyList<CatalogEntry> catalog,
        IReadOnlySet<string> enabledTypes,
        ImmutableHashSet<uint> keepMasterFormIds,
        ImmutableDictionary<uint, ImmutableHashSet<string>> retainMasterSubrecords)
    {
        var overlap = keepMasterFormIds.FirstOrDefault(retainMasterSubrecords.ContainsKey);
        if (overlap != 0 || (keepMasterFormIds.Contains(0) && retainMasterSubrecords.ContainsKey(0)))
        {
            throw new InvalidOperationException(
                $"Diagnostic FormID 0x{overlap:X8} cannot be both master-pure and partially retained.");
        }

        var masterByFormId = new Dictionary<uint, ParsedMainRecord>();
        foreach (var master in masterRecords)
        {
            masterByFormId.TryAdd(master.Header.FormId, master);
        }

        foreach (var formId in keepMasterFormIds.Concat(retainMasterSubrecords.Keys).Distinct())
        {
            if (!masterByFormId.TryGetValue(formId, out var master))
            {
                throw new InvalidOperationException(
                    $"Diagnostic FormID 0x{formId:X8} does not exist in the master ESM.");
            }

            if (!enabledTypes.Contains(master.Header.Signature))
            {
                throw new InvalidOperationException(
                    $"Diagnostic FormID 0x{formId:X8} is {master.Header.Signature}, which is not planner-enabled.");
            }

            var entry = catalog.FirstOrDefault(candidate =>
                candidate.MasterFormId == formId
                && string.Equals(candidate.Type, master.Header.Signature, StringComparison.Ordinal));
            if (entry is null || entry.Source != SourceKind.DmpOverride)
            {
                throw new InvalidOperationException(
                    $"Diagnostic FormID 0x{formId:X8} is not an exact DMP override of its master record.");
            }

            if (!retainMasterSubrecords.TryGetValue(formId, out var signatures))
            {
                continue;
            }

            if (signatures.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Diagnostic FormID 0x{formId:X8} has no retained subrecord signatures.");
            }

            foreach (var signature in signatures)
            {
                if (signature.Length != 4)
                {
                    throw new InvalidOperationException(
                        $"Diagnostic subrecord signature '{signature}' for 0x{formId:X8} must be exactly four characters.");
                }

                if (!master.Subrecords.Any(subrecord =>
                        string.Equals(subrecord.Signature, signature, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Master {master.Header.Signature} 0x{formId:X8} has no {signature} subrecord to retain.");
                }
            }
        }
    }
}
