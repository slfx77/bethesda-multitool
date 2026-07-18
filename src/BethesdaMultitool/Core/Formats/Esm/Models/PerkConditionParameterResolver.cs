using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>Maps CTDA condition-function indices to script function names and resolves their parameter values.</summary>
internal static class PerkConditionParameterResolver
{
    /// <summary>
    ///     Returns true when the condition index resolves to a function present in the
    ///     final FNV executable's game-command table. Prototype-only or corrupt indices
    ///     must not be serialized: the retail loader indexes this table while loading
    ///     CTDAs and does not safely tolerate an out-of-range value.
    /// </summary>
    public static bool IsKnownConditionFunction(ushort conditionFunctionIndex)
    {
        return ScriptFunctionTable.Get(ToScriptOpcode(conditionFunctionIndex)) is not null;
    }

    /// <summary>Returns the script function name for a CTDA condition-function index.</summary>
    public static string ResolveScriptFunctionName(ushort conditionFunctionIndex)
    {
        return ScriptFunctionTable.GetName(ToScriptOpcode(conditionFunctionIndex));
    }

    /// <summary>Returns the declared parameter type for a condition function's nth parameter, or null if unknown.</summary>
    public static ScriptParamType? GetParameterType(ushort conditionFunctionIndex, int parameterIndex)
    {
        var function = ScriptFunctionTable.Get(ToScriptOpcode(conditionFunctionIndex));
        return function is not null && parameterIndex >= 0 && parameterIndex < function.Params.Length
            ? function.Params[parameterIndex].Type
            : null;
    }

    /// <summary>Resolves a raw CTDA parameter value into a display string and/or a FormID, based on its declared type.</summary>
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

    /// <summary>True when the named condition-function parameter is an ActorValue code.</summary>
    public static bool IsActorValueParameter(ushort conditionFunctionIndex, int parameterIndex)
    {
        return GetParameterType(conditionFunctionIndex, parameterIndex) == ScriptParamType.ActorValue;
    }

    /// <summary>
    ///     True when the named CTDA parameter is a FormID for this condition function index.
    ///     Used by the dangling-FormID sanitizer to decide whether to validate the parameter.
    ///     Returns false when the function or parameter is unknown so the sanitizer stays
    ///     permissive (we'd rather keep a CTDA whose Param1 we can't classify than drop it).
    /// </summary>
    public static bool IsFormParameter(ushort conditionFunctionIndex, int parameterIndex)
    {
        var type = GetParameterType(conditionFunctionIndex, parameterIndex);
        return type.HasValue && ShouldResolveAsForm(type);
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
