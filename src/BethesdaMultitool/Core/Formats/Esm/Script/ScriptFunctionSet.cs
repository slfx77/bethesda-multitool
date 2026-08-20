using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     One game's script-command and condition-function lookups. Script commands are keyed by their
///     bytecode opcode; CTDA functions and their metadata are keyed independently by the raw function
///     index stored in the record. Some classic engines share definitions across those domains, but
///     there is no universal index-to-opcode transform. Obtain via <see cref="ScriptFunctionTables.For" />.
/// </summary>
public sealed class ScriptFunctionSet
{
    private readonly IReadOnlyDictionary<ushort, ScriptFunctionDef>? _conditionFunctions;
    private readonly IReadOnlyDictionary<ushort, ConditionParamKind?[]>? _conditionParamKinds;
    private readonly IReadOnlyDictionary<ushort, bool[]>? _conditionTypeOverrideEligibility;
    private readonly IReadOnlyDictionary<ushort, ScriptFunctionDef> _functions;
    private readonly bool _useLegacyConditionOpcodeProjection;

    internal ScriptFunctionSet(
        BethesdaGame game,
        IReadOnlyDictionary<ushort, ScriptFunctionDef> functions,
        IReadOnlyDictionary<ushort, ScriptFunctionDef>? conditionFunctionsByIndex = null,
        IReadOnlyDictionary<ushort, ConditionParamKind?[]>? conditionParamKindsByIndex = null,
        IReadOnlyDictionary<ushort, bool[]>? conditionTypeOverrideEligibilityByIndex = null,
        bool useLegacyConditionOpcodeProjection = false)
    {
        Game = game;
        _functions = functions;
        _conditionFunctions = conditionFunctionsByIndex;
        _conditionParamKinds = conditionParamKindsByIndex;
        _conditionTypeOverrideEligibility = conditionTypeOverrideEligibilityByIndex;
        _useLegacyConditionOpcodeProjection = useLegacyConditionOpcodeProjection;
    }

    public BethesdaGame Game { get; }

    internal bool HasAuthoritativeConditionParamKinds => _conditionParamKinds is not null;

    public ScriptFunctionDef? Get(ushort opcode)
    {
        return _functions.GetValueOrDefault(opcode);
    }

    /// <summary>
    ///     Looks up the raw CTDA <paramref name="functionIndex" />. An explicit condition map is
    ///     authoritative even when empty. A null map fails closed unless the set explicitly enables
    ///     the bounded classic-engine compatibility projection for a separately evidenced caller.
    /// </summary>
    public ScriptFunctionDef? GetConditionFunction(ushort functionIndex)
    {
        if (_conditionFunctions is not null)
        {
            return _conditionFunctions.GetValueOrDefault(functionIndex);
        }

        // This projection is evidenced only for classic table indices below the game-command base.
        // In particular, FO76 has live raw indices above 0x0FFF that collide under bitwise OR.
        return _useLegacyConditionOpcodeProjection && functionIndex < 0x1000
            ? Get((ushort)(0x1000 + functionIndex))
            : null;
    }

    internal bool IsConditionTypeOverrideEligible(ushort functionIndex, int paramIndex)
    {
        return paramIndex >= 0 &&
               _conditionTypeOverrideEligibility is not null &&
               _conditionTypeOverrideEligibility.TryGetValue(
                   functionIndex, out var parameters) &&
               paramIndex < parameters.Length &&
               parameters[paramIndex];
    }

    internal bool TryGetConditionParamKind(
        ushort functionIndex, int paramIndex, out ConditionParamKind kind)
    {
        kind = default;
        if (_conditionParamKinds is null || paramIndex < 0 ||
            !_conditionParamKinds.TryGetValue(functionIndex, out var parameters) ||
            paramIndex >= parameters.Length || parameters[paramIndex] is not { } classified)
        {
            return false;
        }

        kind = classified;
        return true;
    }

    public string GetName(ushort opcode)
    {
        return _functions.TryGetValue(opcode, out var def) ? def.Name : $"UnknownFunc_0x{opcode:X4}";
    }
}
