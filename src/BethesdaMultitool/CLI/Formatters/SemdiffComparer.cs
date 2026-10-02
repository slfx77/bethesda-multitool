using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.CLI.Formatters;

/// <summary>
///     The single matching and verdict engine of the semantic diff: pairs the records of two files,
///     compares each pair's header and subrecords, and returns a <see cref="SemdiffTypes.SemdiffResult" />.
///     Pure: no console, no file access. The table renderer and any structured writer read the result.
///     <para>
///         Identity rules. A FormID is not unique within every file (Xbox 360 masters split one INFO
///         into several records that share its FormID), so records are grouped by key in file order
///         and the i-th occurrence of a key in A is paired with the i-th in B; nothing is ever keyed
///         into a dictionary by raw FormID. A FormID can also be reused by a different record type
///         between builds, so a pair whose signatures differ is refused
///         (<see cref="SemdiffTypes.DiffType.SignatureMismatch" />) instead of decoding B's bytes with
///         A's schema.
///     </para>
/// </summary>
internal static class SemdiffComparer
{
    internal static SemdiffTypes.SemdiffResult Compare(
        IReadOnlyList<SemdiffTypes.ParsedRecord> recordsA,
        IReadOnlyList<SemdiffTypes.ParsedRecord> recordsB,
        SemdiffTypes.SemdiffCompareOptions options)
    {
        var entries = new List<SemdiffTypes.RecordDiff>();
        var warnings = new List<SemdiffTypes.SemdiffWarning>();

        switch (options.Match)
        {
            case SemdiffTypes.MatchMode.EditorId:
                CompareByEditorId(recordsA, recordsB, options, entries, warnings);
                break;
            case SemdiffTypes.MatchMode.ExplicitMap:
                CompareByMap(recordsA, recordsB, options, entries, warnings);
                break;
            default:
                CompareByFormId(recordsA, recordsB, options, entries, warnings);
                break;
        }

        // The --map listing keeps the order the pairs were given in; the other modes read in FormID
        // order (a stable sort, so occurrences of one FormID stay in file order).
        var ordered = options.Match == SemdiffTypes.MatchMode.ExplicitMap
            ? entries
            : entries.OrderBy(e => e.FormId).ToList();
        var listed = ordered.Where(e => options.ShowAll || IsListedByDefault(e.DiffType)).ToList();

        var different = CountVerdict(entries, SemdiffTypes.DiffType.Different);
        var signatureMismatch = CountVerdict(entries, SemdiffTypes.DiffType.SignatureMismatch);
        var onlyInA = CountVerdict(entries, SemdiffTypes.DiffType.OnlyInA);
        var onlyInB = CountVerdict(entries, SemdiffTypes.DiffType.OnlyInB);
        var ambiguous = CountVerdict(entries, SemdiffTypes.DiffType.Ambiguous);
        var formVersionOnly = CountVerdict(entries, SemdiffTypes.DiffType.FormVersionOnly);
        var nonSemantic = CountVerdict(entries, SemdiffTypes.DiffType.NonSemanticHeaderOnly);
        var identical = CountVerdict(entries, SemdiffTypes.DiffType.Identical);

        return new SemdiffTypes.SemdiffResult
        {
            Records = listed,
            Warnings = warnings,
            Summary = new SemdiffTypes.SemdiffSummary
            {
                Compared = different + signatureMismatch + formVersionOnly + nonSemantic + identical,
                WithDifferences = different + signatureMismatch + onlyInA + onlyInB + ambiguous,
                Different = different,
                SignatureMismatch = signatureMismatch,
                OnlyInA = onlyInA,
                OnlyInB = onlyInB,
                Ambiguous = ambiguous,
                FormVersionOnly = formVersionOnly,
                NonSemanticHeaderOnly = nonSemantic,
                Identical = identical,
                Listed = listed.Count
            }
        };
    }

    /// <summary>
    ///     True for the verdicts the default listing shows. <see cref="SemdiffTypes.DiffType.Identical" />,
    ///     <see cref="SemdiffTypes.DiffType.NonSemanticHeaderOnly" /> and
    ///     <see cref="SemdiffTypes.DiffType.FormVersionOnly" /> appear only under <c>--all</c>.
    /// </summary>
    internal static bool IsListedByDefault(SemdiffTypes.DiffType diffType)
    {
        return diffType is not (SemdiffTypes.DiffType.Identical or SemdiffTypes.DiffType.NonSemanticHeaderOnly
            or SemdiffTypes.DiffType.FormVersionOnly);
    }

    /// <summary>
    ///     Where a record's EditorID lives in the other file, for a record the FormID did not pair
    ///     with a same-signature record (a signature mismatch, or a record on one side only).
    ///     <paramref name="findInA" /> and <paramref name="findInB" /> look up
    ///     (signature, EditorID) in that file. Null when neither side has an EditorID to look up.
    /// </summary>
    internal static string? BuildEditorIdHint(SemdiffTypes.RecordDiff diff,
        Func<string, string, IReadOnlyList<SemdiffTypes.ParsedRecord>> findInA,
        Func<string, string, IReadOnlyList<SemdiffTypes.ParsedRecord>> findInB,
        string labelA, string labelB)
    {
        if (diff.DiffType is not (SemdiffTypes.DiffType.SignatureMismatch or SemdiffTypes.DiffType.OnlyInA
            or SemdiffTypes.DiffType.OnlyInB))
        {
            return null;
        }

        var parts = new List<string>();
        if (diff.RecordA is { } a && diff.EditorIdA is { } editorIdA)
        {
            parts.Add(DescribeEditorIdLookup(labelB, a.Type, editorIdA, findInB(a.Type, editorIdA)));
        }

        if (diff.RecordB is { } b && diff.EditorIdB is { } editorIdB)
        {
            parts.Add(DescribeEditorIdLookup(labelA, b.Type, editorIdB, findInA(b.Type, editorIdB)));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    internal static string DescribeEditorIdLookup(string otherLabel, string signature, string editorId,
        IReadOnlyList<SemdiffTypes.ParsedRecord> matches)
    {
        if (matches.Count == 0)
        {
            return $"{otherLabel} has no {signature} with EditorID {editorId}";
        }

        return $"{otherLabel} has {signature} {editorId} at " +
               string.Join(", ", matches.Select(m => $"0x{m.FormId:X8}"));
    }

    private static void CompareByFormId(
        IReadOnlyList<SemdiffTypes.ParsedRecord> recordsA,
        IReadOnlyList<SemdiffTypes.ParsedRecord> recordsB,
        SemdiffTypes.SemdiffCompareOptions options,
        List<SemdiffTypes.RecordDiff> entries,
        List<SemdiffTypes.SemdiffWarning> warnings)
    {
        var groupsA = GroupByFormId(recordsA);
        var groupsB = GroupByFormId(recordsB);

        foreach (var formId in groupsA.Keys.Union(groupsB.Keys).OrderBy(id => id))
        {
            var listA = Occurrences(groupsA, formId);
            var listB = Occurrences(groupsB, formId);
            WarnIfRepeated(listA, SemdiffTypes.SemdiffSide.A, options.LabelA, warnings);
            WarnIfRepeated(listB, SemdiffTypes.SemdiffSide.B, options.LabelB, warnings);
            PairOccurrences(listA, listB, SemdiffTypes.MatchedBy.FormId, options, entries, warnings);
        }
    }

    private static void CompareByEditorId(
        IReadOnlyList<SemdiffTypes.ParsedRecord> recordsA,
        IReadOnlyList<SemdiffTypes.ParsedRecord> recordsB,
        SemdiffTypes.SemdiffCompareOptions options,
        List<SemdiffTypes.RecordDiff> entries,
        List<SemdiffTypes.SemdiffWarning> warnings)
    {
        var groupsA = GroupByIdentity(recordsA);
        var groupsB = GroupByIdentity(recordsB);

        foreach (var key in groupsA.Keys.Union(groupsB.Keys))
        {
            var listA = Occurrences(groupsA, key);
            var listB = Occurrences(groupsB, key);

            if (key.EditorId == null)
            {
                // No EditorID on this side: fall back to (signature, FormID). A repeat here is the
                // split-INFO shape, not an EditorID collision, so it pairs by occurrence like FormID mode.
                WarnIfRepeated(listA, SemdiffTypes.SemdiffSide.A, options.LabelA, warnings);
                WarnIfRepeated(listB, SemdiffTypes.SemdiffSide.B, options.LabelB, warnings);
                PairOccurrences(listA, listB, SemdiffTypes.MatchedBy.FormIdFallback, options, entries, warnings);
                continue;
            }

            if (listA.Count > 1 || listB.Count > 1)
            {
                // Two records claim one EditorID: which of them is "the" record cannot be decided from
                // the key, so report every candidate and pair nothing.
                var first = listA.Count > 0 ? listA[0] : listB[0];
                entries.Add(new SemdiffTypes.RecordDiff(first.FormId, key.Type, SemdiffTypes.DiffType.Ambiguous,
                    null, null)
                {
                    FormIdB = listB.Count > 0 ? listB[0].FormId : first.FormId,
                    MatchedBy = SemdiffTypes.MatchedBy.EditorId,
                    EditorIdA = listA.Count > 0 ? listA[0].EditorId : null,
                    EditorIdB = listB.Count > 0 ? listB[0].EditorId : null,
                    AmbiguousFormIdsA = listA.Select(r => r.FormId).ToList(),
                    AmbiguousFormIdsB = listB.Select(r => r.FormId).ToList()
                });
                continue;
            }

            PairOccurrences(listA, listB, SemdiffTypes.MatchedBy.EditorId, options, entries, warnings);
        }
    }

    private static void CompareByMap(
        IReadOnlyList<SemdiffTypes.ParsedRecord> recordsA,
        IReadOnlyList<SemdiffTypes.ParsedRecord> recordsB,
        SemdiffTypes.SemdiffCompareOptions options,
        List<SemdiffTypes.RecordDiff> entries,
        List<SemdiffTypes.SemdiffWarning> warnings)
    {
        var groupsA = GroupByFormId(recordsA);
        var groupsB = GroupByFormId(recordsB);

        foreach (var mapping in options.ExplicitPairs)
        {
            var listA = Occurrences(groupsA, mapping.FormIdA);
            var listB = Occurrences(groupsB, mapping.FormIdB);
            var mapText = $"--map 0x{mapping.FormIdA:X8}=0x{mapping.FormIdB:X8}";

            if (listA.Count == 0)
            {
                warnings.Add(new SemdiffTypes.SemdiffWarning("map-formid-not-found", SemdiffTypes.SemdiffSide.A,
                    mapping.FormIdA, $"{mapText}: FormID 0x{mapping.FormIdA:X8} is not in {options.LabelA}"));
            }

            if (listB.Count == 0)
            {
                warnings.Add(new SemdiffTypes.SemdiffWarning("map-formid-not-found", SemdiffTypes.SemdiffSide.B,
                    mapping.FormIdB, $"{mapText}: FormID 0x{mapping.FormIdB:X8} is not in {options.LabelB}"));
            }

            WarnIfRepeated(listA, SemdiffTypes.SemdiffSide.A, options.LabelA, warnings);
            WarnIfRepeated(listB, SemdiffTypes.SemdiffSide.B, options.LabelB, warnings);

            var firstEntry = entries.Count;
            PairOccurrences(listA, listB, SemdiffTypes.MatchedBy.ExplicitMap, options, entries, warnings);

            // Every entry of a mapping names both sides of the mapping, including a side that is absent.
            for (var i = firstEntry; i < entries.Count; i++)
            {
                entries[i] = entries[i] with { FormId = mapping.FormIdA, FormIdB = mapping.FormIdB };
            }
        }
    }

    private static void PairOccurrences(
        IReadOnlyList<SemdiffTypes.ParsedRecord> listA,
        IReadOnlyList<SemdiffTypes.ParsedRecord> listB,
        SemdiffTypes.MatchedBy matchedBy,
        SemdiffTypes.SemdiffCompareOptions options,
        List<SemdiffTypes.RecordDiff> entries,
        List<SemdiffTypes.SemdiffWarning> warnings)
    {
        var count = Math.Max(listA.Count, listB.Count);
        for (var i = 0; i < count; i++)
        {
            var a = i < listA.Count ? listA[i] : null;
            var b = i < listB.Count ? listB[i] : null;
            entries.Add(BuildEntry(a, b, i, matchedBy, options, warnings));
        }
    }

    private static SemdiffTypes.RecordDiff BuildEntry(
        SemdiffTypes.ParsedRecord? a,
        SemdiffTypes.ParsedRecord? b,
        int occurrence,
        SemdiffTypes.MatchedBy matchedBy,
        SemdiffTypes.SemdiffCompareOptions options,
        List<SemdiffTypes.SemdiffWarning> warnings)
    {
        if (b == null)
        {
            var onlyA = a ?? throw new ArgumentException("At least one side of a pair must be present.");
            return new SemdiffTypes.RecordDiff(onlyA.FormId, onlyA.Type, SemdiffTypes.DiffType.OnlyInA, onlyA, null)
            {
                MatchedBy = matchedBy,
                EditorIdA = onlyA.EditorId,
                OccurrenceA = occurrence,
                FlagNamesA = DescribeFlags(options.GameA, onlyA)
            };
        }

        if (a == null)
        {
            return new SemdiffTypes.RecordDiff(b.FormId, b.Type, SemdiffTypes.DiffType.OnlyInB, null, b)
            {
                MatchedBy = matchedBy,
                EditorIdB = b.EditorId,
                OccurrenceB = occurrence,
                FlagNamesB = DescribeFlags(options.GameB, b)
            };
        }

        var editorIdA = a.EditorId;
        var editorIdB = b.EditorId;

        if (!string.Equals(a.Type, b.Type, StringComparison.Ordinal))
        {
            // The FormID was reused by another record type. B's bytes mean nothing under A's schema,
            // so no typed comparison is attempted; each side keeps only its own facts.
            return new SemdiffTypes.RecordDiff(a.FormId, a.Type, SemdiffTypes.DiffType.SignatureMismatch, a, b)
            {
                FormIdB = b.FormId,
                MatchedBy = matchedBy,
                EditorIdA = editorIdA,
                EditorIdB = editorIdB,
                OccurrenceA = occurrence,
                OccurrenceB = occurrence,
                FlagNamesA = DescribeFlags(options.GameA, a),
                FlagNamesB = DescribeFlags(options.GameB, b),
                InventoryA = SemdiffRecordParser.BuildSubrecordInventory(a),
                InventoryB = SemdiffRecordParser.BuildSubrecordInventory(b)
            };
        }

        // The engine resolves EditorIDs case-insensitively (as --match editorid pairs them), so a change of
        // case alone is no reason to doubt the pair; it still shows as an EDID field difference.
        if (editorIdA != null && editorIdB != null &&
            !string.Equals(editorIdA, editorIdB, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new SemdiffTypes.SemdiffWarning("editorid-differs", null, a.FormId,
                $"{a.Type} 0x{a.FormId:X8}: EditorID {editorIdA} in {options.LabelA} but {editorIdB} in " +
                $"{options.LabelB}; this pair may not be the same object (compared anyway)"));
        }

        var subrecords = SemdiffRecordParser.CompareSubrecords(a, b, options.BigEndianA, options.BigEndianB);
        var header = SemdiffRecordParser.CompareRecordHeaders(a, b, options.GameA);
        var orderNote = subrecords.OrderDivergence is { } divergence
            ? DescribeOrderDivergence(divergence, options.LabelA, options.LabelB)
            : null;

        return new SemdiffTypes.RecordDiff(a.FormId, a.Type, DecideVerdict(subrecords, header), a, b,
            subrecords.FieldDiffs)
        {
            FormIdB = b.FormId,
            MatchedBy = matchedBy,
            Header = header,
            EditorIdA = editorIdA,
            EditorIdB = editorIdB,
            OccurrenceA = occurrence,
            OccurrenceB = occurrence,
            FlagNamesA = DescribeFlags(options.GameA, a),
            FlagNamesB = DescribeFlags(options.GameB, b),
            SubrecordOrderNote = orderNote
        };
    }

    /// <summary>
    ///     The verdict of a same-signature pair: <see cref="SemdiffTypes.DiffType.Different" /> for any
    ///     subrecord difference, subrecord reordering or header flag change; otherwise the form version,
    ///     then the version-control words, decide between the three verdicts only <c>--all</c> lists.
    /// </summary>
    private static SemdiffTypes.DiffType DecideVerdict(SemdiffTypes.SubrecordComparison subrecords,
        SemdiffTypes.HeaderComparison header)
    {
        if (subrecords.FieldDiffs.Count > 0 || subrecords.OrderDivergence != null || header.HasCountedDelta)
        {
            return SemdiffTypes.DiffType.Different;
        }

        if (header.HasFormVersionDelta)
        {
            return SemdiffTypes.DiffType.FormVersionOnly;
        }

        return header.HasBookkeepingDelta
            ? SemdiffTypes.DiffType.NonSemanticHeaderOnly
            : SemdiffTypes.DiffType.Identical;
    }

    /// <summary>
    ///     For example "Same subrecords per signature, in a different order: first divergence at subrecord
    ///     index 1 (0-based), where File A has XCLR and File B has XCAS". Every signature holds the same
    ///     instances in the same order on both sides, so at the first divergence the signatures differ.
    /// </summary>
    internal static string DescribeOrderDivergence(SemdiffTypes.SubrecordOrderDivergence divergence,
        string labelA, string labelB)
    {
        return "Same subrecords per signature, in a different order: first divergence at subrecord index " +
               $"{divergence.Index} (0-based), where {labelA} has {divergence.SignatureA ?? "no subrecord"} and " +
               $"{labelB} has {divergence.SignatureB ?? "no subrecord"}";
    }

    private static IReadOnlyList<string> DescribeFlags(BethesdaGame game, SemdiffTypes.ParsedRecord record)
    {
        return RecordHeaderFlagRegistry.DescribeSetBits(game, record.Type, record.Flags);
    }

    private static void WarnIfRepeated(IReadOnlyList<SemdiffTypes.ParsedRecord> occurrences,
        SemdiffTypes.SemdiffSide side, string label, List<SemdiffTypes.SemdiffWarning> warnings)
    {
        if (occurrences.Count < 2)
        {
            return;
        }

        var formId = occurrences[0].FormId;
        var why = occurrences.All(r => r.Type == "INFO")
            ? " (Xbox 360 masters split one INFO into several records that share its FormID)"
            : "";
        warnings.Add(new SemdiffTypes.SemdiffWarning("duplicate-formid", side, formId,
            $"FormID 0x{formId:X8} occurs {occurrences.Count} times in {label}{why}; " +
            "occurrences were paired by file order"));
    }

    private static Dictionary<uint, List<SemdiffTypes.ParsedRecord>> GroupByFormId(
        IReadOnlyList<SemdiffTypes.ParsedRecord> records)
    {
        return records
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Offset).ToList());
    }

    private static Dictionary<IdentityKey, List<SemdiffTypes.ParsedRecord>> GroupByIdentity(
        IReadOnlyList<SemdiffTypes.ParsedRecord> records)
    {
        return records
            .GroupBy(IdentityKey.For)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Offset).ToList());
    }

    private static IReadOnlyList<SemdiffTypes.ParsedRecord> Occurrences<TKey>(
        Dictionary<TKey, List<SemdiffTypes.ParsedRecord>> groups, TKey key)
        where TKey : notnull
    {
        if (groups.TryGetValue(key, out var list))
        {
            return list;
        }

        return [];
    }

    private static int CountVerdict(List<SemdiffTypes.RecordDiff> entries, SemdiffTypes.DiffType diffType)
    {
        return entries.Count(e => e.DiffType == diffType);
    }

    /// <summary>
    ///     A record's identity under <c>--match editorid</c>: its signature plus its EditorID
    ///     (upper-cased, because the engine resolves EditorIDs case-insensitively), or, for a record
    ///     without one, its signature plus its FormID.
    /// </summary>
    private readonly record struct IdentityKey(string Type, string? EditorId, uint FormId)
    {
        public static IdentityKey For(SemdiffTypes.ParsedRecord record)
        {
            return record.EditorId is { } editorId
                ? new IdentityKey(record.Type, editorId.ToUpperInvariant(), 0)
                : new IdentityKey(record.Type, null, record.FormId);
        }
    }
}
