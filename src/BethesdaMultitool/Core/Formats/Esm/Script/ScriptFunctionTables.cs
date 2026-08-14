using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Game-keyed access to distinct script-opcode and raw condition-index tables. FNV uses the exact
///     250-callback subset from its pinned final Xbox executable. FO3 uses its own exact 237-callback
///     condition map from a pinned retail PC executable; its separate script-opcode lookup retains the
///     pre-existing FNV compatibility table. FO4 and Oblivion use their own explicit subsets. Skyrim has a condition-only raw-index
///     table reconciled against its pinned LE executable/map and xEdit. Fallout 76 has a community-sourced
///     xEdit condition-only table whose raw keys remain independent of opcodes. Starfield likewise has a
///     condition-only xEdit table for raw diagnostics, with no engine/retail identity oracle. The Skyrim,
///     Fallout 76, and Starfield condition-only maps do not populate the script-opcode domain. Games without
///     either table fail closed through empty, correctly game-identified sets.
/// </summary>
public static class ScriptFunctionTables
{
    private static readonly IReadOnlyDictionary<ushort, ScriptFunctionDef> EmptyFunctions =
        new Dictionary<ushort, ScriptFunctionDef>();

    private static readonly ScriptFunctionSet FalloutSet =
        new(
            game: BethesdaGame.FalloutNewVegas,
            functions: ScriptFunctionTable.All,
            conditionFunctionsByIndex: ScriptFunctionTable.ConditionFunctions);

    private static readonly ScriptFunctionSet Fallout3Set =
        new(
            game: BethesdaGame.Fallout3,
            functions: ScriptFunctionTable.All,
            conditionFunctionsByIndex: Fallout3ConditionFunctionTable.ConditionFunctions);

    private static readonly ScriptFunctionSet OblivionSet =
        new(
            game: BethesdaGame.Oblivion,
            functions: OblivionScriptFunctionTable.Functions,
            conditionFunctionsByIndex: OblivionScriptFunctionTable.ConditionFunctions);

    private static readonly ScriptFunctionSet Fallout4Set =
        new(
            game: BethesdaGame.Fallout4,
            functions: Fallout4ScriptFunctionTable.Functions,
            conditionFunctionsByIndex: Fallout4ScriptFunctionTable.ConditionFunctions,
            conditionParamKindsByIndex: Fallout4ScriptFunctionTable.ConditionParamKinds,
            conditionTypeOverrideEligibilityByIndex:
                Fallout4ScriptFunctionTable.ConditionTypeOverrideEligibility);

    private static readonly ScriptFunctionSet SkyrimSet =
        new(
            game: BethesdaGame.Skyrim,
            functions: EmptyFunctions,
            conditionFunctionsByIndex: SkyrimConditionFunctionTable.ConditionFunctions,
            conditionParamKindsByIndex: SkyrimConditionFunctionTable.ConditionParamKinds,
            conditionTypeOverrideEligibilityByIndex:
                SkyrimConditionFunctionTable.ConditionTypeOverrideEligibility);

    private static readonly ScriptFunctionSet Fallout76Set =
        new(
            game: BethesdaGame.Fallout76,
            functions: EmptyFunctions,
            conditionFunctionsByIndex: Fallout76ConditionFunctionTable.ConditionFunctions,
            conditionParamKindsByIndex: Fallout76ConditionFunctionTable.ConditionParamKinds,
            conditionTypeOverrideEligibilityByIndex:
                Fallout76ConditionFunctionTable.ConditionTypeOverrideEligibility);

    private static readonly ScriptFunctionSet StarfieldSet =
        new(
            game: BethesdaGame.Starfield,
            functions: EmptyFunctions,
            conditionFunctionsByIndex: StarfieldConditionFunctionTable.ConditionFunctions,
            conditionParamKindsByIndex: StarfieldConditionFunctionTable.ConditionParamKinds,
            conditionTypeOverrideEligibilityByIndex:
                StarfieldConditionFunctionTable.ConditionTypeOverrideEligibility);

    private static readonly ScriptFunctionSet MorrowindSet = Empty(BethesdaGame.Morrowind);
    private static readonly ScriptFunctionSet UnknownSet = Empty(BethesdaGame.Unknown);

    private static ScriptFunctionSet Empty(BethesdaGame game) =>
        new(
            game: game,
            functions: EmptyFunctions,
            conditionFunctionsByIndex: EmptyFunctions);

    public static ScriptFunctionSet For(BethesdaGame game) => game switch
    {
        BethesdaGame.Unknown => UnknownSet,
        BethesdaGame.Morrowind => MorrowindSet,
        BethesdaGame.Oblivion => OblivionSet,
        BethesdaGame.Fallout3 => Fallout3Set,
        BethesdaGame.FalloutNewVegas => FalloutSet,
        BethesdaGame.Skyrim => SkyrimSet,
        BethesdaGame.Fallout4 => Fallout4Set,
        BethesdaGame.Fallout76 => Fallout76Set,
        BethesdaGame.Starfield => StarfieldSet,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, null),
    };
}
