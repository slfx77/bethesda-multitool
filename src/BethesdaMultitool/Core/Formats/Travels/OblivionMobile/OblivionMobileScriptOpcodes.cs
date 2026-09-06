namespace BethesdaMultitool.Core.Formats.Travels.OblivionMobile;

/// <summary>
///     The instruction set of the Oblivion mobile (2006, J2ME) <c>.scr</c> byte-code — one row per
///     opcode the interpreter has a case for, giving the mnemonic and the exact operand widths.
///     Original RE (2026-09-05) from the 32 retail scripts; the widths are proven by tiling, the
///     names are not (see <see cref="OblivionMobileOpcodeInfo.Observed" />).
///     <para>
///         Why the table carries opcodes that never occur: 61 of the 79 codes appear in the retail
///         corpus, and the other 18 are dead only in *this* build. An operand-width table with
///         holes cannot skip an instruction it has never seen, so the first script from another
///         build that uses one would desync the decoder for the rest of the chunk and produce
///         plausible-looking garbage rather than an error. Every row therefore has a width, and
///         the rows nothing in the corpus reaches are flagged <c>Observed == false</c> — their
///         widths are the interpreter's, not something measured here, so treat a decode that
///         relies on one as unverified.
///     </para>
///     <para>
///         Opcodes 79 and above are rejected. The interpreter itself ignores them (no operands, no
///         error), but a decoder cannot tell "a future opcode" from "we lost sync three
///         instructions ago", and silently continuing is how a desync turns into wrong output
///         instead of a thrown message naming the byte.
///     </para>
/// </summary>
internal static class OblivionMobileScriptOpcodes
{
    /// <summary>Highest opcode with an interpreter case; 79 and up are ignored by the engine.</summary>
    public const byte MaxOpcode = 78;

    /// <summary>Pops the call stack. Every retail chunk's last instruction is this one.</summary>
    public const byte Return = 2;

    /// <summary>Pushes another label onto the call stack (its operand is a label id).</summary>
    public const byte Call = 23;

    /// <summary>Loads a map: a <c>.jtm</c> tile map and then the <c>.cml</c> sprite set beside it.</summary>
    public const byte LoadMap = 8;

    /// <summary>Replaces the running script with another <c>.scr</c>.</summary>
    public const byte LoadScript = 29;

    /// <summary>Selects the <c>lang_N.txt</c> overlay; the string operand is read and discarded.</summary>
    public const byte LoadLang = 56;

    /// <summary>Preloads a PNG. The most common instruction in the corpus by a wide margin (684).</summary>
    public const byte LoadImage = 72;

    /// <summary>Sprite command whose sub-command byte selects the operand width that follows.</summary>
    public const byte SpriteCommand = 34;

    private const OblivionMobileOperandForm U8 = OblivionMobileOperandForm.UInt8;
    private const OblivionMobileOperandForm U16 = OblivionMobileOperandForm.UInt16;
    private const OblivionMobileOperandForm U24 = OblivionMobileOperandForm.UInt24;
    private const OblivionMobileOperandForm S8 = OblivionMobileOperandForm.Text8;
    private const OblivionMobileOperandForm S16 = OblivionMobileOperandForm.Text16;
    private const OblivionMobileOperandForm Txt = OblivionMobileOperandForm.TextRef;
    private const OblivionMobileOperandForm Txt0 = OblivionMobileOperandForm.OptionalTextRef;
    private const OblivionMobileOperandForm Lab = OblivionMobileOperandForm.LabelId;
    private const OblivionMobileOperandForm Oid = OblivionMobileOperandForm.ObjectSlot;
    private const OblivionMobileOperandForm Lst = OblivionMobileOperandForm.ByteList;
    private const OblivionMobileOperandForm Cmd = OblivionMobileOperandForm.SubCommand;

    private static readonly OblivionMobileOpcodeInfo?[] TableByCode = BuildTable();

    private static readonly byte[] SubCommandWidths = BuildSubCommandWidths();

    /// <summary>
    ///     The row for <paramref name="opcode" />, or null when the interpreter has no case for it
    ///     (79 and above). A caller that gets null must stop rather than assume no operands.
    /// </summary>
    public static OblivionMobileOpcodeInfo? Find(byte opcode)
    {
        return opcode < TableByCode.Length ? TableByCode[opcode] : null;
    }

    /// <summary>
    ///     Operand bytes that follow sub-command <paramref name="subCommand" /> of opcode 34.
    ///     Sub-commands 2-6, 8-13 and 18-20 take one byte, 7/14/15 take a big-endian u16, and
    ///     everything else takes nothing at all — which is why a decoder cannot treat 34 as a
    ///     fixed-width instruction. Retail uses 11 of the sub-commands (14 x69, 7 x32, 15 x29).
    /// </summary>
    public static int SubCommandOperandWidth(byte subCommand)
    {
        return SubCommandWidths[subCommand];
    }

    private static byte[] BuildSubCommandWidths()
    {
        var widths = new byte[256];
        foreach (var cmd in new byte[] { 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 18, 19, 20 })
        {
            widths[cmd] = 1;
        }

        foreach (var cmd in new byte[] { 7, 14, 15 })
        {
            widths[cmd] = 2;
        }

        return widths;
    }

    private static OblivionMobileOpcodeInfo?[] BuildTable()
    {
        var table = new OblivionMobileOpcodeInfo?[MaxOpcode + 1];

        // Observed in the retail corpus: 61 opcodes, 3,844 instructions, widths proven by the
        // fact that decoding every chunk lands exactly on the next label marker.
        Add(table, 0, "ERR0", true);
        Add(table, 2, "RET", true);
        Add(table, 3, "SAY", true, Txt);
        Add(table, 7, "SETFLAG39", true, U8);
        Add(table, 8, "LOADMAP", true, S8, S8);
        Add(table, 9, "NOP9", true, S8);
        Add(table, 10, "SETMODE", true, U8, U24);
        Add(table, 11, "WAIT", true, U16);
        Add(table, 12, "STATE0", true);
        Add(table, 15, "DIALOG", true, Txt0, U8, U8, U16, U16);
        Add(table, 16, "TILE", true, U8, U8, U8, U8, U8);
        Add(table, 17, "SPRPOS", true, Oid, U16, U16);
        Add(table, 18, "OP18", true, U8, U8, U8, U8);
        Add(table, 19, "OP19", true, U8);
        Add(table, 20, "OP20", true, U8);
        Add(table, 21, "WAITSPR", true, Lst);
        Add(table, 22, "OP22", true, U8, U8, U8);
        Add(table, 23, "CALL", true, Lab);
        Add(table, 24, "SPRANIM", true, Oid, U8);
        Add(table, 25, "OP25", true, U16, U16);
        Add(table, 26, "OP26", true, U8);
        Add(table, 27, "TILECLR", true, U8, U8);
        Add(table, 29, "LOADSCR", true, S8);
        Add(table, 32, "OP32", true, Oid, U8, U8);
        Add(table, 34, "SPRCMD", true, Oid, Cmd);
        Add(table, 35, "OP35", true, U8);
        Add(table, 36, "OP36", true, Oid, U16, U16);
        Add(table, 37, "GIVE", true, Oid, U8, U8);
        Add(table, 39, "MSG", true, Txt, U8, U8, U8);
        Add(table, 40, "MSGCLR", true);
        Add(table, 41, "SPRX", true, Oid, U16);
        Add(table, 42, "SPRY", true, Oid, U16);
        Add(table, 43, "OP43", true, S8);
        Add(table, 44, "OP44", true);
        Add(table, 45, "OP45", true);
        Add(table, 46, "OP46", true, Oid, U8);
        Add(table, 47, "SHOP", true, U8, U8, U8);
        Add(table, 49, "OP49", true, U8, U8, U8);
        Add(table, 50, "TILERECT", true, U8, U8, U8, U8, U8, U8, U8);
        Add(table, 51, "TILERECTCLR", true, U8, U8, U8, U8);
        Add(table, 52, "WALKTO", true, U8, U8, U16, U16);
        Add(table, 53, "SAYAT", true, U8, U8, Txt);
        Add(table, 56, "LOADLANG", true, S8, U8);
        Add(table, 58, "SETLEVEL", true, U8, U8);
        Add(table, 59, "OP59", true, Oid, U8);
        Add(table, 60, "OP60", true);
        Add(table, 61, "STATE9", true);
        Add(table, 64, "SETCOLOR", true, U24);
        Add(table, 66, "OP66", true, Txt);
        Add(table, 67, "RESETACTOR", true, U8);
        Add(table, 68, "SOUND", true, U8, U16, U16);
        Add(table, 69, "SOUNDT", true, U8, U16, U16, U8);
        Add(table, 70, "OP70", true, U16, U16);
        Add(table, 71, "OP71", true, U16, U16);
        Add(table, 72, "LOADIMG", true, S16);
        Add(table, 73, "STATE15", true);
        Add(table, 74, "OP74", true);
        Add(table, 75, "OP75", true, Oid);
        Add(table, 76, "OP76", true, U8);
        Add(table, 77, "STATE4", true);
        Add(table, 78, "OP78", true, Oid, U8);

        // Unobserved: the interpreter has a case for each, but nothing in the 32 retail scripts
        // reaches them. Widths carried so a script from another build still decodes; they are the
        // one part of this table the fixture bytes do not prove.
        Add(table, 1, "OP1", false);
        Add(table, 4, "OP4", false, U8, U8);
        Add(table, 5, "OP5", false, U8);
        Add(table, 6, "OP6", false, U8);
        Add(table, 13, "OP13", false, U8);
        Add(table, 14, "OP14", false, U8, Lab);
        Add(table, 28, "OP28", false, U8);
        Add(table, 30, "OP30", false);
        Add(table, 31, "OP31", false);
        Add(table, 33, "OP33", false, U8, U8);
        Add(table, 38, "OP38", false, Oid, U8, U8);
        Add(table, 48, "OP48", false);
        Add(table, 54, "OP54", false);
        Add(table, 55, "OP55", false);
        Add(table, 57, "OP57", false);
        Add(table, 62, "OP62", false);
        Add(table, 63, "OP63", false);
        Add(table, 65, "OP65", false, Oid, U8);

        return table;
    }

    private static void Add(
        OblivionMobileOpcodeInfo?[] table,
        byte code,
        string mnemonic,
        bool observed,
        params OblivionMobileOperandForm[] operands)
    {
        table[code] = new OblivionMobileOpcodeInfo(code, mnemonic, observed, operands);
    }
}

/// <summary>
///     How one operand is laid out. <c>Text8</c>/<c>Text16</c> are length-prefixed byte strings
///     (u8 and big-endian u16 length respectively, ISO-8859-1 bytes). <c>TextRef</c> is the
///     engine's "lang id or inline string" word — see <see cref="OblivionMobileScript" /> for the
///     trap that the code form is two bytes wide while the definition-block form is one.
/// </summary>
internal enum OblivionMobileOperandForm
{
    /// <summary>One unsigned byte.</summary>
    UInt8,

    /// <summary>Two bytes, big-endian.</summary>
    UInt16,

    /// <summary>Three bytes, big-endian — only ever an RGB colour in the corpus.</summary>
    UInt24,

    /// <summary>u8 length followed by that many ISO-8859-1 bytes.</summary>
    Text8,

    /// <summary>Big-endian u16 length followed by that many ISO-8859-1 bytes.</summary>
    Text16,

    /// <summary>Big-endian u16: <c>0xFxxx</c> is lang id <c>xxx</c>, otherwise an inline length.</summary>
    TextRef,

    /// <summary>As <see cref="TextRef" />, but a zero word means "no text" rather than an empty one.</summary>
    OptionalTextRef,

    /// <summary>One byte naming a label in this script's own table.</summary>
    LabelId,

    /// <summary>One byte naming a live object/actor slot.</summary>
    ObjectSlot,

    /// <summary>u8 count followed by that many bytes.</summary>
    ByteList,

    /// <summary>Opcode 34's sub-command byte, whose value picks the width that follows it.</summary>
    SubCommand,
}

/// <summary>One interpreter case: the opcode, this spec's mnemonic, and its operand widths.</summary>
/// <param name="Code">The opcode byte.</param>
/// <param name="Mnemonic">This decoder's name for it. Names are descriptive, not the engine's.</param>
/// <param name="Observed">
///     True when the retail corpus contains at least one of these. False means the width came from
///     the interpreter's reader rather than from measured bytes.
/// </param>
/// <param name="Operands">Operand forms, in the order the bytes follow the opcode.</param>
internal sealed record OblivionMobileOpcodeInfo(
    byte Code,
    string Mnemonic,
    bool Observed,
    IReadOnlyList<OblivionMobileOperandForm> Operands);
