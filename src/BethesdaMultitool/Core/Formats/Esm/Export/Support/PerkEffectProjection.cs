using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Support;

internal static class PerkEffectProjection
{
    internal static IEnumerable<KeyValuePair<string, string>> Fields(PerkEntry entry, FormIdResolver? resolver = null)
    {
        yield return Pair("Rank", entry.Rank);
        yield return Pair("Priority", entry.Priority);
        yield return new("Type", entry.TypeName);
        if (entry.QuestFormId is { } quest) { yield return new("Quest", resolver?.FormatWithEditorId(quest) ?? $"0x{quest:X8}"); }
        if (entry.QuestStage is { } stage) { yield return Pair("Quest Stage", stage); }
        if (entry.AbilityFormId is { } ability) { yield return new("Ability", resolver?.FormatWithEditorId(ability) ?? $"0x{ability:X8}"); }
        if (entry.EntryPoint is { } point) { yield return Pair("Entry Point", point); }
        if (entry.EntryPointFunction is { } function) { yield return Pair("Entry Point Function", function); }
        if (entry.FunctionType is { } functionType) { yield return Pair("Function Type (EPFT)", functionType); }
        if (entry.PerkConditionTabCount is { } tabs) { yield return Pair("Condition Tabs", tabs); }
        if (entry.EffectValue is { } value) { yield return new("Value", value.ToString("R", CultureInfo.InvariantCulture)); }
        if (entry.EffectValue2 is { } value2) { yield return new("Value 2", value2.ToString("R", CultureInfo.InvariantCulture)); }
        if (entry.ActivationLabel is { } label) { yield return new("Activation Label", label); }
        if (entry.ActivationFlags is { } flags) { yield return Pair("Activation Flags", flags); }
        if (entry.ActivationScript is { } script)
        {
            if (script.SourceText is { } source) { yield return new("Activation Script Source", source); }
            if (script.DecompiledText is { } decompiled) { yield return new("Activation Script Decompiled", decompiled); }
            if (script.CompiledData is { } compiled) { yield return new("Activation Script Bytecode", Convert.ToHexString(compiled)); }
            if (script.WithheldSourceReason is { } reason) { yield return new("Activation Script Source Status", reason); }
            if (script.IsIncompleteExecutableBundle) { yield return new("Activation Script Status", "Incomplete executable bundle"); }
        }
        if (entry.RuntimeAddress is { } runtimeAddress)
        {
            yield return new("Runtime Entry VA", $"0x{runtimeAddress:X8}");
            yield return new("Runtime Entry Class", entry.RuntimeClassName ?? "Unavailable");
            if (entry.RuntimeLayoutBasis is { } basis) { yield return new("Runtime Layout Basis", LayoutLabel(basis)); }
            if (entry.RuntimeLayoutStatus is { } status) { yield return new("Runtime Layout Status", LayoutStatusLabel(status)); }
            yield return new("Runtime Recovery", entry.RecoveryIssues.Count == 0 ? "Decoded captured fields" : string.Join("; ", entry.RecoveryIssues));
        }
        if (entry.RuntimeRawData is { } runtimeRaw) { yield return new("Raw Runtime Entry", Convert.ToHexString(runtimeRaw)); }
        if (entry.RuntimeFunctionAddress is { } runtimeFunction) { yield return new("Runtime Function VA", $"0x{runtimeFunction:X8}"); }
        if (entry.RuntimeFunctionClassName is { } runtimeClass) { yield return new("Runtime Function Class", runtimeClass); }
        if (entry.RuntimeFunctionData is { } runtimeData) { yield return new("Raw Runtime Function", Convert.ToHexString(runtimeData)); }
        if (entry.EffectFormId is { } form) { yield return new("Effect Form", resolver?.FormatWithEditorId(form) ?? $"0x{form:X8}"); }
        if (entry.EffectData is { } description) { yield return new("Data", description); }
        if (entry.RawEntryData is { } rawData) { yield return new("Raw DATA", Convert.ToHexString(rawData)); }
        if (entry.RawFunctionData is { } rawFunction) { yield return new("Raw EPFD", Convert.ToHexString(rawFunction)); }
    }

    internal static IEnumerable<KeyValuePair<string, string>> Conditions(PerkEntry entry)
    {
        for (var groupIndex = 0; groupIndex < entry.ConditionGroups.Count; groupIndex++)
        {
            var group = entry.ConditionGroups[groupIndex];
            yield return new($"Group [{groupIndex}] Run On", group.RunOn?.ToString(CultureInfo.InvariantCulture) ?? "Unassigned");
            if (group.RuntimeAddress is { } address) { yield return new($"Group [{groupIndex}] Runtime VA", $"0x{address:X8}"); }
            if (group.RecoveryIssues.Count > 0) { yield return new($"Group [{groupIndex}] Recovery", string.Join("; ", group.RecoveryIssues)); }
            for (var conditionIndex = 0; conditionIndex < group.Conditions.Count; conditionIndex++)
            {
                var condition = group.Conditions[conditionIndex];
                yield return new($"Group [{groupIndex}] Condition [{conditionIndex}]",
                    $"{condition.FunctionName} (#{condition.FunctionIndex}) " +
                    $"({condition.Parameter1}, {condition.Parameter2}) {condition.OperatorDisplay} " +
                    Comparison(condition));
                foreach (var field in ConditionFields(condition))
                {
                    yield return new($"Group [{groupIndex}] Condition [{conditionIndex}] {field.Key}", field.Value);
                }
            }
        }
    }

    internal static IEnumerable<KeyValuePair<string, string>> ConditionFields(PerkCondition condition)
    {
        if (condition.Flags is { } flags) { yield return Pair("Flags", flags); }
        if (condition.RunOn is { } runOn) { yield return new("Run On", runOn.ToString(CultureInfo.InvariantCulture)); }
        if (condition.ReferenceFormId is { } reference) { yield return new("Reference", $"0x{reference:X8}"); }
        if (condition.ComparisonGlobalFormId is { } global) { yield return new("Comparison Global", $"0x{global:X8}"); }
        if (condition.RuntimeAddress is { } address) { yield return new("Runtime VA", $"0x{address:X8}"); }
        if (condition.RuntimeLayoutBasis is { } basis) { yield return new("Layout Basis", LayoutLabel(basis)); }
        if (condition.RuntimeRawData is { } raw) { yield return new("Raw Runtime", Convert.ToHexString(raw)); }
        if (condition.RecoveryIssues.Count > 0) { yield return new("Recovery", string.Join("; ", condition.RecoveryIssues)); }
    }

    internal static string Comparison(PerkCondition condition) =>
        (condition.Flags.GetValueOrDefault() & 4) != 0
            ? condition.ComparisonGlobalFormId is { } global ? $"Global 0x{global:X8}" : "Global: Unavailable"
            : condition.RecoveryIssues.Any(issue => issue.StartsWith("Float at +4", StringComparison.Ordinal))
                ? "Unavailable" : condition.ComparisonValue.ToString("R", CultureInfo.InvariantCulture);

    private static KeyValuePair<string, string> Pair(string name, int value) =>
        new(name, value.ToString(CultureInfo.InvariantCulture));

    private static string LayoutLabel(string basis) => basis.StartsWith("Xbox 360 class layouts agree", StringComparison.Ordinal)
        ? "PDB layout; capture compatibility unverified" : basis;

    private static string LayoutStatusLabel(string status) =>
        status.StartsWith("Captured RTTI class matched", StringComparison.Ordinal) ? "RTTI matched" :
        status.StartsWith("RTTI class unavailable or unsupported", StringComparison.Ordinal)
            ? "RTTI unavailable; base offsets inferred" : status;
}
