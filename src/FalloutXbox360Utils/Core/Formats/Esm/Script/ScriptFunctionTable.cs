namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Lookup table for all script function definitions.
///     The actual data is in the generated partial class ScriptFunctionTable.Generated.cs.
/// </summary>
public static partial class ScriptFunctionTable
{
    /// <summary>
    ///     Look up a function definition by opcode.
    /// </summary>
    public static ScriptFunctionDef? Get(ushort opcode)
    {
        return _functions.GetValueOrDefault(opcode);
    }

    /// <summary>
    ///     Get the friendly name of a function by opcode.
    ///     Returns the short name if available, otherwise the full name.
    /// </summary>
    public static string GetName(ushort opcode)
    {
        if (_functions.TryGetValue(opcode, out var def))
        {
            return def.Name;
        }

        return $"UnknownFunc_0x{opcode:X4}";
    }
}
