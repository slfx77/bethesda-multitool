using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Diffs two <see cref="RecordCollection" /> instances loaded from the
///     same build via different formats (ESM vs DMP) and reports per-record-type,
///     per-field parity. Pure function — no I/O — unit-testable with fabricated
///     collections.
/// </summary>
internal static class ParityAuditCore
{
    /// <summary>Up to N example tuples retained per (type, field, status) bucket.</summary>
    public const int DefaultExamplesPerField = 5;

    public static ParityAuditResult Compare(
        string esmLabel,
        RecordCollection esmRecords,
        FormIdResolver esmResolver,
        string dmpLabel,
        RecordCollection dmpRecords,
        FormIdResolver dmpResolver,
        int examplesPerField = DefaultExamplesPerField)
    {
        var esmByType = GroupFlatten(esmRecords, esmResolver);
        var dmpByType = GroupFlatten(dmpRecords, dmpResolver);

        var allTypes = esmByType.Keys
            .Union(dmpByType.Keys, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var typeResults = new List<RecordTypeParity>(allTypes.Count);
        foreach (var typeName in allTypes)
        {
            esmByType.TryGetValue(typeName, out var esmRecs);
            dmpByType.TryGetValue(typeName, out var dmpRecs);
            esmRecs ??= new Dictionary<uint, Dictionary<string, string>>();
            dmpRecs ??= new Dictionary<uint, Dictionary<string, string>>();
            typeResults.Add(CompareType(typeName, esmRecs, dmpRecs, examplesPerField));
        }

        return new ParityAuditResult
        {
            EsmLabel = esmLabel,
            DmpLabel = dmpLabel,
            RecordTypes = typeResults
        };
    }

    private static Dictionary<string, Dictionary<uint, Dictionary<string, string>>> GroupFlatten(
        RecordCollection records,
        FormIdResolver resolver)
    {
        var result = new Dictionary<string, Dictionary<uint, Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var (typeName, formId, _, _, fields) in RecordFieldFlattener.FlattenAll(records, resolver))
        {
            if (!result.TryGetValue(typeName, out var byForm))
            {
                byForm = new Dictionary<uint, Dictionary<string, string>>();
                result[typeName] = byForm;
            }

            byForm[formId] = fields;
        }

        return result;
    }

    private static RecordTypeParity CompareType(
        string typeName,
        Dictionary<uint, Dictionary<string, string>> esmRecs,
        Dictionary<uint, Dictionary<string, string>> dmpRecs,
        int examplesPerField)
    {
        var matched = new List<uint>();
        foreach (var formId in esmRecs.Keys)
        {
            if (dmpRecs.ContainsKey(formId))
            {
                matched.Add(formId);
            }
        }

        var esmOnlyForms = esmRecs.Count - matched.Count;
        var dmpOnlyForms = dmpRecs.Count - matched.Count;

        var fieldStats = new Dictionary<string, FieldStats>(StringComparer.Ordinal);
        foreach (var formId in matched)
        {
            var esmFields = esmRecs[formId];
            var dmpFields = dmpRecs[formId];

            // Iterate the union of keys from both sides so missing-key cases
            // are captured (a field present only on one side counts as
            // EsmOnly or DmpOnly even if the other side has no entry at all).
            foreach (var fieldName in EnumerateUnion(esmFields, dmpFields))
            {
                esmFields.TryGetValue(fieldName, out var esmRaw);
                dmpFields.TryGetValue(fieldName, out var dmpRaw);
                var esmValue = esmRaw ?? "";
                var dmpValue = dmpRaw ?? "";

                var esmEmpty = IsDefaultLike(esmValue);
                var dmpEmpty = IsDefaultLike(dmpValue);

                if (esmEmpty && dmpEmpty)
                {
                    continue;
                }

                if (!fieldStats.TryGetValue(fieldName, out var stats))
                {
                    stats = new FieldStats();
                    fieldStats[fieldName] = stats;
                }

                FieldStatus? exampleStatus = null;
                if (esmEmpty)
                {
                    stats.DmpOnly++;
                    exampleStatus = FieldStatus.DmpOnly;
                }
                else if (dmpEmpty)
                {
                    stats.EsmOnly++;
                    exampleStatus = FieldStatus.EsmOnly;
                }
                else if (string.Equals(esmValue, dmpValue, StringComparison.Ordinal))
                {
                    stats.Agree++;
                }
                else
                {
                    stats.Disagree++;
                    exampleStatus = FieldStatus.Disagree;
                }

                if (exampleStatus is { } status && stats.Examples.Count < examplesPerField)
                {
                    stats.Examples.Add(new FieldExample(formId, esmValue, dmpValue, status));
                }
            }
        }

        var fields = fieldStats
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new FieldParity
            {
                FieldName = kv.Key,
                EsmOnly = kv.Value.EsmOnly,
                DmpOnly = kv.Value.DmpOnly,
                Agree = kv.Value.Agree,
                Disagree = kv.Value.Disagree,
                Examples = kv.Value.Examples
            })
            .ToList();

        return new RecordTypeParity
        {
            TypeName = typeName,
            EsmRecordCount = esmRecs.Count,
            DmpRecordCount = dmpRecs.Count,
            MatchedRecordCount = matched.Count,
            EsmOnlyRecordCount = esmOnlyForms,
            DmpOnlyRecordCount = dmpOnlyForms,
            Fields = fields
        };
    }

    private static IEnumerable<string> EnumerateUnion(
        Dictionary<string, string> a,
        Dictionary<string, string> b)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in a.Keys)
        {
            if (seen.Add(key))
            {
                yield return key;
            }
        }

        foreach (var key in b.Keys)
        {
            if (seen.Add(key))
            {
                yield return key;
            }
        }
    }

    /// <summary>
    ///     Recognizes the string forms that the flatten functions emit when
    ///     the underlying field is unset or zero. Both sides being default-like
    ///     means the audit has no signal — the field is skipped entirely so
    ///     all-zero fields don't bloat the report.
    /// </summary>
    internal static bool IsDefaultLike(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        switch (value)
        {
            case "0":
            case "0.0":
            case "0.00":
            case "0.000":
            case "0.0000":
            case "False":
            case "None":
            case "-":
            case "0x00000000":
                return true;
            default:
                return false;
        }
    }

    private sealed class FieldStats
    {
        public int Agree;
        public int Disagree;
        public int DmpOnly;
        public int EsmOnly;
        public List<FieldExample> Examples { get; } = [];
    }
}
