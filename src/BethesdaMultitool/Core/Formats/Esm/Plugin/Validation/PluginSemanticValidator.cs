using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Validation;

/// <summary>
///     Post-emit semantic validation that catches the same structural-but-not-byte-corrupt
///     issues FNVEdit's "Check for Errors" flags. Each finding is a problem the byte-level
///     <see cref="PluginRoundTripValidator" /> doesn't notice — duplicate FormIDs, REFRs
///     in the wrong cell-children GRUP type for their persistent flag, dangling base
///     FormIDs in NAME subrecords, dangling or mistyped script references, etc.
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
        IReadOnlyDictionary<string, HashSet<uint>>? masterFormIdsByType = null,
        IReadOnlySet<uint>? additionalValidFormIds = null,
        IReadOnlyDictionary<string, uint>? masterScriptFormIdsByEditorId = null)
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
        var duplicateScriptEditorIds = FindDuplicateEffectiveScriptEditorIds(
            records, masterScriptFormIdsByEditorId);
        var conditionFunctionsAbsentFromFnvCallbackTable =
            FindConditionFunctionsAbsentFromFnvCallbackTable(records);

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
        var scriptDangling = new List<string>();
        var scriptTypeMismatches = new List<string>();
        var totalRefrs = 0;
        var totalScriptReferences = 0;

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

            foreach (var scriptId in ReadFormIds(record, "SCRI"))
            {
                if (scriptId is 0 or 0xFFFFFFFFu)
                {
                    continue;
                }

                totalScriptReferences++;
                if (ContainsTypedFormId(pluginFormIdsByType, "SCPT", scriptId)
                    || ContainsTypedFormId(masterFormIdsByType, "SCPT", scriptId))
                {
                    continue;
                }

                if (TryResolveRecordType(
                        scriptId, masterFormIdsByType, pluginFormIdsByType, out var scriptRecordType))
                {
                    scriptTypeMismatches.Add(
                        $"{signature} 0x{formId:X8} SCRI FormID 0x{scriptId:X8} resolves to " +
                        $"{scriptRecordType}, expected SCPT.");
                }
                else if (masterFormIdsByType is not null
                         || (masterFormIds is not null && !masterFormIds.Contains(scriptId)))
                {
                    scriptDangling.Add(
                        $"{signature} 0x{formId:X8} SCRI FormID 0x{scriptId:X8} is " +
                        "neither an SCPT in master nor freshly emitted by this plugin.");
                }
            }

            if (signature is "REFR" or "ACHR" or "ACRE" or "PGRE")
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
                         // Engine-hardcoded forms (FormIDs below 0x800: PlayerRef, marker/default
                         // objects, etc.) live in the game EXE, not any ESM, so they're legitimately
                         // absent from master ∪ plugin — don't flag a ref that points at one.
                         && baseId.Value >= 0x800u
                         && !masterFormIds.Contains(baseId.Value) && !pluginFormIds.Contains(baseId.Value))
                {
                    refrBaseDangling.Add(
                        $"{signature} 0x{formId:X8} base FormID 0x{baseId.Value:X8} is " +
                        "neither in master nor freshly emitted by this plugin.");
                }
            }
        }

        var packageSemantic = PackageSemanticValidator.Validate(
            records, pluginFormIdsByType, masterFormIdsByType);
        var scriptSemantic = ScriptReferenceSemanticValidator.Validate(
            records, pluginFormIds, masterFormIds, additionalValidFormIds);

        var report = new StringBuilder();
        var errors = 0;
        var warnings = 0;

        // TES4 HEDR must declare every record AND GRUP header except TES4 itself — verified
        // against shipped FalloutNV.esm (542,016 = 465,016 records + 77,000 groups). This
        // shipped 35% low for years because the field was fed from a per-write-site counter;
        // it is now derived from the assembled bytes, and this rule keeps it that way.
        var declaredHedrCount = TryReadHedrRecordCount(espBytes);
        var structuralRecordCount = records.Count(static r => r.Header.Signature != "TES4")
                                    + grupHeaders.Count;
        if (declaredHedrCount is { } declared && declared != structuralRecordCount)
        {
            warnings++;
            report.AppendLine(
                $"WARN: TES4 HEDR record count is {declared:N0} but the file structurally " +
                $"contains {structuralRecordCount:N0} (records + GRUPs, excluding TES4).");
        }

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

        if (duplicateScriptEditorIds.Count > 0)
        {
            errors += duplicateScriptEditorIds.Count;
            report.AppendLine(
                $"ERROR: {duplicateScriptEditorIds.Count:N0} duplicate effective SCPT EditorID(s) found:");
            foreach (var duplicate in duplicateScriptEditorIds.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {duplicate}");
            }

            if (duplicateScriptEditorIds.Count > MaxDuplicateExamples)
            {
                report.AppendLine(
                    $"  …and {duplicateScriptEditorIds.Count - MaxDuplicateExamples:N0} more.");
            }
        }

        if (conditionFunctionsAbsentFromFnvCallbackTable.Count > 0)
        {
            errors += conditionFunctionsAbsentFromFnvCallbackTable.Count;
            report.AppendLine(
                $"ERROR: {conditionFunctionsAbsentFromFnvCallbackTable.Count:N0} CTDA/CTDT subrecord(s) use a function " +
                "absent from the exact retail FNV condition-callback table:");
            foreach (var unknown in conditionFunctionsAbsentFromFnvCallbackTable.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {unknown}");
            }

            if (conditionFunctionsAbsentFromFnvCallbackTable.Count > MaxDuplicateExamples)
            {
                report.AppendLine(
                    $"  …and {conditionFunctionsAbsentFromFnvCallbackTable.Count - MaxDuplicateExamples:N0} more.");
            }
        }

        if (scriptDangling.Count > 0)
        {
            errors += scriptDangling.Count;
            report.AppendLine($"ERROR: {scriptDangling.Count:N0} record(s) with dangling SCRI FormID:");
            foreach (var msg in scriptDangling.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {msg}");
            }

            if (scriptDangling.Count > MaxDuplicateExamples)
            {
                report.AppendLine($"  …and {scriptDangling.Count - MaxDuplicateExamples:N0} more.");
            }
        }

        if (scriptTypeMismatches.Count > 0)
        {
            errors += scriptTypeMismatches.Count;
            report.AppendLine(
                $"ERROR: {scriptTypeMismatches.Count:N0} record(s) whose SCRI target is not SCPT:");
            foreach (var msg in scriptTypeMismatches.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {msg}");
            }

            if (scriptTypeMismatches.Count > MaxDuplicateExamples)
            {
                report.AppendLine($"  …and {scriptTypeMismatches.Count - MaxDuplicateExamples:N0} more.");
            }
        }

        if (scriptSemantic.Errors.Length > 0)
        {
            errors += scriptSemantic.Errors.Length;
            report.AppendLine(
                $"ERROR: {scriptSemantic.Errors.Length:N0} invalid SCPT SCRO/SCRV reference-table issue(s):");
            foreach (var msg in scriptSemantic.Errors.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {msg}");
            }

            if (scriptSemantic.Errors.Length > MaxDuplicateExamples)
            {
                report.AppendLine($"  …and {scriptSemantic.Errors.Length - MaxDuplicateExamples:N0} more.");
            }
        }

        if (packageSemantic.Errors.Length > 0)
        {
            errors += packageSemantic.Errors.Length;
            report.AppendLine(
                $"ERROR: {packageSemantic.Errors.Length:N0} invalid PACK target/location or actor PKID reference(s):");
            foreach (var msg in packageSemantic.Errors.Take(MaxDuplicateExamples))
            {
                report.AppendLine($"  {msg}");
            }

            if (packageSemantic.Errors.Length > MaxDuplicateExamples)
            {
                report.AppendLine(
                    $"  …and {packageSemantic.Errors.Length - MaxDuplicateExamples:N0} more.");
            }
        }

        if (errors == 0 && warnings == 0)
        {
            report.AppendLine(
                $"Semantic check passed: {records.Count:N0} records, {totalRefrs:N0} placed refs, " +
                $"{totalScriptReferences:N0} script refs, " +
                $"{scriptSemantic.ReferenceCount:N0} SCPT object/variable refs, " +
                $"{packageSemantic.PackageReferenceCount:N0} package target/location refs, " +
                $"{packageSemantic.ActorPackageCount:N0} actor package refs; no duplicate FormIDs, " +
                "no duplicate effective SCPT EditorIDs, all refs correctly parented + flagged, " +
                "all SCRI/PACK/PKID targets resolve with " +
                "the expected record type.");
        }
        else
        {
            report.Insert(0,
                $"Semantic check: {errors:N0} error(s), {warnings:N0} warning(s) across " +
                $"{records.Count:N0} records.\n");
        }

        return new SemanticValidationResult(errors, warnings, report.ToString().TrimEnd());
    }

    /// <summary>
    ///     Read TES4's HEDR <c>numRecords</c> (data offset +4) straight from the bytes.
    ///     Returns null when the header is absent or malformed — a file that can't present a
    ///     HEDR has bigger problems than a count mismatch, reported by the other rules.
    /// </summary>
    private static uint? TryReadHedrRecordCount(byte[] espBytes)
    {
        if (espBytes.Length < EsmParser.MainRecordHeaderSize
            || Encoding.ASCII.GetString(espBytes, 0, 4) != "TES4")
        {
            return null;
        }

        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(espBytes.AsSpan(4, 4));
        var pos = EsmParser.MainRecordHeaderSize;
        var end = Math.Min(espBytes.Length, pos + (int)dataSize);
        while (pos + 6 <= end)
        {
            var signature = Encoding.ASCII.GetString(espBytes, pos, 4);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(espBytes.AsSpan(pos + 4, 2));
            if (signature == "HEDR" && length >= 8 && pos + 6 + length <= end)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(espBytes.AsSpan(pos + 10, 4));
            }

            pos += 6 + length;
        }

        return null;
    }

    private static List<string> FindDuplicateEffectiveScriptEditorIds(
        IReadOnlyList<ParsedMainRecord> pluginRecords,
        IReadOnlyDictionary<string, uint>? masterScriptFormIdsByEditorId)
    {
        var effectiveFormIdsByEditorId =
            new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        var masterEditorIdByFormId = new Dictionary<uint, string>();

        if (masterScriptFormIdsByEditorId is not null)
        {
            foreach (var (editorId, formId) in masterScriptFormIdsByEditorId)
            {
                Add(editorId, formId);
                masterEditorIdByFormId.TryAdd(formId, editorId);
            }
        }

        foreach (var record in pluginRecords.Where(static record => record.Header.Signature == "SCPT"))
        {
            var formId = record.Header.FormId;

            // A plugin SCPT at a master FormID replaces that master's effective EditorID.
            // Remove the old name before adding the fully-merged plugin record's name so
            // a legitimate rename is not misreported as a load-order duplicate.
            if (masterEditorIdByFormId.TryGetValue(formId, out var masterEditorId)
                && effectiveFormIdsByEditorId.TryGetValue(masterEditorId, out var oldFormIds))
            {
                oldFormIds.Remove(formId);
                if (oldFormIds.Count == 0)
                {
                    effectiveFormIdsByEditorId.Remove(masterEditorId);
                }
            }

            if (!string.IsNullOrWhiteSpace(record.EditorId))
            {
                Add(record.EditorId, formId);
            }
        }

        return effectiveFormIdsByEditorId
            .Where(static pair => pair.Value.Count > 1)
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair =>
                $"'{pair.Key}' resolves to {string.Join(", ", pair.Value.Order().Select(static id => $"0x{id:X8}"))}")
            .ToList();

        void Add(string editorId, uint formId)
        {
            if (!effectiveFormIdsByEditorId.TryGetValue(editorId, out var formIds))
            {
                formIds = [];
                effectiveFormIdsByEditorId.Add(editorId, formIds);
            }

            formIds.Add(formId);
        }
    }

    private static List<string> FindConditionFunctionsAbsentFromFnvCallbackTable(
        IReadOnlyList<ParsedMainRecord> records)
    {
        var findings = new List<string>();
        foreach (var record in records)
        {
            var conditionOrdinal = 0;
            foreach (var subrecord in record.Subrecords)
            {
                if (subrecord.Signature is not ("CTDA" or "CTDT"))
                {
                    continue;
                }

                conditionOrdinal++;
                if (subrecord.Data.Length < 10)
                {
                    // Byte-structure validation reports truncated subrecords separately.
                    continue;
                }

                var functionIndex = BinaryPrimitives.ReadUInt16LittleEndian(
                    subrecord.Data.AsSpan(8, 2));
                if (PerkConditionParameterResolver.IsKnownConditionFunction(functionIndex))
                {
                    continue;
                }

                findings.Add(
                    $"{record.Header.Signature} 0x{record.Header.FormId:X8} condition " +
                    $"#{conditionOrdinal} uses function 0x{functionIndex:X4}");
            }
        }

        return findings;
    }

    private static uint? ReadNameFormId(ParsedMainRecord record)
    {
        var name = record.Subrecords.FirstOrDefault(s => s.Signature == "NAME" && s.Data.Length >= 4);
        return name is null ? null : BinaryPrimitives.ReadUInt32LittleEndian(name.Data);
    }

    private static IEnumerable<uint> ReadFormIds(ParsedMainRecord record, string signature)
    {
        foreach (var subrecord in record.Subrecords)
        {
            if (subrecord.Signature == signature && subrecord.Data.Length >= sizeof(uint))
            {
                yield return BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data);
            }
        }
    }

    private static bool ContainsTypedFormId(
        IReadOnlyDictionary<string, HashSet<uint>>? formIdsByType,
        string signature,
        uint formId)
    {
        return formIdsByType is not null
               && formIdsByType.TryGetValue(signature, out var formIds)
               && formIds.Contains(formId);
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
