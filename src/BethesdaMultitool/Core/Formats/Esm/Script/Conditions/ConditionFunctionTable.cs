using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     Game-keyed CTDA condition-function lookup: <c>(functionIndex) → {name, param kinds}</c>,
///     built over the game's script command table (<c>opcode = 0x1000 | functionIndex</c>) plus a
///     per-game numeric-vs-FormID classifier. This is the shared seam both the dialogue condition
///     display and the schema decoder's typed CTDA decoding consume — previously the numeric split
///     lived inline in <c>DialogueConditionDisplayFormatter</c> and was FNV-only.
/// </summary>
public sealed class ConditionFunctionTable
{
    private readonly ScriptFunctionSet _functions;

    private ConditionFunctionTable(ScriptFunctionSet functions)
    {
        _functions = functions;
    }

    private static readonly ConditionFunctionTable Fallout =
        new(ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas));

    private static readonly ConditionFunctionTable Oblivion =
        new(ScriptFunctionTables.For(BethesdaGame.Oblivion));

    public static ConditionFunctionTable For(BethesdaGame game) => game switch
    {
        BethesdaGame.Oblivion => Oblivion,
        _ => Fallout,
    };

    public BethesdaGame Game => _functions.Game;

    /// <summary>The function's display name, or a hex placeholder when unknown.</summary>
    public string GetName(ushort functionIndex) =>
        _functions.GetConditionFunction(functionIndex)?.Name ?? $"Func 0x{functionIndex:X4}";

    public ScriptFunctionDef? Get(ushort functionIndex) => _functions.GetConditionFunction(functionIndex);

    /// <summary>
    ///     Classifies parameter <paramref name="paramIndex" /> (0 or 1) of the given condition
    ///     function. Unknown functions / out-of-range params classify as
    ///     <see cref="ConditionParamKind.FormId" /> — the display formatter's historical fallback
    ///     resolved unknown params as names (its old switch sent a null param type to the FormID
    ///     branch), and a non-zero value on an unknown function is more useful resolved than raw.
    /// </summary>
    public ConditionParamKind ClassifyParam(ushort functionIndex, int paramIndex)
    {
        var function = _functions.GetConditionFunction(functionIndex);
        if (function == null || paramIndex < 0 || paramIndex >= function.Params.Length)
        {
            return ConditionParamKind.FormId;
        }

        return Classify(function.Params[paramIndex]);
    }

    /// <summary>Classifies a single parameter definition under this table's game numbering.</summary>
    public ConditionParamKind Classify(ScriptFunctionParamDef param) =>
        Game == BethesdaGame.Oblivion
            ? ClassifyTes4(param.ObType)
            : ClassifyFallout(param.Type);

    // Mirrors DialogueConditionDisplayFormatter's original numeric switch exactly (ScriptVar stays
    // numeric — it is a quest-variable index displayed as a number, paired with a Quest param).
    private static ConditionParamKind ClassifyFallout(ScriptParamType type) => type switch
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
            ScriptParamType.CritStage => ConditionParamKind.Numeric,
        _ => ConditionParamKind.FormId,
    };

    // TES4 numbering (ObScriptParamType). Non-FormID kinds: strings, plain numbers, and the
    // by-index enums (ActorValue is a skill/attribute index, VariableName a quest-variable index).
    private static ConditionParamKind ClassifyTes4(ObScriptParamType type) => type switch
    {
        ObScriptParamType.String or
            ObScriptParamType.Integer or
            ObScriptParamType.Float or
            ObScriptParamType.ActorValue or
            ObScriptParamType.Axis or
            ObScriptParamType.AnimGroup or
            ObScriptParamType.Sex or
            ObScriptParamType.VariableName or
            ObScriptParamType.QuestStage or
            ObScriptParamType.CrimeType or
            ObScriptParamType.FormType => ConditionParamKind.Numeric,
        _ => ConditionParamKind.FormId,
    };
}
