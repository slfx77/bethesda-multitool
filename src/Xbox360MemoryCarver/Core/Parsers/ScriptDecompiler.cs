using System.Globalization;
using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Decompiler for compiled Bethesda ObScript bytecode.
///     
///     Based on xNVSE's ScriptAnalyzer implementation.
///     Reference: https://github.com/xNVSE/NVSE/blob/master/nvse/nvse/ScriptAnalyzer.cpp
///     
///     The bytecode format is BIG-ENDIAN on Xbox 360.
/// </summary>
public class ScriptDecompiler
{
    // Statement opcodes from xNVSE ScriptAnalyzer.h
    private const ushort Op_Begin = 0x10;
    private const ushort Op_End = 0x11;
    private const ushort Op_Short = 0x12;
    private const ushort Op_Long = 0x13;
    private const ushort Op_Float = 0x14;
    private const ushort Op_SetTo = 0x15;
    private const ushort Op_If = 0x16;
    private const ushort Op_Else = 0x17;
    private const ushort Op_ElseIf = 0x18;
    private const ushort Op_EndIf = 0x19;
    private const ushort Op_ReferenceFunction = 0x1C;
    private const ushort Op_ScriptName = 0x1D;
    private const ushort Op_Return = 0x1E;
    private const ushort Op_Ref = 0x1F;

    // Begin block event types (from xNVSE g_eventBlockCommandInfos, indices 0-37)
    private static readonly Dictionary<ushort, string> EventTypes = new()
    {
        [0x00] = "GameMode",
        [0x01] = "MenuMode",
        [0x02] = "OnActivate",
        [0x03] = "OnAdd",
        [0x04] = "OnEquip",
        [0x05] = "OnUnequip",
        [0x06] = "OnDrop",
        [0x07] = "SayToDone",
        [0x08] = "OnHit",
        [0x09] = "OnHitWith",
        [0x0A] = "OnDeath",
        [0x0B] = "OnMurder",
        [0x0C] = "OnCombatEnd",
        [0x0D] = "OnLoad",
        [0x0E] = "OnMagicEffectHit",
        [0x0F] = "OnTrigger",
        [0x10] = "OnTriggerEnter",
        [0x11] = "OnTriggerLeave",
        [0x12] = "OnActorEquip",
        [0x13] = "OnActorUnequip",
        [0x14] = "OnReset",
        [0x15] = "ScriptEffectStart",
        [0x16] = "ScriptEffectFinish",
        [0x17] = "ScriptEffectUpdate",
        [0x18] = "OnPackageStart",
        [0x19] = "OnPackageDone",
        [0x1A] = "OnPackageChange",
        [0x1B] = "OnStartCombat",
        [0x1C] = "OnSell",
        [0x1D] = "OnOpen",
        [0x1E] = "OnClose",
        [0x1F] = "OnGrab",
        [0x20] = "OnRelease",
        [0x21] = "OnDestructionStageChange",
        [0x22] = "OnFire",
        [0x23] = "OnNPCActivate",
        [0x24] = "SayTo",
        [0x25] = "Say"
    };

    // Common vanilla game command opcodes (partial list)
    private static readonly Dictionary<ushort, string> CommandNames = new()
    {
        // Vanilla commands start at 0x1000
        [0x1005] = "SetStage",
        [0x1006] = "GetStage",
        [0x1007] = "GetStageDone",
        [0x100C] = "GetSecondsPassed",
        [0x100F] = "Activate",
        [0x1012] = "GetDistance",
        [0x1018] = "GetPos",
        [0x1019] = "SetPos",
        [0x101A] = "GetAngle",
        [0x101B] = "SetAngle",
        [0x101C] = "GetStartingPos",
        [0x101D] = "GetStartingAngle",
        [0x1024] = "GetActorValue",
        [0x1025] = "SetActorValue",
        [0x1026] = "ModActorValue",
        [0x1027] = "GetBaseActorValue",
        [0x102A] = "GetLineOfSight",
        [0x102B] = "AddSpell",
        [0x102C] = "RemoveSpell",
        [0x102E] = "GetItemCount",
        [0x1034] = "AddItem",
        [0x1035] = "RemoveItem",
        [0x1036] = "EquipItem",
        [0x1037] = "UnequipItem",
        [0x1039] = "GetIsPlayersLastRiddenHorse",  // or similar condition
        [0x103A] = "GetActorValue",
        [0x103B] = "GetSelf",
        [0x103C] = "GetContainer",
        [0x1046] = "Enable",
        [0x1047] = "Disable",
        [0x104B] = "GetDisabled",
        [0x104E] = "PlayGroup",
        [0x104F] = "LoopGroup",
        [0x1059] = "StartConversation",
        [0x105D] = "GetDead",
        [0x1064] = "GetCurrentAIPackage",
        [0x1065] = "IsCurrentFurnitureRef",
        [0x1066] = "IsCurrentFurnitureObj",
        [0x1068] = "GetFactionRank",
        [0x1069] = "GetFactionRankDifference",
        [0x106A] = "GetDetected",
        [0x1070] = "MoveTo",
        [0x1072] = "GetRandomPercent",
        [0x1073] = "GetQuestVariable",
        [0x1078] = "Say",
        [0x1079] = "SayTo",
        [0x107A] = "GetScriptVariable",
        [0x107C] = "StartQuest",
        [0x107D] = "StopQuest",
        [0x107E] = "GetQuestRunning",
        [0x1084] = "ShowMessage",
        [0x1085] = "SetAlert",
        [0x108A] = "Kill",
        [0x108D] = "GetHeadingAngle",
        [0x108F] = "IsActionRef",
        [0x1097] = "GetInCell",
        [0x109C] = "GetIsReference",
        [0x10A0] = "SetFactionRank",
        [0x10A1] = "ModFactionRank",
        [0x10A3] = "PlaceAtMe",
        [0x10A5] = "GetIsID",
        [0x10C1] = "IsWeaponOut",
        [0x10C5] = "GetCrimeGold",
        [0x10C6] = "SetCrimeGold",
        [0x10C7] = "ModCrimeGold",
        [0x10D8] = "GetPlayerControlsDisabled",
        [0x10DF] = "DisablePlayerControls",
        [0x10E0] = "EnablePlayerControls",
        [0x10E9] = "GetDetectionLevel",
        [0x1100] = "ForceActorValue",
        [0x1101] = "ModCurrentValue",
        [0x110A] = "ResetHealth",
        [0x1137] = "GetIsCreature",
        [0x1139] = "GetPlayerTeammate",
        [0x113E] = "ShowBarterMenu",
        [0x1143] = "GetInZone",
        [0x1148] = "SetEssential",
        [0x118E] = "SetWeather",
        [0x11A2] = "ModAV",  // Common command
        [0x11A3] = "SetAV",  // Common command
        // NVSE commands start at 0x1400
        [0x1400] = "GetNVSEVersion",
        [0x1401] = "GetNVSERevision",
    };

    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _offset;
    private readonly int _size;
    private readonly bool _isBigEndian;
    private readonly List<string> _variables = [];
    private readonly List<string> _refVariables = [];
    private readonly Dictionary<uint, string> _variableNames = [];
    private readonly Dictionary<uint, string> _variableTypes = [];
    private readonly Dictionary<uint, string> _refVarNames = [];
    private int _indentLevel;
    private int _varCounter;

    /// <summary>
    ///     Create a new script decompiler.
    /// </summary>
    /// <param name="data">The raw file data.</param>
    /// <param name="offset">Offset to the start of the bytecode.</param>
    /// <param name="size">Size of the bytecode in bytes.</param>
    /// <param name="isBigEndian">If true, read multi-byte values as big-endian (Xbox 360). Default is true.</param>
    public ScriptDecompiler(ReadOnlyMemory<byte> data, int offset, int size, bool isBigEndian = true)
    {
        _data = data;
        _offset = offset;
        _size = size;
        _isBigEndian = isBigEndian;
    }

    /// <summary>
    ///     Set a known variable name from extracted metadata.
    /// </summary>
    public void SetVariableName(uint index, string name, string? type = null)
    {
        _variableNames[index] = name;
        if (!string.IsNullOrEmpty(type))
            _variableTypes[index] = type;
    }

    /// <summary>
    ///     Set a known reference variable name from extracted metadata.
    /// </summary>
    public void SetRefVariableName(uint index, string name)
    {
        _refVarNames[index] = name;
    }

    /// <summary>
    ///     Get the display name for a variable index.
    /// </summary>
    private string GetVariableName(uint index, char typeHint)
    {
        if (_variableNames.TryGetValue(index, out var name))
            return name;

        var prefix = typeHint switch
        {
            'f' => "fVar",
            's' => "iVar",
            'l' => "iVar",
            'r' => "rVar",
            _ => "var"
        };
        return $"{prefix}{index}";
    }

    /// <summary>
    ///     Get the display name for a reference variable index.
    /// </summary>
    private string GetRefName(uint index)
    {
        if (_refVarNames.TryGetValue(index, out var name))
            return name;
        return $"Ref{index}";
    }

    private ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        return _isBigEndian 
            ? BinaryUtils.ReadUInt16BE(data, offset) 
            : BinaryUtils.ReadUInt16LE(data, offset);
    }

    private uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        return _isBigEndian 
            ? BinaryUtils.ReadUInt32BE(data, offset) 
            : BinaryUtils.ReadUInt32LE(data, offset);
    }

    /// <summary>
    ///     Decompile the script bytecode to source text.
    /// </summary>
    public DecompilationResult Decompile()
    {
        var result = new DecompilationResult();
        var sb = new StringBuilder();
        var span = _data.Span;
        var pos = _offset;
        var end = _offset + _size;
        _indentLevel = 1; // Start indented since we're usually inside a Begin/End block

        try
        {
            // Check if this starts with ScriptName (full script) or not (block content)
            if (pos + 2 <= end)
            {
                var firstOpcode = ReadUInt16(span, pos);
                if (firstOpcode == Op_ScriptName)
                {
                    _indentLevel = 0;
                    result.ScriptType = "FullScript";
                }
                else
                {
                    // This is bytecode from within a Begin/End block
                    sb.AppendLine("; (Bytecode fragment - not a complete script)");
                    result.ScriptType = "Fragment";
                }
            }

            while (pos + 4 <= end && pos < span.Length)
            {
                var opcode = ReadUInt16(span, pos);
                ushort length;
                ushort refIdx = 0;
                var dataStart = pos + 4;

                // Handle ReferenceFunction (0x1C) - special structure
                if (opcode == Op_ReferenceFunction)
                {
                    if (pos + 8 > end) break;
                    refIdx = ReadUInt16(span, pos + 2);
                    opcode = ReadUInt16(span, pos + 4);
                    length = ReadUInt16(span, pos + 6);
                    dataStart = pos + 8;
                }
                else
                {
                    length = ReadUInt16(span, pos + 2);
                }

                // Safety check for obviously wrong lengths
                if (length > 2000)
                {
                    sb.AppendLine($"; [ERROR: Excessive length {length} at offset 0x{pos:X4}]");
                    break;
                }

                // Get the statement data
                var stmtData = length > 0 && dataStart + length <= span.Length
                    ? span.Slice(dataStart, length)
                    : ReadOnlySpan<byte>.Empty;

                var line = DecompileStatement(opcode, length, stmtData, refIdx, ref result);
                if (!string.IsNullOrEmpty(line))
                {
                    sb.Append(new string('\t', Math.Max(0, _indentLevel)));
                    sb.AppendLine(line);
                }

                pos = dataStart + length;
            }

            result.Success = true;
            result.DecompiledText = sb.ToString();
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.DecompiledText = sb.ToString() + $"\n; ERROR: {ex.Message}";
        }

        return result;
    }

    private string DecompileStatement(ushort opcode, ushort length, ReadOnlySpan<byte> data, ushort refIdx, ref DecompilationResult result)
    {
        // Check if it's a statement opcode (0x10-0x1F) or a command (0x1000+)
        if (opcode >= 0x1000)
        {
            return DecompileCommand(opcode, data, refIdx);
        }

        switch (opcode)
        {
            case Op_ScriptName:
                result.ScriptType = "Object";
                _indentLevel = 0;
                return "scn DecompiledScript";

            case Op_Begin:
                _indentLevel = 0;
                var beginLine = DecompileBegin(data, ref result);
                _indentLevel = 1;
                return beginLine;

            case Op_End:
                _indentLevel = 0;
                return "End";

            case Op_Short:
                return $"short iVar{_varCounter++}";

            case Op_Long:
                return $"int iVar{_varCounter++}";

            case Op_Float:
                return $"float fVar{_varCounter++}";

            case Op_Ref:
                return $"ref rVar{_varCounter++}";

            case Op_SetTo:
                return DecompileSetTo(data);

            case Op_If:
                var ifLine = DecompileConditional("If", data);
                _indentLevel++;
                return ifLine;

            case Op_ElseIf:
                _indentLevel = Math.Max(0, _indentLevel - 1);
                var elifLine = DecompileConditional("ElseIf", data);
                _indentLevel++;
                return elifLine;

            case Op_Else:
                _indentLevel = Math.Max(0, _indentLevel - 1);
                _indentLevel++;
                return "Else";

            case Op_EndIf:
                _indentLevel = Math.Max(0, _indentLevel - 1);
                return "EndIf";

            case Op_Return:
                return "Return";

            case Op_ReferenceFunction:
                // Should not reach here - handled in main loop
                return "; ReferenceFunction (unexpected)";

            default:
                return $"; Unknown statement opcode 0x{opcode:X2}";
        }
    }

    private string DecompileBegin(ReadOnlySpan<byte> data, ref DecompilationResult result)
    {
        // Begin block format: eventType(2) + endJumpLength(4) + [optional args]
        if (data.Length < 6) return "Begin ; (insufficient data)";

        var eventType = ReadUInt16(data, 0);
        // var endJumpLength = ReadUInt32(data, 2); // Skip to end of block

        var eventName = EventTypes.TryGetValue(eventType, out var name) 
            ? name 
            : $"UnknownEvent_0x{eventType:X2}";

        result.EventBlocks.Add(eventName);

        return $"Begin {eventName}";
    }

    private string DecompileSetTo(ReadOnlySpan<byte> data)
    {
        // Set statement format: [refIdx(2) if starts with 'r'] + varType(1) + varIdx(2) + expression
        if (data.Length < 3) return "Set ; (insufficient data)";

        var sb = new StringBuilder("Set ");
        var pos = 0;

        // Read variable reference
        var varType = (char)data[pos++];
        
        if (varType == 'r' && data.Length >= pos + 3)
        {
            // Reference to variable in another script
            var scriptRefIdx = ReadUInt16(data, pos);
            pos += 2;
            sb.Append($"Ref{scriptRefIdx}.");
            varType = (char)data[pos++];
        }

        if ((varType == 'f' || varType == 's') && data.Length >= pos + 2)
        {
            var varIdx = ReadUInt16(data, pos);
            pos += 2;
            var prefix = varType == 'f' ? "fVar" : "iVar";
            sb.Append($"{prefix}{varIdx}");
        }
        else if (varType == 'G' && data.Length >= pos + 2)
        {
            var globalRefIdx = ReadUInt16(data, pos);
            pos += 2;
            sb.Append($"Global{globalRefIdx}");
        }
        else
        {
            sb.Append($"unknown_{varType}");
        }

        sb.Append(" To ");

        // Parse expression
        if (data.Length > pos)
        {
            sb.Append(DecompileExpression(data.Slice(pos)));
        }
        else
        {
            sb.Append("; (no expression)");
        }

        return sb.ToString();
    }

    private string DecompileConditional(string keyword, ReadOnlySpan<byte> data)
    {
        // Conditional format: jumpOffset(2) + expression
        if (data.Length < 2) return $"{keyword} ; (insufficient data)";

        var jumpOffset = ReadUInt16(data, 0);

        if (data.Length > 2)
        {
            var expr = DecompileExpression(data.Slice(2));
            return $"{keyword} {expr}";
        }

        return $"{keyword} ; (jump={jumpOffset})";
    }

    private string DecompileExpression(ReadOnlySpan<byte> data)
    {
        // Expression format: length(2) + tokens in POSTFIX notation
        if (data.Length < 2) return "(empty)";

        var exprLen = ReadUInt16(data, 0);
        if (exprLen == 0 || data.Length < 2 + exprLen) 
            return "(empty)";

        var exprData = data.Slice(2, Math.Min(exprLen, data.Length - 2));
        var tokens = new List<string>();
        var pos = 0;

        while (pos < exprData.Length)
        {
            var code = exprData[pos++];

            // Skip whitespace
            if (code == ' ' || code == '\t' || code == '\n' || code == '\r' || code == 0)
                continue;

            switch ((char)code)
            {
                case 's': // Short variable
                case 'l': // Long variable  
                case 'f': // Float variable
                    if (pos + 2 <= exprData.Length)
                    {
                        var varIdx = ReadUInt16(exprData, pos);
                        pos += 2;
                        var prefix = (char)code == 'f' ? "fVar" : "iVar";
                        tokens.Add($"{prefix}{varIdx}");
                    }
                    break;

                case 'G': // Global variable
                    if (pos + 2 <= exprData.Length)
                    {
                        var globalIdx = ReadUInt16(exprData, pos);
                        pos += 2;
                        tokens.Add($"Global{globalIdx}");
                    }
                    break;

                case 'Z': // Form reference
                    if (pos + 2 <= exprData.Length)
                    {
                        var refIdx = ReadUInt16(exprData, pos);
                        pos += 2;
                        tokens.Add($"Form{refIdx}");
                    }
                    break;

                case 'r': // Reference to variable in another script
                    if (pos + 2 <= exprData.Length)
                    {
                        var refIdx = ReadUInt16(exprData, pos);
                        pos += 2;
                        tokens.Add($"Ref{refIdx}");
                    }
                    break;

                case 'n': // Integer constant (4 bytes)
                    if (pos + 4 <= exprData.Length)
                    {
                        var intVal = (int)ReadUInt32(exprData, pos);
                        pos += 4;
                        tokens.Add(intVal.ToString(CultureInfo.InvariantCulture));
                    }
                    break;

                case 'z': // Double constant (8 bytes)
                    if (pos + 8 <= exprData.Length)
                    {
                        var bytes = exprData.Slice(pos, 8).ToArray();
                        if (_isBigEndian)
                            Array.Reverse(bytes);
                        var doubleVal = BitConverter.ToDouble(bytes, 0);
                        pos += 8;
                        tokens.Add(doubleVal.ToString("G", CultureInfo.InvariantCulture));
                    }
                    break;

                case '"': // String literal
                    if (pos + 2 <= exprData.Length)
                    {
                        var strLen = ReadUInt16(exprData, pos);
                        pos += 2;
                        if (pos + strLen <= exprData.Length)
                        {
                            var str = Encoding.ASCII.GetString(exprData.Slice(pos, strLen).ToArray());
                            pos += strLen;
                            tokens.Add($"\"{str}\"");
                        }
                    }
                    break;

                case 'X': // Command call
                    if (pos + 4 <= exprData.Length)
                    {
                        var cmdOpcode = ReadUInt16(exprData, pos);
                        var cmdLen = ReadUInt16(exprData, pos + 2);
                        pos += 4;
                        var cmdName = CommandNames.TryGetValue(cmdOpcode, out var name) 
                            ? name 
                            : $"Cmd_0x{cmdOpcode:X4}";
                        tokens.Add(cmdName);
                        pos += cmdLen; // Skip command arguments
                    }
                    break;

                // Multi-character operators - check for double characters
                case '=':
                    // Check if next char is also '=' for ==
                    if (pos < exprData.Length && exprData[pos] == '=' )
                    {
                        pos++;
                        tokens.Add("OP_EQ"); // Use marker for postfix processing
                    }
                    else
                    {
                        tokens.Add("OP_EQ");
                    }
                    break;

                case '!':
                    if (pos < exprData.Length && exprData[pos] == '=' )
                    {
                        pos++;
                        tokens.Add("OP_NE");
                    }
                    else
                    {
                        tokens.Add("OP_NOT");
                    }
                    break;

                case '&':
                    if (pos < exprData.Length && exprData[pos] == '&')
                    {
                        pos++;
                        tokens.Add("OP_AND");
                    }
                    else
                    {
                        tokens.Add("&");
                    }
                    break;

                case '|':
                    if (pos < exprData.Length && exprData[pos] == '|')
                    {
                        pos++;
                        tokens.Add("OP_OR");
                    }
                    else
                    {
                        tokens.Add("|");
                    }
                    break;

                case '<':
                    if (pos < exprData.Length && exprData[pos] == '=')
                    {
                        pos++;
                        tokens.Add("OP_LE");
                    }
                    else
                    {
                        tokens.Add("OP_LT");
                    }
                    break;

                case '>':
                    if (pos < exprData.Length && exprData[pos] == '=')
                    {
                        pos++;
                        tokens.Add("OP_GE");
                    }
                    else
                    {
                        tokens.Add("OP_GT");
                    }
                    break;

                // Single-character operators
                case '+': tokens.Add("OP_ADD"); break;
                case '-': tokens.Add("OP_SUB"); break;
                case '*': tokens.Add("OP_MUL"); break;
                case '/': tokens.Add("OP_DIV"); break;
                case '%': tokens.Add("OP_MOD"); break;
                case '^': tokens.Add("OP_POW"); break;
                
                default:
                    // Check if it's a numeric ASCII literal (0-9, -, .)
                    if (code >= '0' && code <= '9' || code == '.' || (code == '-' && pos < exprData.Length && exprData[pos] >= '0' && exprData[pos] <= '9'))
                    {
                        // Read the entire number as ASCII
                        var numStart = pos - 1;
                        while (pos < exprData.Length)
                        {
                            var c = exprData[pos];
                            if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || 
                                (c == '-' && pos > numStart && (exprData[pos-1] == 'e' || exprData[pos-1] == 'E')))
                                pos++;
                            else
                                break;
                        }
                        var numStr = Encoding.ASCII.GetString(exprData.Slice(numStart, pos - numStart).ToArray());
                        tokens.Add(numStr);
                    }
                    else if (code > 0x20)
                    {
                        // Unknown non-whitespace - show it
                        tokens.Add($"[0x{code:X2}]");
                    }
                    break;
            }
        }

        // Convert from postfix to infix notation
        return PostfixToInfix(tokens);
    }

    /// <summary>
    ///     Convert postfix (RPN) token list to infix notation string.
    /// </summary>
    private static string PostfixToInfix(List<string> tokens)
    {
        if (tokens.Count == 0) return "(empty)";
        if (tokens.Count == 1) return tokens[0];

        var stack = new Stack<string>();
        var operators = new HashSet<string>
        {
            "OP_EQ", "OP_NE", "OP_LT", "OP_GT", "OP_LE", "OP_GE",
            "OP_AND", "OP_OR", "OP_NOT",
            "OP_ADD", "OP_SUB", "OP_MUL", "OP_DIV", "OP_MOD", "OP_POW"
        };

        var opSymbols = new Dictionary<string, string>
        {
            ["OP_EQ"] = "==",
            ["OP_NE"] = "!=",
            ["OP_LT"] = "<",
            ["OP_GT"] = ">",
            ["OP_LE"] = "<=",
            ["OP_GE"] = ">=",
            ["OP_AND"] = "&&",
            ["OP_OR"] = "||",
            ["OP_NOT"] = "!",
            ["OP_ADD"] = "+",
            ["OP_SUB"] = "-",
            ["OP_MUL"] = "*",
            ["OP_DIV"] = "/",
            ["OP_MOD"] = "%",
            ["OP_POW"] = "^"
        };

        foreach (var token in tokens)
        {
            if (operators.Contains(token))
            {
                var opStr = opSymbols[token];
                
                if (token == "OP_NOT")
                {
                    // Unary operator
                    if (stack.Count >= 1)
                    {
                        var operand = stack.Pop();
                        stack.Push($"!{operand}");
                    }
                    else
                    {
                        stack.Push($"!?");
                    }
                }
                else
                {
                    // Binary operator
                    if (stack.Count >= 2)
                    {
                        var right = stack.Pop();
                        var left = stack.Pop();
                        stack.Push($"({left} {opStr} {right})");
                    }
                    else if (stack.Count == 1)
                    {
                        var operand = stack.Pop();
                        stack.Push($"(? {opStr} {operand})");
                    }
                    else
                    {
                        stack.Push($"(? {opStr} ?)");
                    }
                }
            }
            else
            {
                // Operand - push to stack
                stack.Push(token);
            }
        }

        // Combine remaining items on stack
        if (stack.Count == 1)
        {
            return stack.Pop();
        }
        else if (stack.Count > 1)
        {
            // Multiple items left - join them (shouldn't normally happen)
            return string.Join(" ", stack.Reverse());
        }

        return "(parse error)";
    }

    private string DecompileCommand(ushort opcode, ReadOnlySpan<byte> data, ushort refIdx)
    {
        var sb = new StringBuilder();

        // Add calling reference if present
        if (refIdx > 0)
        {
            sb.Append($"Ref{refIdx}.");
        }

        // Get command name
        var cmdName = CommandNames.TryGetValue(opcode, out var name) 
            ? name 
            : $"Cmd_0x{opcode:X4}";
        sb.Append(cmdName);

        // Parse arguments (simplified)
        if (data.Length >= 2)
        {
            var numArgs = ReadUInt16(data, 0);
            if (numArgs > 0 && numArgs < 20 && data.Length > 2)
            {
                sb.Append(' ');
                // Try to parse arguments
                var pos = 2;
                for (var i = 0; i < numArgs && pos < data.Length; i++)
                {
                    var argCode = (char)data[pos++];
                    switch (argCode)
                    {
                        case 'r': // Reference
                            if (pos + 2 <= data.Length)
                            {
                                var refId = ReadUInt16(data, pos);
                                pos += 2;
                                sb.Append($"Ref{refId}");
                            }
                            break;
                        case 'n': // Integer
                            if (pos + 4 <= data.Length)
                            {
                                var val = (int)ReadUInt32(data, pos);
                                pos += 4;
                                sb.Append(val.ToString(CultureInfo.InvariantCulture));
                            }
                            break;
                        case 'f': case 's': // Variable
                            if (pos + 2 <= data.Length)
                            {
                                var varIdx = ReadUInt16(data, pos);
                                pos += 2;
                                var prefix = argCode == 'f' ? "fVar" : "iVar";
                                sb.Append($"{prefix}{varIdx}");
                            }
                            break;
                        default:
                            // Unknown arg format
                            break;
                    }
                    if (i < numArgs - 1) sb.Append(' ');
                }
            }
        }

        return sb.ToString();
    }
}

/// <summary>
///     Result of script decompilation.
/// </summary>
public class DecompilationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string DecompiledText { get; set; } = string.Empty;
    public string ScriptType { get; set; } = "Unknown";
    public List<string> EventBlocks { get; } = [];
}
