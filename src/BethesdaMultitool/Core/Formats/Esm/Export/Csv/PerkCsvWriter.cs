using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Csv;

internal static class PerkCsvWriter
{
    private static readonly string[] Columns =
    [
        "RowType", "FormID", "EditorID", "Name", "Description", "Ranks", "MinLevel", "IsPlayable", "IsTrait",
        "IconPath", "Endianness", "Offset", "EntryRank", "EntryPriority", "EntryType", "EntryTypeName",
        "EntryAbilityFormID", "EntryAbilityName", "EntryAbilityDisplayName", "EntryIndex", "EntryPoint",
        "EntryPointFunction", "FunctionType", "ConditionTabCount", "QuestFormID", "QuestStage", "EffectValue",
        "EffectFormID", "EffectData", "RawEntryData", "RawFunctionData", "GroupIndex", "GroupRunOn",
        "ConditionIndex", "ConditionFunctionIndex", "ConditionFunctionName", "ConditionOperator", "ConditionValue",
        "ConditionParameter1", "ConditionParameter2", "ConditionParameter1FormID", "ConditionParameter2FormID",
        "EffectValue2", "ActivationLabel", "ActivationFlags", "ActivationScriptSource", "ActivationScriptDecompiled", "ActivationScriptBytecode",
        "RuntimeEntryVA", "RuntimeEntryClass", "RawRuntimeEntry", "RuntimeFunctionVA", "RuntimeFunctionClass", "RawRuntimeFunction", "RuntimeRecovery",
        "GroupRuntimeVA", "GroupRecovery", "ConditionFlags", "ConditionRunOn", "ConditionReference", "ConditionComparisonGlobal", "ConditionRuntimeVA", "ConditionRawRuntime", "ConditionRecovery",
        "RuntimeLayoutBasis", "RuntimeLayoutStatus", "ConditionLayoutBasis"
    ];

    internal static string Write(IEnumerable<PerkRecord> perks, FormIdResolver resolver)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join(',', Columns));
        foreach (var perk in perks.OrderBy(item => item.EditorId ?? "", StringComparer.Ordinal))
        {
            Append(text, new Dictionary<string, string?>
            {
                ["RowType"] = "PERK", ["FormID"] = Fmt.FId(perk.FormId), ["EditorID"] = perk.EditorId,
                ["Name"] = perk.FullName, ["Description"] = perk.Description, ["Ranks"] = Number(perk.Ranks),
                ["MinLevel"] = Number(perk.MinLevel), ["IsPlayable"] = perk.IsPlayable.ToString(),
                ["IsTrait"] = perk.IsTrait.ToString(), ["IconPath"] = perk.IconPath,
                ["Endianness"] = Fmt.Endian(perk.IsBigEndian), ["Offset"] = Number(perk.Offset),
                ["RuntimeRecovery"] = perk.RuntimeRecoveryIssues.Count > 0 ? string.Join("; ", perk.RuntimeRecoveryIssues) : null
            });
            for (var conditionIndex = 0; conditionIndex < perk.Conditions.Count; conditionIndex++)
            {
                var condition = perk.Conditions[conditionIndex];
                var row = new Dictionary<string, string?> { ["RowType"] = "CONDITION", ["FormID"] = Fmt.FId(perk.FormId) };
                AddCondition(row, condition, conditionIndex);
                Append(text, row);
            }
            for (var index = 0; index < perk.Entries.Count; index++)
            {
                var entry = perk.Entries[index];
                var detail = PerkEffectProjection.Fields(entry).ToDictionary(pair => pair.Key, pair => pair.Value);
                var row = new Dictionary<string, string?>
                {
                    ["RowType"] = "ENTRY", ["FormID"] = Fmt.FId(perk.FormId), ["EntryIndex"] = Number(index),
                    ["EntryRank"] = Number(entry.Rank), ["EntryPriority"] = Number(entry.Priority),
                    ["EntryType"] = Number(entry.Type), ["EntryTypeName"] = entry.TypeName,
                    ["EntryAbilityFormID"] = Fmt.FIdN(entry.AbilityFormId),
                    ["EntryAbilityName"] = entry.AbilityFormId is { } ability ? resolver.GetEditorId(ability) : null,
                    ["EntryAbilityDisplayName"] = entry.AbilityFormId is { } id ? resolver.GetDisplayName(id) : null,
                    ["EntryPoint"] = detail.GetValueOrDefault("Entry Point"),
                    ["EntryPointFunction"] = detail.GetValueOrDefault("Entry Point Function"),
                    ["FunctionType"] = detail.GetValueOrDefault("Function Type (EPFT)"),
                    ["ConditionTabCount"] = detail.GetValueOrDefault("Condition Tabs"),
                    ["QuestFormID"] = detail.GetValueOrDefault("Quest"), ["QuestStage"] = detail.GetValueOrDefault("Quest Stage"),
                    ["EffectValue"] = detail.GetValueOrDefault("Value"), ["EffectFormID"] = detail.GetValueOrDefault("Effect Form"),
                    ["EffectData"] = detail.GetValueOrDefault("Data"), ["RawEntryData"] = detail.GetValueOrDefault("Raw DATA"),
                    ["RawFunctionData"] = detail.GetValueOrDefault("Raw EPFD"),
                    ["EffectValue2"] = detail.GetValueOrDefault("Value 2"),
                    ["ActivationLabel"] = entry.ActivationLabel, ["ActivationFlags"] = detail.GetValueOrDefault("Activation Flags"),
                    ["ActivationScriptSource"] = entry.ActivationScript?.SourceText,
                    ["ActivationScriptDecompiled"] = entry.ActivationScript?.DecompiledText,
                    ["ActivationScriptBytecode"] = detail.GetValueOrDefault("Activation Script Bytecode"),
                    ["RuntimeEntryVA"] = detail.GetValueOrDefault("Runtime Entry VA"),
                    ["RuntimeEntryClass"] = detail.GetValueOrDefault("Runtime Entry Class"),
                    ["RuntimeLayoutBasis"] = entry.RuntimeLayoutBasis, ["RuntimeLayoutStatus"] = entry.RuntimeLayoutStatus,
                    ["RawRuntimeEntry"] = detail.GetValueOrDefault("Raw Runtime Entry"),
                    ["RuntimeFunctionVA"] = detail.GetValueOrDefault("Runtime Function VA"),
                    ["RuntimeFunctionClass"] = detail.GetValueOrDefault("Runtime Function Class"),
                    ["RawRuntimeFunction"] = detail.GetValueOrDefault("Raw Runtime Function"),
                    ["RuntimeRecovery"] = detail.GetValueOrDefault("Runtime Recovery")
                };
                Append(text, row);
                for (var groupIndex = 0; groupIndex < entry.ConditionGroups.Count; groupIndex++)
                {
                    var group = entry.ConditionGroups[groupIndex];
                    var groupRow = new Dictionary<string, string?>(row)
                    {
                        ["RowType"] = "ENTRY_GROUP", ["GroupIndex"] = Number(groupIndex),
                        ["GroupRunOn"] = group.RunOn?.ToString(CultureInfo.InvariantCulture),
                        ["GroupRuntimeVA"] = group.RuntimeAddress is { } groupVa ? $"0x{groupVa:X8}" : null,
                        ["GroupRecovery"] = group.RecoveryIssues.Count > 0 ? string.Join("; ", group.RecoveryIssues) : null
                    };
                    Append(text, groupRow);
                    for (var conditionIndex = 0; conditionIndex < group.Conditions.Count; conditionIndex++)
                    {
                        var condition = group.Conditions[conditionIndex];
                        var conditionRow = new Dictionary<string, string?>(groupRow)
                        {
                            ["RowType"] = "ENTRY_CONDITION"
                        };
                        AddCondition(conditionRow, condition, conditionIndex);
                        Append(text, conditionRow);
                    }
                }
            }
        }
        return text.ToString();
    }

    private static void AddCondition(Dictionary<string, string?> row, PerkCondition condition, int index)
    {
        var detail = PerkEffectProjection.ConditionFields(condition).ToDictionary(pair => pair.Key, pair => pair.Value);
        row["ConditionIndex"] = Number(index);
        row["ConditionFunctionIndex"] = Number(condition.FunctionIndex);
        row["ConditionFunctionName"] = condition.FunctionName;
        row["ConditionOperator"] = Number(condition.ComparisonOperator);
        row["ConditionValue"] = (condition.Flags.GetValueOrDefault() & 4) == 0
            ? PerkEffectProjection.Comparison(condition) : null;
        row["ConditionParameter1"] = Number(condition.Parameter1);
        row["ConditionParameter2"] = Number(condition.Parameter2);
        row["ConditionParameter1FormID"] = Fmt.FIdN(condition.Parameter1FormId);
        row["ConditionParameter2FormID"] = Fmt.FIdN(condition.Parameter2FormId);
        row["ConditionFlags"] = detail.GetValueOrDefault("Flags");
        row["ConditionRunOn"] = detail.GetValueOrDefault("Run On");
        row["ConditionReference"] = detail.GetValueOrDefault("Reference");
        row["ConditionComparisonGlobal"] = detail.GetValueOrDefault("Comparison Global");
        row["ConditionRuntimeVA"] = detail.GetValueOrDefault("Runtime VA");
        row["ConditionRawRuntime"] = detail.GetValueOrDefault("Raw Runtime");
        row["ConditionRecovery"] = detail.GetValueOrDefault("Recovery");
        row["ConditionLayoutBasis"] = condition.RuntimeLayoutBasis;
    }

    private static void Append(StringBuilder text, IReadOnlyDictionary<string, string?> row) =>
        text.AppendLine(string.Join(',', Columns.Select(column => Fmt.CsvEscape(row.GetValueOrDefault(column)))));

    private static string Number<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
}
