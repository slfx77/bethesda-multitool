using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     Describes CTDA condition lists for the CLI, text reports and JSON: one structured
///     <see cref="ConditionDescription" /> per condition, in source order. Shared by every record type that
///     carries conditions (INFO, PACK, TERM menu items, QUST, PERK), so they all print a condition the same way.
///     <para>
///         Parameters: an observed CIS1/CIS2 string always wins. Otherwise the game's
///         <see cref="ConditionFunctionTable" /> says which slots the function declares, and a declared slot is
///         printed even when its value is zero (a zero variable index, actor value 0 and Sex 0 = Male are all
///         real values). An undeclared slot is printed only when non-zero, as raw hex. FormID vs number comes from
///         the context-aware <see cref="ConditionFunctionTable.TryClassifyParam(ushort, int, byte, uint?, uint?, out ConditionParamKind)" />;
///         for Fallout 3 and New Vegas, ActorValue and Sex slots are named (the sources
///         <c>PerkConditionParameterResolver</c> uses) and a ScriptVar slot after a Quest slot is resolved to the
///         quest variable's name through the <see cref="ConditionDisplayContext" />. A variable that cannot be
///         resolved prints as <c>var N</c>, never a guess.
///     </para>
///     <para>
///         The dialogue viewer keeps its own text (<c>DialogueConditionDisplayFormatter</c>); the comparison,
///         string and Parameter #3 helpers it shares with this class live here and are called from there unchanged.
///     </para>
/// </summary>
public static class ConditionDescriber
{
    /// <summary>Describes every condition in <paramref name="conditions" />, preserving order.</summary>
    public static IReadOnlyList<ConditionDescription> DescribeAll(
        IReadOnlyList<DialogueCondition> conditions,
        ConditionDisplayContext context)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(context);

        var descriptions = new ConditionDescription[conditions.Count];
        var group = 1;
        for (var i = 0; i < conditions.Count; i++)
        {
            var condition = conditions[i] ?? throw new ArgumentException(
                $"Condition {i + 1} is null.", nameof(conditions));
            var isLast = i == conditions.Count - 1;
            descriptions[i] = DescribeCore(condition, i + 1, isLast, group, context);

            // A condition carrying the OR flag joins the next one into its group; an AND starts a new group.
            if (!condition.IsOr)
            {
                group++;
            }
        }

        return descriptions;
    }

    /// <summary>Describes a single condition as a one-element list (index 1, no connector).</summary>
    public static ConditionDescription Describe(DialogueCondition condition, ConditionDisplayContext context)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(context);
        return DescribeCore(condition, 1, true, 1, context);
    }

    /// <summary>
    ///     Formats a numeric comparison value: whole numbers without decimals, otherwise up to three decimals.
    ///     A null <paramref name="provider" /> uses the current culture (the dialogue viewer's historical text);
    ///     CLI, report and JSON text passes <see cref="CultureInfo.InvariantCulture" />.
    /// </summary>
    internal static string FormatComparisonValue(float value, IFormatProvider? provider = null)
    {
        var rounded = MathF.Round(value);
        return MathF.Abs(value - rounded) < 0.0001f
            ? rounded.ToString("0", provider)
            : value.ToString("0.###", provider);
    }

    /// <summary>Quotes a CIS1/CIS2 string parameter with deterministic C-style escapes.</summary>
    internal static string FormatStringParameter(string value)
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

    /// <summary>
    ///     Formats the signed trailing Parameter #3 of a modern 32-byte CTDA, labelled by its Run-On-selected
    ///     meaning where one is known. Returns false when it is absent, or when it holds the ordinary -1 default
    ///     outside the two Run-On-selected contexts.
    /// </summary>
    internal static bool TryFormatParameter3(
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

    private static ConditionDescription DescribeCore(
        DialogueCondition condition,
        int index,
        bool isLast,
        int orGroup,
        ConditionDisplayContext context)
    {
        var game = context.Game;
        var resolver = context.Resolver;
        var table = ConditionFunctionTable.For(game);
        var function = table.Get(condition.FunctionIndex);

        var parameter1 = DescribeParameter(condition, 0, table, function, context);
        var parameter2 = DescribeParameter(condition, 1, table, function, context);

        // Slots are positional: never print parameter 2 in parameter 1's place.
        parameter1 ??= parameter2 is null ? null : RawOperand(condition.Parameter1, false);

        var operatorCode = (condition.Type >> 5) & 0x7;
        var operatorText = operatorCode <= 5 ? condition.ComparisonOperator : $"?({operatorCode})";

        string comparisonKind;
        float? comparisonValue = null;
        uint? globalFormId = null;
        string? globalEditorId = null;
        string comparisonDisplay;
        if (condition.UsesGlobalComparison)
        {
            var globalId = condition.ComparisonGlobalFormId;
            comparisonKind = ConditionDescription.ComparisonKindGlobal;
            globalFormId = globalId;
            globalEditorId = LookUpEditorId(resolver, globalId);
            comparisonDisplay = $"GLOB {ConditionTextFormatter.FormatFormId(globalId, globalEditorId)}";
        }
        else if (float.IsFinite(condition.ComparisonValue))
        {
            comparisonKind = ConditionDescription.ComparisonKindNumeric;
            comparisonValue = condition.ComparisonValue;
            comparisonDisplay = FormatComparisonValue(condition.ComparisonValue, CultureInfo.InvariantCulture);
        }
        else
        {
            comparisonKind = ConditionDescription.ComparisonKindNumeric;
            comparisonDisplay =
                $"non-finite (bits 0x{BitConverter.SingleToUInt32Bits(condition.ComparisonValue):X8})";
        }

        var runOnExplicit = DialogueConditionRunOnPolicy.ShouldDisplay(condition, game);
        var runOn = runOnExplicit ? DialogueConditionRunOnPolicy.Format(condition, game) : "Subject";

        uint? reference = null;
        string? referenceEditorId = null;
        string? referenceDisplay = null;
        if (DialogueConditionReferencePolicy.TryGetSemanticReference(condition, game, out var semanticReference))
        {
            reference = semanticReference;
            referenceEditorId = LookUpEditorId(resolver, semanticReference);
            referenceDisplay = ConditionTextFormatter.FormatFormId(semanticReference, referenceEditorId);
        }

        var functionName = table.GetName(condition.FunctionIndex);
        var operands = new List<string>(2);
        if (parameter1 is not null)
        {
            operands.Add(parameter1.Display);
        }

        if (parameter2 is not null)
        {
            operands.Add(parameter2.Display);
        }

        var expression = operands.Count > 0
            ? $"{functionName}({string.Join(", ", operands)}) {operatorText} {comparisonDisplay}"
            : $"{functionName} {operatorText} {comparisonDisplay}";

        return new ConditionDescription
        {
            Index = index,
            Raw = condition,
            Game = game,
            FunctionName = functionName,
            FunctionKnown = function is not null,
            Operator = operatorText,
            ComparisonKind = comparisonKind,
            ComparisonValue = comparisonValue,
            ComparisonGlobalFormId = globalFormId,
            ComparisonGlobalEditorId = globalEditorId,
            ComparisonDisplay = comparisonDisplay,
            Parameter1 = parameter1,
            Parameter2 = parameter2,
            RunOn = runOn,
            RunOnExplicit = runOnExplicit,
            Reference = reference,
            ReferenceEditorId = referenceEditorId,
            ReferenceDisplay = referenceDisplay,
            Parameter3Label = TryFormatParameter3(condition, game, out var parameter3) ? parameter3 : null,
            OrFlagOnLast = isLast && condition.IsOr,
            ConnectorToNext = isLast ? null : condition.IsOr ? "OR" : "AND",
            OrGroup = orGroup,
            Expression = expression
        };
    }

    private static ConditionOperand? DescribeParameter(
        DialogueCondition condition,
        int slot,
        ConditionFunctionTable table,
        ScriptFunctionDef? function,
        ConditionDisplayContext context)
    {
        var raw = slot == 0 ? condition.Parameter1 : condition.Parameter2;
        var declared = function is not null && IsDeclared(table, function, condition.FunctionIndex, slot);

        // An observed CIS1/CIS2 sibling is authoritative; the CTDA u32 is then placeholder storage.
        var stringValue = slot == 0 ? condition.Parameter1String : condition.Parameter2String;
        if (stringValue is not null)
        {
            return new ConditionOperand(raw, ConditionOperand.KindString, FormatStringParameter(stringValue))
            {
                Declared = declared,
                StringValue = stringValue
            };
        }

        if (function is null || !declared)
        {
            return raw == 0 ? null : RawOperand(raw, false);
        }

        if (table.Game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas && slot < function.Params.Length)
        {
            switch (function.Params[slot].Type)
            {
                case ScriptParamType.ActorValue:
                    return new ConditionOperand(
                        raw,
                        ConditionOperand.KindActorValue,
                        raw <= ushort.MaxValue
                            ? ScriptStatementDecoder.GetActorValueName((ushort)raw)
                            : raw.ToString(CultureInfo.InvariantCulture))
                    {
                        Declared = true
                    };
                case ScriptParamType.Sex:
                    return new ConditionOperand(
                        raw,
                        ConditionOperand.KindSex,
                        raw switch
                        {
                            0 => "Male",
                            1 => "Female",
                            _ => raw.ToString(CultureInfo.InvariantCulture)
                        })
                    {
                        Declared = true
                    };
                case ScriptParamType.ScriptVar:
                    return DescribeScriptVariable(condition, slot, function, raw, context);
            }
        }

        if (!table.TryClassifyParam(
                condition.FunctionIndex,
                slot,
                condition.Type,
                condition.RunOn,
                condition.Parameter1,
                out var kind))
        {
            return RawOperand(raw, true);
        }

        if (kind != ConditionParamKind.FormId)
        {
            return new ConditionOperand(raw, ConditionOperand.KindNumber, raw.ToString(CultureInfo.InvariantCulture))
            {
                Declared = true
            };
        }

        var editorId = LookUpEditorId(context.Resolver, raw);
        return new ConditionOperand(raw, ConditionOperand.KindFormId, ConditionTextFormatter.FormatFormId(raw, editorId))
        {
            Declared = true,
            FormId = raw,
            EditorId = editorId,
            Resolved = editorId is not null
        };
    }

    private static ConditionOperand DescribeScriptVariable(
        DialogueCondition condition,
        int slot,
        ScriptFunctionDef function,
        uint raw,
        ConditionDisplayContext context)
    {
        uint? ownerFormId = slot == 1 &&
                            function.Params.Length > 0 &&
                            function.Params[0].Type is ScriptParamType.Quest or ScriptParamType.ObjectRef &&
                            condition.Parameter1String is null
            ? condition.Parameter1
            : null;
        var name = ownerFormId is { } owner
            ? function.Params[0].Type == ScriptParamType.Quest
                ? context.TryGetQuestVariableName(owner, raw, out _)
                : context.TryGetScriptVariableName(owner, raw)
            : null;
        var display = name is not null
            ? $"{name} [var {raw.ToString(CultureInfo.InvariantCulture)}]"
            : $"var {raw.ToString(CultureInfo.InvariantCulture)}";
        return new ConditionOperand(raw, ConditionOperand.KindScriptVariable, display)
        {
            Declared = true,
            VariableIndex = raw,
            VariableName = name,
            VariableOwnerFormId = ownerFormId,
            Resolved = name is not null
        };
    }

    private static bool IsDeclared(
        ConditionFunctionTable table,
        ScriptFunctionDef function,
        ushort functionIndex,
        int slot)
    {
        // Classic tables declare parameters through the definition; the modern condition-only tables carry
        // empty definitions and declare their slots through the authoritative parameter-kind map instead.
        return slot < function.Params.Length || table.TryClassifyParam(functionIndex, slot, out _);
    }

    private static ConditionOperand RawOperand(uint raw, bool declared)
    {
        return new ConditionOperand(raw, ConditionOperand.KindRaw, $"0x{raw:X8}")
        {
            Declared = declared
        };
    }

    private static string? LookUpEditorId(FormIdResolver resolver, uint formId)
    {
        return formId == 0 ? null : resolver.GetEditorId(formId);
    }
}
