using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Replaces dangling FormID references with <c>0x00000000</c> (engine null) so the
///     runtime doesn't null-deref while binding cross-record links during load.
///
///     A FormID is "dangling" when it isn't in the master ESM's record set AND isn't one of
///     the FormIDs this conversion has allocated for new records. The 0/0xFFFFFFFF sentinels
///     are pass-through (the engine handles those).
///
///     The validator aggregates substitution counts and emits one summary warning per
///     <see cref="EmitSummary" /> call, so a few thousand dangling refs don't drown the log
///     with per-substitution noise.
/// </summary>
internal sealed class FormIdValidator
{
    private readonly HashSet<uint> _knownFormIds;
    private readonly Dictionary<string, int> _substitutionsByContext = new(StringComparer.Ordinal);

    public FormIdValidator(IEnumerable<uint> masterFormIds, IEnumerable<uint> newFormIds)
    {
        _knownFormIds = [.. masterFormIds];
        foreach (var id in newFormIds)
        {
            _knownFormIds.Add(id);
        }
    }

    public int TotalSubstitutions { get; private set; }

    public uint Validate(uint formId, string context)
    {
        if (formId == 0 || formId == 0xFFFFFFFFu || _knownFormIds.Contains(formId))
        {
            return formId;
        }

        TotalSubstitutions++;
        _substitutionsByContext.TryGetValue(context, out var count);
        _substitutionsByContext[context] = count + 1;
        return 0;
    }

    public uint? Validate(uint? formId, string context)
    {
        if (!formId.HasValue)
        {
            return null;
        }

        var value = formId.Value;
        if (value == 0 || _knownFormIds.Contains(value))
        {
            return formId;
        }

        if (value == 0xFFFFFFFFu)
        {
            return null;
        }

        TotalSubstitutions++;
        _substitutionsByContext.TryGetValue(context, out var count);
        _substitutionsByContext[context] = count + 1;
        return null;
    }

    /// <summary>
    ///     Validate a list of FormIDs. Dangling entries are dropped from the result (the
    ///     subrecords they would emit are skipped). Returns the original list if every entry
    ///     was valid.
    /// </summary>
    public List<uint> ValidateList(IReadOnlyList<uint> formIds, string context)
    {
        if (formIds.Count == 0)
        {
            return [];
        }

        var result = new List<uint>(formIds.Count);
        foreach (var id in formIds)
        {
            if (id == 0 || _knownFormIds.Contains(id))
            {
                result.Add(id);
                continue;
            }

            TotalSubstitutions++;
            _substitutionsByContext.TryGetValue(context, out var count);
            _substitutionsByContext[context] = count + 1;
        }

        return result;
    }

    public void EmitSummary(IConversionProgressSink sink, string phase)
    {
        if (TotalSubstitutions == 0)
        {
            return;
        }

        var breakdown = string.Join(", ",
            _substitutionsByContext
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}"));

        sink.Warn(phase,
            $"Replaced {TotalSubstitutions} dangling FormID reference(s) with null. Breakdown: {breakdown}. " +
            "Cause: source DMP references records that don't exist in declared masters " +
            "(typical when converting an FO3 prototype DMP against an FNV master ESM).",
            code: "v20.formid.placeholder-summary");
    }
}
