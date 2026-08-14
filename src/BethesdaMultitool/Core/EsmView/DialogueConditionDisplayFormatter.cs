using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

/// <summary>
///     Formats parsed INFO conditions and result-script metadata for the dialogue viewer.
///     Observed CIS1/CIS2 siblings take precedence over their CTDA placeholder slots. Otherwise,
///     function names and the numeric-vs-FormID parameter split come from the game-keyed
///     <see cref="ConditionFunctionTable" />.
/// </summary>
internal static class DialogueConditionDisplayFormatter
{
    /// <summary>Renders a CTDA condition as a readable function-call expression with operator, value, and qualifiers.</summary>
    public static string FormatCondition(
        DialogueCondition condition,
        Func<uint, string> resolveFormName,
        Func<uint, string>? resolveEditorId = null,
        BethesdaGame game = GameProfiles.DefaultGame)
    {
        var table = ConditionFunctionTable.For(game);
        var functionName = table.GetName(condition.FunctionIndex);

        // Use EditorID for scripting-style display (bare identifier), fall back to full name
        var resolveParamName = resolveEditorId ?? resolveFormName;

        var parameterParts = new List<string>();
        if (condition.Parameter1String is { } parameter1String)
        {
            parameterParts.Add(FormatStringParameter(parameter1String));
        }
        else if (condition.Parameter1 != 0)
        {
            parameterParts.Add(FormatParameter(table, condition, 0, condition.Parameter1, resolveParamName));
        }

        if (condition.Parameter2String is { } parameter2String)
        {
            parameterParts.Add(FormatStringParameter(parameter2String));
        }
        else if (condition.Parameter2 != 0)
        {
            parameterParts.Add(FormatParameter(table, condition, 1, condition.Parameter2, resolveParamName));
        }

        var comparison = condition.UsesGlobalComparison
            ? FormatGlobalComparison(condition.ComparisonGlobalFormId, resolveParamName)
            : FormatComparisonValue(condition.ComparisonValue);
        var expression = parameterParts.Count > 0
            ? $"{functionName}({string.Join(", ", parameterParts)}) {condition.ComparisonOperator} {comparison}"
            : $"{functionName} {condition.ComparisonOperator} {comparison}";

        var qualifiers = new List<string>();
        if (condition.IsOr)
        {
            qualifiers.Add("OR");
        }

        if (DialogueConditionRunOnPolicy.ShouldDisplay(condition, game))
        {
            qualifiers.Add($"Run On: {DialogueConditionRunOnPolicy.Format(condition, game)}");
        }

        if (DialogueConditionReferencePolicy.TryGetSemanticReference(condition, game, out var reference))
        {
            qualifiers.Add($"Ref: {resolveFormName(reference)} (0x{reference:X8})");
        }

        if (TryFormatParameter3(condition, game, out var parameter3))
        {
            qualifiers.Add(parameter3);
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
        BethesdaGame game = GameProfiles.DefaultGame)
    {
        // A physical CIS1/CIS2 sibling is authoritative even when the function table is absent,
        // incomplete, or disagrees with a newer retail record. The CTDA u32 is only placeholder
        // storage in that case and must never enter the reverse FormID index.
        if (paramIndex switch
            {
                0 => condition.Parameter1String is not null,
                1 => condition.Parameter2String is not null,
                _ => false
            })
        {
            return false;
        }

        var table = ConditionFunctionTable.For(game);
        return table.TryClassifyParam(
                   condition.FunctionIndex,
                   paramIndex,
                   condition.Type,
                   condition.RunOn,
                   condition.Parameter1,
                   out var kind) &&
               kind == ConditionParamKind.FormId;
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

    private static string FormatGlobalComparison(uint formId, Func<uint, string> resolveName)
    {
        if (formId == 0)
        {
            return "GLOB 0x00000000";
        }

        var resolved = resolveName(formId);
        return $"GLOB {resolved} (0x{formId:X8})";
    }

    private static string FormatStringParameter(string value)
    {
        var escaped = new StringBuilder(value.Length + 2);
        escaped.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                case '\b':
                    escaped.Append("\\b");
                    break;
                case '\f':
                    escaped.Append("\\f");
                    break;
                case var control when char.IsControl(control):
                    escaped.Append("\\u");
                    escaped.Append(((int)control).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    escaped.Append(character);
                    break;
            }
        }

        escaped.Append('"');
        return escaped.ToString();
    }

    private static bool TryFormatParameter3(
        DialogueCondition condition,
        BethesdaGame game,
        out string formatted)
    {
        formatted = string.Empty;
        if (condition.Parameter3 is not { } value)
        {
            return false;
        }

        // Community provenance for Starfield's signed Quest Alias/Event Data arms: xEdit commit
        // e0e529a2d473756520f2d41f72c24dea0cf5ee0d, wbDefinitionsSF1.pas SHA-256
        // 8736162FCE44C970CFA3DDAC945A739530169390C4FDABAFC0209B36B247A576,
        // MPL-2.0. The retail census supports the physical signed field, not these labels.
        var modern = game is BethesdaGame.Skyrim or BethesdaGame.Fallout4 or BethesdaGame.Fallout76
            or BethesdaGame.Starfield;
        var semanticLabel = modern
            ? condition.RunOn switch
            {
                5 => "Quest Alias",
                7 => "Event Data",
                _ => null
            }
            : null;

        // -1 is the normal raw default. It is still meaningful for the two Run-On-selected modern
        // contexts, but suppress it elsewhere so every ordinary 32-byte condition does not gain noise.
        if (semanticLabel is null && value == -1)
        {
            return false;
        }

        var label = semanticLabel ?? "Parameter #3";
        formatted = $"{label}: {value.ToString(CultureInfo.InvariantCulture)}";
        return true;
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

        // Unknown functions/params stay numeric — the historical raw-value fallback.
        return table.TryClassifyParam(
                   condition.FunctionIndex,
                   paramIndex,
                   condition.Type,
                   condition.RunOn,
                   condition.Parameter1,
                   out var kind) &&
               kind == ConditionParamKind.FormId
            ? resolveName(value)
            : value.ToString();
    }
}
