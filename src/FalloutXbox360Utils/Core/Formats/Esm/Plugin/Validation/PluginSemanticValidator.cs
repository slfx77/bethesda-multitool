using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Validation;

/// <summary>
///     Post-emit semantic validation that catches the same structural-but-not-byte-corrupt
///     issues FNVEdit's "Check for Errors" flags. Each finding is a problem the byte-level
///     <see cref="PluginRoundTripValidator" /> doesn't notice — duplicate FormIDs, REFRs
///     in the wrong cell-children GRUP type for their persistent flag, dangling base
///     FormIDs in NAME subrecords, etc.
/// </summary>
public static class PluginSemanticValidator
{
    /// <summary>Cell Persistent Children GRUP type.</summary>
    private const int GrupTypePersistentChildren = 8;

    /// <summary>Cell Temporary Children GRUP type.</summary>
    private const int GrupTypeTemporaryChildren = 9;

    /// <summary>Cell Visible-When-Distant Children GRUP type.</summary>
    private const int GrupTypeVwdChildren = 10;

    /// <summary>Record header persistent flag bit (0x00000400).</summary>
    private const uint PersistentFlag = 0x00000400u;

    /// <summary>Cap on how many duplicate-FormID examples to list before truncating.</summary>
    private const int MaxDuplicateExamples = 10;

    /// <summary>
    ///     Run all semantic checks against the emitted plugin bytes. Optionally cross-checks
    ///     base-FormID references against the master ESM's known FormID set.
    /// </summary>
    public static SemanticValidationResult Validate(
        byte[] espBytes,
        IReadOnlySet<uint>? masterFormIds = null,
        IReadOnlyDictionary<string, HashSet<uint>>? masterFormIdsByType = null)
    {
        var (records, grupHeaders) = EsmParser.EnumerateRecordsWithGrups(espBytes);
        var pluginFormIdsByType = records
            .Where(r => r.Header.Signature != "TES4")
            .GroupBy(r => r.Header.Signature, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new HashSet<uint>(g.Select(r => r.Header.FormId)),
                StringComparer.Ordinal);
        var pluginFormIds = new HashSet<uint>(pluginFormIdsByType.Values.SelectMany(static ids => ids));

        // Offset-sorted event stream of GRUP headers + records, just like
        // PcEsmCellContextIndex uses to reconstruct the parent-GRUP stack.
        var events = new List<(long Offset, GrupHeaderInfo? Grup, ParsedMainRecord? Record)>(
            records.Count + grupHeaders.Count);
        foreach (var g in grupHeaders)
        {
            events.Add((g.Offset, g, null));
        }

        foreach (var r in records)
        {
            events.Add((r.Offset, null, r));
        }

        events.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var grupStack = new Stack<GrupHeaderInfo>();
        var formIdOccurrences = new Dictionary<uint, int>();
        var duplicateFormIds = new HashSet<uint>();
        var persistentFlagMismatches = new List<string>();
        var refrBaseDangling = new List<string>();
        var refrBaseTypeMismatches = new List<string>();
        var refrsInNonChildrenGrup = new List<string>();
        var totalRefrs = 0;

        foreach (var (offset, grup, record) in events)
        {
            while (grupStack.TryPeek(out var top) && top.Offset + top.GroupSize <= offset)
            {
                grupStack.Pop();
            }

            if (grup is not null)
            {
                grupStack.Push(grup);
                continue;
            }

            if (record is null || record.Header.Signature == "TES4")
            {
                continue;
            }

            var formId = record.Header.FormId;
            var signature = record.Header.Signature;

            // Within an ESP, no FormID should appear twice. Two records with the same FormID
            // is an immediate engine-crash trigger.
            if (!formIdOccurrences.TryAdd(formId, 1))
            {
                formIdOccurrences[formId]++;
                duplicateFormIds.Add(formId);
            }

            if (signature is "REFR" or "ACHR" or "ACRE")
            {
                totalRefrs++;
                if (grupStack.TryPeek(out var parentGrup))
                {
                    var isPersistent = (record.Header.Flags & PersistentFlag) != 0;
                    if (parentGrup.GroupType == GrupTypePersistentChildren && !isPersistent)
                    {
                        persistentFlagMismatches.Add(
                            $"{signature} 0x{formId:X8} is in Cell Persistent Children GRUP " +
                            $"(label 0x{ReadLabelFormId(parentGrup):X8}) but its record-header " +
                            "persistent flag (0x400) is not set.");
                    }
                    else if (parentGrup.GroupType == GrupTypeTemporaryChildren && isPersistent)
                    {
                        persistentFlagMismatches.Add(
                            $"{signature} 0x{formId:X8} is in Cell Temporary Children GRUP " +
                            $"(label 0x{ReadLabelFormId(parentGrup):X8}) but its record-header " +
                            "persistent flag (0x400) is set — should live in the type-8 " +
                            "persistent-children GRUP instead.");
                    }
                    else if (parentGrup.GroupType is not (GrupTypePersistentChildren
                             or GrupTypeTemporaryChildren or GrupTypeVwdChildren))
                    {
                        refrsInNonChildrenGrup.Add(
                            $"{signature} 0x{formId:X8} parent GRUP is type {parentGrup.GroupType}, " +
                            "expected 8 (Persistent Children), 9 (Temporary Children) or 10 (VWD Children).");
                    }
                }
                else
                {
                    refrsInNonChildrenGrup.Add(
                        $"{signature} 0x{formId:X8} has no parent GRUP — placed refs must live " +
                        "inside a cell-children GRUP.");
                }

                var baseId = ReadNameFormId(record);
                if (baseId.HasValue && baseId.Value != 0 && baseId.Value != 0xFFFFFFFFu
                    && TryResolveRecordType(
                        baseId.Value, masterFormIdsByType, pluginFormIdsByType, out var baseRecordType))
                {
                    if (!ReferenceBaseRemapper.CanPlacedRecordUseBaseType(signature, baseRecordType))
                    {
                        refrBaseTypeMismatches.Add(
                            $"{signature} 0x{formId:X8} base FormID 0x{baseId.Value:X8} is " +
                            $"{baseRecordType}, which is not a valid base type for {signature}.");
                    }
                }
                else if (masterFormIds is not null
                    && baseId.HasValue && baseId.Value != 0 && baseId.Value != 0xFFFFFFFFu
                    && !masterFormIds.Contains(baseId.Value) && !pluginFormIds.Contains(baseId.Value))
                {
                    refrBaseDangling.Add(
                        $"{signature} 0x{formId:X8} base FormID 0x{baseId.Value:X8} is " +
                        "neither in master nor freshly emitted by this plugin.");
                }
            }
        }

        var report = new StringBuilder();
        var errors = 0;
        var warnings = 0;

        if (duplicateFormIds.Count > 0)
        {
            errors += duplicateFormIds.Count;
            report.AppendLine($"ERROR: {duplicateFormIds.Count:N0} duplicate FormID(s) found:");
            foreach (var dup in duplicateFormIds.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  0x{dup:X8} appears {formIdOccurrences[dup]}x");
            }

            if (duplicateFormIds.Count > MaxDuplicateExamples)
            {
                report.AppendLine($"  …and {duplicateFormIds.Count - MaxDuplicateExamples:N0} more.");
            }
        }

        if (persistentFlagMismatches.Count > 0)
        {
            warnings += persistentFlagMismatches.Count;
            report.AppendLine(
                $"WARN: {persistentFlagMismatches.Count:N0} persistent-flag/parent-GRUP mismatch(es):");
            foreach (var msg in persistentFlagMismatches.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {msg}");
            }

            if (persistentFlagMismatches.Count > MaxDuplicateExamples)
            {
                report.AppendLine($"  …and {persistentFlagMismatches.Count - MaxDuplicateExamples:N0} more.");
            }
        }

        if (refrsInNonChildrenGrup.Count > 0)
        {
            errors += refrsInNonChildrenGrup.Count;
            report.AppendLine(
                $"ERROR: {refrsInNonChildrenGrup.Count:N0} placed ref(s) outside a cell-children GRUP:");
            foreach (var msg in refrsInNonChildrenGrup.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {msg}");
            }

            if (refrsInNonChildrenGrup.Count > MaxDuplicateExamples)
            {
                report.AppendLine($"  …and {refrsInNonChildrenGrup.Count - MaxDuplicateExamples:N0} more.");
            }
        }

        if (refrBaseDangling.Count > 0)
        {
            warnings += refrBaseDangling.Count;
            report.AppendLine($"WARN: {refrBaseDangling.Count:N0} placed ref(s) with dangling base FormID:");
            foreach (var msg in refrBaseDangling.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {msg}");
            }

            if (refrBaseDangling.Count > MaxDuplicateExamples)
            {
                report.AppendLine($"  …and {refrBaseDangling.Count - MaxDuplicateExamples:N0} more.");
            }
        }

        if (refrBaseTypeMismatches.Count > 0)
        {
            errors += refrBaseTypeMismatches.Count;
            report.AppendLine(
                $"ERROR: {refrBaseTypeMismatches.Count:N0} placed ref(s) with invalid base record type:");
            foreach (var msg in refrBaseTypeMismatches.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {msg}");
            }

            if (refrBaseTypeMismatches.Count > MaxDuplicateExamples)
            {
                report.AppendLine($"  …and {refrBaseTypeMismatches.Count - MaxDuplicateExamples:N0} more.");
            }
        }

        if (errors == 0 && warnings == 0)
        {
            report.AppendLine(
                $"Semantic check passed: {records.Count:N0} records, {totalRefrs:N0} placed refs, " +
                "no duplicate FormIDs, all refs correctly parented + flagged.");
        }
        else
        {
            report.Insert(0,
                $"Semantic check: {errors:N0} error(s), {warnings:N0} warning(s) across " +
                $"{records.Count:N0} records.\n");
        }

        return new SemanticValidationResult(errors, warnings, report.ToString().TrimEnd());
    }

    private static uint? ReadNameFormId(ParsedMainRecord record)
    {
        var name = record.Subrecords.FirstOrDefault(s => s.Signature == "NAME" && s.Data.Length >= 4);
        return name is null ? null : BinaryPrimitives.ReadUInt32LittleEndian(name.Data);
    }

    private static uint ReadLabelFormId(GrupHeaderInfo grup)
    {
        return grup.Label.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(grup.Label) : 0u;
    }

    private static bool TryResolveRecordType(
        uint formId,
        IReadOnlyDictionary<string, HashSet<uint>>? masterFormIdsByType,
        IReadOnlyDictionary<string, HashSet<uint>> pluginFormIdsByType,
        out string recordType)
    {
        foreach (var (type, ids) in pluginFormIdsByType)
        {
            if (ids.Contains(formId))
            {
                recordType = type;
                return true;
            }
        }

        if (masterFormIdsByType is not null)
        {
            foreach (var (type, ids) in masterFormIdsByType)
            {
                if (ids.Contains(formId))
                {
                    recordType = type;
                    return true;
                }
            }
        }

        recordType = string.Empty;
        return false;
    }
}

/// <summary>
///     Result of a single <see cref="PluginSemanticValidator.Validate" /> run.
/// </summary>
public sealed record SemanticValidationResult(int ErrorCount, int WarningCount, string Report)
{
    /// <summary>True when no semantic errors or warnings were found.</summary>
    public bool IsClean => ErrorCount == 0 && WarningCount == 0;
}
