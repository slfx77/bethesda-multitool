using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     FNV-targeted resolver for conversion/runtime paths that consume the extracted FNV command and
///     exact retail callback tables. Its raw indices are not a cross-game CTDA numbering rule; generic
///     consumers must use the game-keyed <see cref="Script.Conditions.ConditionFunctionTable" />.
/// </summary>
internal static class PerkConditionParameterResolver
{
    private static readonly ScriptFunctionSet FalloutFunctions =
        ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas);

    /// <summary>
    ///     Returns true when the raw condition index belongs to the pinned final FNV executable's exact
    ///     non-null callback subset. Absent, script-only, corrupt, and out-of-range indices fail closed.
    /// </summary>
    public static bool IsKnownConditionFunction(ushort conditionFunctionIndex)
    {
        return FalloutFunctions.GetConditionFunction(conditionFunctionIndex) is not null;
    }

    /// <summary>Returns the script function name for a CTDA condition-function index.</summary>
    public static string ResolveScriptFunctionName(ushort conditionFunctionIndex)
    {
        return FalloutFunctions.GetConditionFunction(conditionFunctionIndex)?.Name
               ?? $"UnknownCondition_0x{conditionFunctionIndex:X4}";
    }

    /// <summary>Returns the declared parameter type for a condition function's nth parameter, or null if unknown.</summary>
    public static ScriptParamType? GetParameterType(ushort conditionFunctionIndex, int parameterIndex)
    {
        var function = FalloutFunctions.GetConditionFunction(conditionFunctionIndex);
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
