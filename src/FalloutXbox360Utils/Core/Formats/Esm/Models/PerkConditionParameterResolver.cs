using FalloutXbox360Utils.Core.Formats.Esm.Script;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

internal static class PerkConditionParameterResolver
{
    public static string ResolveScriptFunctionName(ushort conditionFunctionIndex)
    {
        return ScriptFunctionTable.GetName(ToScriptOpcode(conditionFunctionIndex));
    }

    public static ScriptParamType? GetParameterType(ushort conditionFunctionIndex, int parameterIndex)
    {
        var function = ScriptFunctionTable.Get(ToScriptOpcode(conditionFunctionIndex));
        return function is not null && parameterIndex >= 0 && parameterIndex < function.Params.Length
            ? function.Params[parameterIndex].Type
            : null;
    }

    public static (string? Display, uint? FormId) ResolveParameter(
        ushort conditionFunctionIndex,
        int parameterIndex,
        uint rawValue)
    {
        var paramType = GetParameterType(conditionFunctionIndex, parameterIndex);
        if (rawValue == 0 && paramType is null)
        {
            return (null, null);
        }

        if (ShouldResolveAsForm(paramType))
        {
            return rawValue == 0 ? (null, null) : (null, rawValue);
        }

        return paramType switch
        {
            ScriptParamType.ActorValue => (ScriptStatementDecoder.GetActorValueName((ushort)rawValue), null),
            ScriptParamType.Sex => (rawValue == 0 ? "Male" : "Female", null),
            null => (null, null),
            _ => (rawValue.ToString(), null)
        };
    }

    public static bool IsActorValueParameter(ushort conditionFunctionIndex, int parameterIndex)
    {
        return GetParameterType(conditionFunctionIndex, parameterIndex) == ScriptParamType.ActorValue;
    }

    private static ushort ToScriptOpcode(ushort conditionFunctionIndex)
    {
        return conditionFunctionIndex >= 0x1000
            ? conditionFunctionIndex
            : (ushort)(0x1000 + conditionFunctionIndex);
    }

    private static bool ShouldResolveAsForm(ScriptParamType? paramType)
    {
        return paramType switch
        {
            null => false,
            ScriptParamType.Char or
                ScriptParamType.Int or
                ScriptParamType.Float or
                ScriptParamType.Axis or
                ScriptParamType.AnimGroup or
                ScriptParamType.Sex or
                ScriptParamType.ActorValue or
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
}
