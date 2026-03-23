using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;

namespace FalloutXbox360Utils;

/// <summary>
///     Formats parsed INFO conditions and result-script metadata for the dialogue viewer.
/// </summary>
internal static class DialogueConditionDisplayFormatter
{
    public static string FormatCondition(
        DialogueCondition condition,
        Func<uint, string> resolveFormName,
        Func<uint, string>? resolveEditorId = null)
    {
        var opcode = (ushort)(0x1000 | condition.FunctionIndex);
        var function = ScriptFunctionTable.Get(opcode);
        var functionName = function?.Name ?? $"Func 0x{condition.FunctionIndex:X4}";
        ScriptParamType? firstParamType = function is not null && function.Params.Length > 0
            ? function.Params[0].Type
            : null;
        ScriptParamType? secondParamType = function is not null && function.Params.Length > 1
            ? function.Params[1].Type
            : null;

        // Use EditorID for scripting-style display (bare identifier), fall back to full name
        var resolveParamName = resolveEditorId ?? resolveFormName;

        var parameterParts = new List<string>();
        if (condition.Parameter1 != 0)
        {
            parameterParts.Add(FormatParameter(
                firstParamType,
                condition.Parameter1,
                resolveParamName));
        }

        if (condition.Parameter2 != 0)
        {
            parameterParts.Add(FormatParameter(
                secondParamType,
                condition.Parameter2,
                resolveParamName));
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
    public static bool IsFormReference(DialogueCondition condition, int paramIndex)
    {
        var opcode = (ushort)(0x1000 | condition.FunctionIndex);
        var function = ScriptFunctionTable.Get(opcode);
        if (function == null || paramIndex >= function.Params.Length)
        {
            return false;
        }

        return function.Params[paramIndex].Type switch
        {
            ScriptParamType.Char or
                ScriptParamType.Int or
                ScriptParamType.Float or
                ScriptParamType.Axis or
                ScriptParamType.AnimGroup or
                ScriptParamType.Sex or
                ScriptParamType.ScriptVar or
                ScriptParamType.Stage or
                ScriptParamType.CrimeType or
                ScriptParamType.FormType or
                ScriptParamType.MiscStat or
                ScriptParamType.VatsValue or
                ScriptParamType.VatsValueData or
                ScriptParamType.Alignment or
                ScriptParamType.CritStage => false,
            _ => true
        };
    }

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
        ScriptParamType? paramType,
        uint value,
        Func<uint, string> resolveName)
    {
        if (value == 0)
        {
            return "0";
        }

        return paramType switch
        {
            ScriptParamType.Char or
                ScriptParamType.Int or
                ScriptParamType.Float or
                ScriptParamType.Axis or
                ScriptParamType.AnimGroup or
                ScriptParamType.Sex or
                ScriptParamType.ScriptVar or
                ScriptParamType.Stage or
                ScriptParamType.CrimeType or
                ScriptParamType.FormType or
                ScriptParamType.MiscStat or
                ScriptParamType.VatsValue or
                ScriptParamType.VatsValueData or
                ScriptParamType.Alignment or
                ScriptParamType.CritStage => value.ToString(),
            _ => resolveName(value)
        };
    }
}
