using System.Text.Json;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     Writes described conditions into a caller's JSON document with <see cref="Utf8JsonWriter" /> only
///     (the published CLI is trimmed and reflection-based serialization throws there). Every field is always
///     written; absent values are explicit nulls. FormIDs and raw words are <c>0x%08X</c> strings, and a
///     non-finite comparison float is written as <c>value: null</c> beside its <c>rawBits</c>.
///     <para>
///         Per condition: <c>index, function, functionIndex, functionKnown, operator, operatorCode,
///         comparison{kind, value, rawBits, globalFormId, globalEditorId, display}, parameter1, parameter2
///         (each null or {raw, kind, display, declared, formId, editorId, resolved, variableIndex, variableName,
///         variableOwnerFormId, stringValue}), runOn{raw, name, explicit}, reference (null or {formId, editorId,
///         display}), referenceRaw, parameter3, parameter3Label, or, orFlagOnLast, connectorToNext, orGroup,
///         swapSubjectTarget, typeRaw, expression, text</c>. <c>text</c> is
///         <see cref="ConditionTextFormatter.FormatLine(ConditionDescription)" />; <c>orGroup</c>/<c>connectorToNext</c> follow the
///         GECK grouping convention, which is not asserted as engine semantics.
///     </para>
/// </summary>
public static class ConditionJsonWriter
{
    /// <summary>Writes <c>"propertyName": [ ...conditions ]</c> into the object currently open in <paramref name="writer" />.</summary>
    public static void Write(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<ConditionDescription> conditions)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(propertyName);
        ArgumentNullException.ThrowIfNull(conditions);

        writer.WriteStartArray(propertyName);
        foreach (var condition in conditions)
        {
            WriteCondition(writer, condition);
        }

        writer.WriteEndArray();
    }

    /// <summary>Writes one condition as a JSON object (as an array element or after a property name).</summary>
    public static void WriteCondition(Utf8JsonWriter writer, ConditionDescription condition)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(condition);

        writer.WriteStartObject();
        writer.WriteNumber("index", condition.Index);
        writer.WriteString("function", condition.FunctionName);
        writer.WriteNumber("functionIndex", condition.FunctionIndex);
        writer.WriteBoolean("functionKnown", condition.FunctionKnown);
        writer.WriteString("operator", condition.Operator);
        writer.WriteNumber("operatorCode", condition.OperatorCode);

        writer.WriteStartObject("comparison");
        writer.WriteString("kind", condition.ComparisonKind);
        if (condition.ComparisonValue is { } value)
        {
            writer.WriteNumber("value", value);
        }
        else
        {
            writer.WriteNull("value");
        }

        writer.WriteString("rawBits", FormatHex(condition.ComparisonRawBits));
        WriteHexOrNull(writer, "globalFormId", condition.ComparisonGlobalFormId);
        WriteStringOrNull(writer, "globalEditorId", condition.ComparisonGlobalEditorId);
        writer.WriteString("display", condition.ComparisonDisplay);
        writer.WriteEndObject();

        WriteOperand(writer, "parameter1", condition.Parameter1);
        WriteOperand(writer, "parameter2", condition.Parameter2);

        writer.WriteStartObject("runOn");
        writer.WriteNumber("raw", condition.RunOnRaw);
        writer.WriteString("name", condition.RunOn);
        writer.WriteBoolean("explicit", condition.RunOnExplicit);
        writer.WriteEndObject();

        if (condition.Reference is { } reference)
        {
            writer.WriteStartObject("reference");
            writer.WriteString("formId", FormatHex(reference));
            WriteStringOrNull(writer, "editorId", condition.ReferenceEditorId);
            WriteStringOrNull(writer, "display", condition.ReferenceDisplay);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("reference");
        }

        writer.WriteString("referenceRaw", FormatHex(condition.ReferenceRaw));
        if (condition.Parameter3 is { } parameter3)
        {
            writer.WriteNumber("parameter3", parameter3);
        }
        else
        {
            writer.WriteNull("parameter3");
        }

        WriteStringOrNull(writer, "parameter3Label", condition.Parameter3Label);
        writer.WriteBoolean("or", condition.IsOr);
        writer.WriteBoolean("orFlagOnLast", condition.OrFlagOnLast);
        WriteStringOrNull(writer, "connectorToNext", condition.ConnectorToNext);
        writer.WriteNumber("orGroup", condition.OrGroup);
        if (condition.SupportsModernTypeFlags)
        {
            writer.WriteBoolean("swapSubjectTarget", condition.SwapSubjectTarget);
            writer.WriteBoolean("usePackData", condition.UsePackData);
        }
        else
        {
            writer.WriteNull("swapSubjectTarget");
            writer.WriteNull("usePackData");
        }
        writer.WriteNumber("unknownTypeBits", condition.UnknownTypeBits);
        writer.WriteNumber("typeRaw", condition.TypeRaw);
        writer.WriteString("expression", condition.Expression);
        writer.WriteString("text", ConditionTextFormatter.FormatLine(condition));
        writer.WriteEndObject();
    }

    private static void WriteOperand(Utf8JsonWriter writer, string propertyName, ConditionOperand? operand)
    {
        if (operand is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteString("raw", FormatHex(operand.Raw));
        writer.WriteString("kind", operand.Kind);
        writer.WriteString("display", operand.Display);
        writer.WriteBoolean("declared", operand.Declared);
        WriteHexOrNull(writer, "formId", operand.FormId);
        WriteStringOrNull(writer, "editorId", operand.EditorId);
        if (operand.Resolved is { } resolved)
        {
            writer.WriteBoolean("resolved", resolved);
        }
        else
        {
            writer.WriteNull("resolved");
        }

        if (operand.VariableIndex is { } variableIndex)
        {
            writer.WriteNumber("variableIndex", variableIndex);
        }
        else
        {
            writer.WriteNull("variableIndex");
        }

        WriteStringOrNull(writer, "variableName", operand.VariableName);
        WriteHexOrNull(writer, "variableOwnerFormId", operand.VariableOwnerFormId);
        WriteStringOrNull(writer, "stringValue", operand.StringValue);
        writer.WriteEndObject();
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(propertyName, value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteHexOrNull(Utf8JsonWriter writer, string propertyName, uint? value)
    {
        if (value is { } number)
        {
            writer.WriteString(propertyName, FormatHex(number));
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static string FormatHex(uint value)
    {
        return $"0x{value:X8}";
    }
}
