using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     One game's script command table as an instance lookup. The same table serves two consumers:
///     the Obscript bytecode decompiler (function-call opcodes) and CTDA condition display/decoding
///     (condition function index → <c>opcode = 0x1000 | index</c>). Obtain via
///     <see cref="ScriptFunctionTables.For" />.
/// </summary>
public sealed class ScriptFunctionSet
{
    private readonly IReadOnlyDictionary<ushort, ScriptFunctionDef> _functions;

    internal ScriptFunctionSet(BethesdaGame game, IReadOnlyDictionary<ushort, ScriptFunctionDef> functions)
    {
        Game = game;
        _functions = functions;
    }

    public BethesdaGame Game { get; }

    public ScriptFunctionDef? Get(ushort opcode) => _functions.GetValueOrDefault(opcode);

    /// <summary>Condition-function lookup: <paramref name="functionIndex" /> is the raw CTDA index.</summary>
    public ScriptFunctionDef? GetConditionFunction(ushort functionIndex) =>
        Get((ushort)(0x1000 | functionIndex));

    public string GetName(ushort opcode) =>
        _functions.TryGetValue(opcode, out var def) ? def.Name : $"UnknownFunc_0x{opcode:X4}";
}
