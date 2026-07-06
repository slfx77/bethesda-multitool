using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

/// <summary>
///     Formats parsed INFO conditions and result-script metadata for the dialogue viewer.
///     Function names and the numeric-vs-FormID parameter split come from the game-keyed
///     <see cref="ConditionFunctionTable" /> — Oblivion conditions previously rendered through the
///     FNV table, misnaming every function past index 0x171 and mistyping params past raw id 31.
/// </summary>
internal static class DialogueConditionDisplayFormatter
{
    /// <summary>Renders a CTDA condition as a readable function-call expression with operator, value, and qualifiers.</summary>
    public static string FormatCondition(
        DialogueCondition condition,
        Func<uint, string> resolveFormName,
        Func<uint, string>? resolveEditorId = null,
        BethesdaGame game = BethesdaGame.FalloutNewVegas)
    {
        var table = ConditionFunctionTable.For(game);
        var functionName = table.GetName(condition.FunctionIndex);

        // Use EditorID for scripting-style display (bare identifier), fall back to full name
        var resolveParamName = resolveEditorId ?? resolveFormName;

        var parameterParts = new List<string>();
        if (condition.Parameter1 != 0)
        {
            parameterParts.Add(FormatParameter(table, condition, 0, condition.Parameter1, resolveParamName));
        }

        if (condition.Parameter2 != 0)
        {
            parameterParts.Add(FormatParameter(table, condition, 1, condition.Parameter2, resolveParamName));
        }

        var expression = parameterParts.Count > 0
            ? $"{functionName}({string.Join(", ", parameterParts)}) {condition.ComparisonOperator} {FormatComparisonValue(condition.ComparisonValue)}"
            : $"{functionName} {condition.ComparisonOperator} {FormatComparisonValue(condition.ComparisonValue)}";

        var qualifiers = new List<string>();
        if (condition.IsOr)
        {
            qualifiers.Add("OR");
        }

        if (condition.RunOn != 0)
        {
            qualifiers.Add($"Run On: {condition.RunOnName}");
        }

        if (condition.Reference != 0)
        {
            qualifiers.Add($"Ref: {resolveFormName(condition.Reference)} (0x{condition.Reference:X8})");
        }

        if (condition.IsSubjectTargetSwapped)
        {
            qualifiers.Add("Swap Subject/Target");
        }

        return qualifiers.Count > 0
            ? $"{expression} [{string.Join("; ", qualifiers)}]"
            : expression;
    }

    /// <summary>
    ///     Determines whether a condition parameter at the given index (0 or 1) is a FormID reference
    ///     rather than a numeric value.
    /// </summary>
    public static bool IsFormReference(
        DialogueCondition condition,
        int paramIndex,
        BethesdaGame game = BethesdaGame.FalloutNewVegas)
    {
        var table = ConditionFunctionTable.For(game);
        var function = table.Get(condition.FunctionIndex);
        if (function == null || paramIndex >= function.Params.Length)
        {
            return false;
        }

        return table.ClassifyParam(condition.FunctionIndex, paramIndex) == ConditionParamKind.FormId;
    }

    /// <summary>Formats a result script's referenced objects as a comma-separated "name (0xFormID)" list.</summary>
    public static string FormatResultScriptReferences(
        DialogueResultScript resultScript,
        Func<uint, string> resolveFormName)
    {
        return string.Join(", ",
            resultScript.ReferencedObjects.Select(formId => $"{resolveFormName(formId)} (0x{formId:X8})"));
    }

    private static string FormatComparisonValue(float value)
    {
        var rounded = MathF.Round(value);
        return MathF.Abs(value - rounded) < 0.0001f
            ? rounded.ToString("0")
            : value.ToString("0.###");
    }

    private static string FormatParameter(
        ConditionFunctionTable table,
        DialogueCondition condition,
        int paramIndex,
        uint value,
        Func<uint, string> resolveName)
    {
        if (value == 0)
        {
            return "0";
        }

        // Unknown functions/params classify Numeric — the historical raw-value fallback.
        return table.ClassifyParam(condition.FunctionIndex, paramIndex) == ConditionParamKind.FormId
            ? resolveName(value)
            : value.ToString();
    }
}
