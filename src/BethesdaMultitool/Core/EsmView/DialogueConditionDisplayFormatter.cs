using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.EsmView;

/// <summary>
///     Formats parsed INFO conditions and result-script metadata for the dialogue viewer.
///     Observed CIS1/CIS2 siblings take precedence over their CTDA placeholder slots. Otherwise,
///     function names and the numeric-vs-FormID parameter split come from the game-keyed
///     <see cref="ConditionFunctionTable" />.
///     <para>
///         This is the viewer's compact text (implicit default Run On, zero parameters omitted). CLI,
///         report and JSON output use <see cref="ConditionDescriber" /> instead; the comparison, CIS-string
///         and Parameter #3 helpers live there and are shared, so both keep identical wording for them.
///     </para>
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
            parameterParts.Add(ConditionDescriber.FormatStringParameter(parameter1String));
        }
        else if (condition.Parameter1 != 0)
        {
            parameterParts.Add(FormatParameter(table, condition, 0, condition.Parameter1, resolveParamName));
        }

        if (condition.Parameter2String is { } parameter2String)
        {
            parameterParts.Add(ConditionDescriber.FormatStringParameter(parameter2String));
        }
        else if (condition.Parameter2 != 0)
        {
            parameterParts.Add(FormatParameter(table, condition, 1, condition.Parameter2, resolveParamName));
        }

        var comparison = condition.UsesGlobalComparison
            ? FormatGlobalComparison(condition.ComparisonGlobalFormId, resolveParamName)
            : ConditionDescriber.FormatComparisonValue(condition.ComparisonValue);
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

        // Parameter #3 labels (ConditionDescriber.TryFormatParameter3). Community provenance for Starfield's
        // signed Quest Alias/Event Data arms: xEdit commit e0e529a2d473756520f2d41f72c24dea0cf5ee0d,
        // wbDefinitionsSF1.pas SHA-256 8736162FCE44C970CFA3DDAC945A739530169390C4FDABAFC0209B36B247A576,
        // MPL-2.0. The retail census supports the physical signed field, not these labels.
        if (ConditionDescriber.TryFormatParameter3(condition, game, out var parameter3))
        {
            qualifiers.Add(parameter3);
        }

        qualifiers.AddRange(ConditionTypeFlags.Describe(condition.Type, game));

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

    private static string FormatGlobalComparison(uint formId, Func<uint, string> resolveName)
    {
        if (formId == 0)
        {
            return "GLOB 0x00000000";
        }

        var resolved = resolveName(formId);
        return $"GLOB {resolved} (0x{formId:X8})";
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
