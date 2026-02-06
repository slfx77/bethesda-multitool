namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Flow control opcode constants from PDB SCRIPT_OUTPUT enum (FLOW_* section, values 16-32).
/// </summary>
public static class ScriptOpcodes
{
    public const ushort Begin = 0x0010;
    public const ushort End = 0x0011;
    public const ushort VarShort = 0x0012;
    public const ushort VarLong = 0x0013;
    public const ushort VarFloat = 0x0014;
    public const ushort Set = 0x0015;
    public const ushort If = 0x0016;
    public const ushort Else = 0x0017;
    public const ushort ElseIf = 0x0018;
    public const ushort EndIf = 0x0019;
    public const ushort While = 0x001A;
    public const ushort EndWhile = 0x001B;
    public const ushort SetRef = 0x001C;
    public const ushort ScriptName = 0x001D;
    public const ushort Return = 0x001E;
    public const ushort FlowRef = 0x001F;  // FLOW_REF — purpose unclear, skip paramLen

    /// <summary>Opcodes at or above this value are function calls (console or game).</summary>
    public const ushort MinFunctionOpcode = 0x0100;

    /// <summary>Sentinel marking end of bytecode stream.</summary>
    public const ushort ScriptDone = 0xFFFF;

    // Variable marker bytes within bytecode
    public const byte MarkerIntLocal = 0x73;    // 's' — int local variable
    public const byte MarkerFloatLocal = 0x66;  // 'f' — float local variable
    public const byte MarkerReference = 0x72;   // 'r' — reference.variable
    public const byte MarkerGlobal = 0x47;      // 'G' — global variable

    // Expression tokens
    public const byte ExprPush = 0x20;          // Push prefix (next byte determines value type)
    public const byte ExprFunctionCall = 0x58;  // Function call within expression
    public const byte ExprIntLiteral = 0x6E;    // 'n' — integer literal (4 bytes)
    public const byte ExprDoubleLiteral = 0x7A; // 'z' — double literal (8 bytes)
}
