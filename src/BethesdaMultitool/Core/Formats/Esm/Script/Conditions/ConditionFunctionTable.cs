using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     Game-keyed CTDA condition-function lookup: <c>(functionIndex) → {name, param kinds}</c>,
///     keyed by the raw index stored in the record. A game may reuse a script-command definition
///     where engine evidence supports it, but the condition index is not itself an opcode. This is
///     the shared seam both dialogue display and schema CTDA decoding consume.
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

    private static readonly ConditionFunctionTable Fallout3 =
        new(ScriptFunctionTables.For(BethesdaGame.Fallout3));

    private static readonly ConditionFunctionTable Oblivion =
        new(ScriptFunctionTables.For(BethesdaGame.Oblivion));

    private static readonly ConditionFunctionTable Fallout4 =
        new(ScriptFunctionTables.For(BethesdaGame.Fallout4));

    private static readonly ConditionFunctionTable Morrowind =
        new(ScriptFunctionTables.For(BethesdaGame.Morrowind));

    private static readonly ConditionFunctionTable Skyrim =
        new(ScriptFunctionTables.For(BethesdaGame.Skyrim));

    private static readonly ConditionFunctionTable Fallout76 =
        new(ScriptFunctionTables.For(BethesdaGame.Fallout76));

    private static readonly ConditionFunctionTable Starfield =
        new(ScriptFunctionTables.For(BethesdaGame.Starfield));

    private static readonly ConditionFunctionTable Unknown =
        new(ScriptFunctionTables.For(BethesdaGame.Unknown));

    public static ConditionFunctionTable For(BethesdaGame game) => game switch
    {
        BethesdaGame.Unknown => Unknown,
        BethesdaGame.Morrowind => Morrowind,
        BethesdaGame.Oblivion => Oblivion,
        BethesdaGame.Fallout3 => Fallout3,
        BethesdaGame.FalloutNewVegas => Fallout,
        BethesdaGame.Skyrim => Skyrim,
        BethesdaGame.Fallout4 => Fallout4,
        BethesdaGame.Fallout76 => Fallout76,
        BethesdaGame.Starfield => Starfield,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, null),
    };

    public BethesdaGame Game => _functions.Game;

    /// <summary>The function's display name, or a hex placeholder when unknown.</summary>
    public string GetName(ushort functionIndex) =>
        _functions.GetConditionFunction(functionIndex)?.Name ?? $"Func 0x{functionIndex:X4}";

    public ScriptFunctionDef? Get(ushort functionIndex) => _functions.GetConditionFunction(functionIndex);

    /// <summary>
    ///     Tries to classify parameter <paramref name="paramIndex" /> (0 or 1) of a resolved function
    ///     definition. Explicit per-game condition maps are authoritative. Unsupported games, absent
    ///     definitions/parameters, and unresolved metadata return false rather than inventing a FormID.
    /// </summary>
    public bool TryClassifyParam(ushort functionIndex, int paramIndex, out ConditionParamKind kind)
    {
        kind = default;
        if (_functions.TryGetConditionParamKind(functionIndex, paramIndex, out kind))
        {
            return true;
        }

        if (_functions.HasAuthoritativeConditionParamKinds)
        {
            return false;
        }

        var function = _functions.GetConditionFunction(functionIndex);
        if (function == null || paramIndex < 0 || paramIndex >= function.Params.Length)
        {
            return false;
        }

        kind = Classify(function.Params[paramIndex]);
        return true;
    }

    /// <summary>
    ///     Tries to classify a CTDA parameter with its condition context. Skyrim, Fallout 4, Fallout 76,
    ///     and Starfield
    ///     <c>Use Aliases</c> (0x02) and <c>Use Packdata</c> (0x08) replace only xEdit-declared
    ///     Reference/Actor/Package slots with non-FormID indices; alias wins when both bits are set.
    ///     FO4/FO76/SF1's GetIsCurrentPackage/Run-On-Quest-Alias exception needs an observed Run On value and
    ///     therefore fails closed when that context is unavailable. The overload accepting parameter #1
    ///     additionally resolves Skyrim GetVATSValue's value-dependent second slot.
    /// </summary>
    public bool TryClassifyParam(
        ushort functionIndex,
        int paramIndex,
        byte conditionType,
        uint? runOn,
        out ConditionParamKind kind) =>
        TryClassifyParam(
            functionIndex, paramIndex, conditionType, runOn, parameter1Value: null, out kind);

    /// <summary>
    ///     Context-aware classification including the raw first parameter. Skyrim GetVATSValue uses
    ///     parameter #1 as a selector for parameter #2: selectors 0–3 and 9–10 are FormIDs, the remaining
    ///     defined selectors 4–8 and 11–20 are scalar/enum/raw values, and absent/out-of-range selectors
    ///     fail closed.
    /// </summary>
    public bool TryClassifyParam(
        ushort functionIndex,
        int paramIndex,
        byte conditionType,
        uint? runOn,
        uint? parameter1Value,
        out ConditionParamKind kind)
    {
        if (Game == BethesdaGame.Skyrim &&
            functionIndex == SkyrimConditionFunctionTable.VatsValueFunctionIndex &&
            paramIndex == 1)
        {
            kind = default;
            if (_functions.GetConditionFunction(functionIndex) is null ||
                parameter1Value is not { } selector || selector > 20)
            {
                return false;
            }

            kind = selector is 0 or 1 or 2 or 3 or 9 or 10
                ? ConditionParamKind.FormId
                : ConditionParamKind.Numeric;
            return true;
        }

        if (!TryClassifyParam(functionIndex, paramIndex, out kind))
        {
            return false;
        }

        if (Game is not (
                BethesdaGame.Skyrim or
                BethesdaGame.Fallout4 or
                BethesdaGame.Fallout76 or
                BethesdaGame.Starfield) ||
            !_functions.IsConditionTypeOverrideEligible(functionIndex, paramIndex))
        {
            return true;
        }

        var useAliases = (conditionType & 0x02) != 0;
        var usePackdata = (conditionType & 0x08) != 0;
        if (!useAliases && !usePackdata)
        {
            return true;
        }

        // In FO4/FO76/SF1 xEdit, this exact case keeps param1 as the Package FormID: the alias is the
        // Run-On-selected physical Parameter #3. Without Run On, guessing either way is unsafe.
        if (Game is (BethesdaGame.Fallout4 or BethesdaGame.Fallout76 or BethesdaGame.Starfield) &&
            useAliases && functionIndex == 0x0A1 && paramIndex == 0)
        {
            if (runOn is null)
            {
                return false;
            }

            if (runOn == 5)
            {
                return true;
            }
        }

        kind = ConditionParamKind.Numeric;
        return true;
    }

    /// <summary>
    ///     Classifies a known parameter without Type/Run-On context, falling back to raw/numeric on
    ///     any metadata miss. This deliberately returns the declared base kind; CTDA consumers that
    ///     possess condition flags must call the context-aware overload above.
    /// </summary>
    public ConditionParamKind ClassifyParam(ushort functionIndex, int paramIndex) =>
        TryClassifyParam(functionIndex, paramIndex, out var kind) ? kind : ConditionParamKind.Numeric;

    /// <summary>Classifies a single parameter definition under this table's game numbering.</summary>
    public ConditionParamKind Classify(ScriptFunctionParamDef param) =>
        Game switch
        {
            BethesdaGame.Oblivion => ClassifyTes4(param.ObType),
            BethesdaGame.Fallout4 => ClassifyFallout4(param.Fallout4Type),
            _ => ClassifyFallout(param.Type),
        };

    // FO3/FNV CTDA scalar and enum parameter kinds. ActorValue is the numeric AV enum index, not an
    // AVIF FormID; ScriptVar likewise stays numeric as the variable index paired with a Quest param.
    private static ConditionParamKind ClassifyFallout(ScriptParamType type) => type switch
    {
        ScriptParamType.Char or
            ScriptParamType.Int or
            ScriptParamType.Float or
            ScriptParamType.ActorValue or
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

    // This is only a defensive direct-call interpretation. FO4 CTDA paths use the generated
    // condition-specific xEdit storage kinds because script parameters are not always a one-to-one
    // physical CTDA mapping (GetEventData is the canonical counterexample).
    private static ConditionParamKind ClassifyFallout4(Fallout4ScriptParamType type) => type switch
    {
        Fallout4ScriptParamType.Char or
            Fallout4ScriptParamType.Int or
            Fallout4ScriptParamType.Float or
            Fallout4ScriptParamType.Axis or
            Fallout4ScriptParamType.AnimGroup or
            Fallout4ScriptParamType.Sex or
            Fallout4ScriptParamType.ScriptVar or
            Fallout4ScriptParamType.Stage or
            Fallout4ScriptParamType.CrimeType or
            Fallout4ScriptParamType.FormType or
            Fallout4ScriptParamType.MiscStat or
            Fallout4ScriptParamType.VatsValue or
            Fallout4ScriptParamType.VatsValueData or
            Fallout4ScriptParamType.EventFunction or
            Fallout4ScriptParamType.EventFunctionMember or
            Fallout4ScriptParamType.Alignment or
            Fallout4ScriptParamType.CastingSource or
            Fallout4ScriptParamType.WardState or
            Fallout4ScriptParamType.PackageDataNumeric or
            Fallout4ScriptParamType.MovementIdleFromState or
            Fallout4ScriptParamType.MovementIdleToState or
            Fallout4ScriptParamType.SceneAction => ConditionParamKind.Numeric,
        _ => ConditionParamKind.FormId,
    };
}
