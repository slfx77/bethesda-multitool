using System.Globalization;
using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Scda;

/// <summary>
///     Decompiles compiled Fallout: New Vegas script bytecode back into readable script format.
///     Based on Oblivion SCPT format documentation and opcode analysis from PDB symbols.
/// </summary>
public sealed class ScdaDecompiler
{
    private readonly Dictionary<ushort, (string Name, string Category)> _opcodeTable = new();
    private string? _currentRef;

    public ScdaDecompiler()
    {
        LoadDefaultOpcodeTable();
    }

    /// <summary>
    ///     Load opcode table from CSV file.
    /// </summary>
    public async Task LoadOpcodeTableAsync(string csvPath)
    {
        if (!File.Exists(csvPath)) return;

        var lines = await File.ReadAllLinesAsync(csvPath);
        foreach (var line in lines.Skip(1)) // Skip header
        {
            var parts = line.Split(',');
            if (parts.Length >= 4 && ushort.TryParse(parts[0], out var opcode))
            {
                _opcodeTable[opcode] = (parts[2], parts[3]);
            }
        }
    }

    /// <summary>
    ///     Decompile bytecode to readable script format.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3776:Cognitive Complexity", Justification = "Bytecode decompiler requires complex switch logic")]
    public string Decompile(byte[] bytecode)
    {
        var sb = new StringBuilder();
        var indent = 0;
        var pos = 0;

        string Indent() => new('\t', indent);

        while (pos < bytecode.Length - 1)
        {
            var opcode = BinaryUtils.ReadUInt16LE(bytecode, pos);

            // Core opcodes (control flow)
            switch (opcode)
            {
                case 0x0010: // Begin
                    {
                        var modeLen = BinaryUtils.ReadUInt16LE(bytecode, pos + 2);
                        var mode = modeLen > 0 ? BinaryUtils.ReadUInt16LE(bytecode, pos + 4) : 0;
                        var blockName = GetBlockTypeName(mode);
                        sb.AppendLine($"{Indent()}Begin {blockName}");
                        indent++;
                        pos += 4 + modeLen;
                        continue;
                    }

                case 0x0011: // End
                    indent = Math.Max(0, indent - 1);
                    sb.AppendLine($"{Indent()}End");
                    pos += 4;
                    continue;

                case 0x0015: // Set
                    {
                        var setLen = BinaryUtils.ReadUInt16LE(bytecode, pos + 2);
                        var (varName, varBytes) = ParseVariable(bytecode, pos + 4);
                        var exprLenOffset = pos + 4 + varBytes;
                        var exprLen = BinaryUtils.ReadUInt16LE(bytecode, exprLenOffset);
                        var exprStart = exprLenOffset + 2;
                        var expr = ParseExpression(bytecode, exprStart, exprLen);
                        sb.AppendLine($"{Indent()}set {varName} to {expr}");
                        pos += 4 + setLen;
                        continue;
                    }

                case 0x0016: // If
                    {
                        var compLen = BinaryUtils.ReadUInt16LE(bytecode, pos + 2);
                        var exprLen = BinaryUtils.ReadUInt16LE(bytecode, pos + 6);
                        var expr = ParseExpression(bytecode, pos + 8, exprLen);
                        sb.AppendLine($"{Indent()}if ({expr})");
                        indent++;
                        pos += 4 + compLen;
                        continue;
                    }

                case 0x0017: // Else
                    {
                        var elseLen = BinaryUtils.ReadUInt16LE(bytecode, pos + 2);
                        indent = Math.Max(0, indent - 1);
                        sb.AppendLine($"{Indent()}else");
                        indent++;
                        pos += 4 + elseLen;
                        continue;
                    }

                case 0x0018: // ElseIf
                    {
                        var elifLen = BinaryUtils.ReadUInt16LE(bytecode, pos + 2);
                        var exprLen = BinaryUtils.ReadUInt16LE(bytecode, pos + 6);
                        var expr = ParseExpression(bytecode, pos + 8, exprLen);
                        indent = Math.Max(0, indent - 1);
                        sb.AppendLine($"{Indent()}elseif ({expr})");
                        indent++;
                        pos += 4 + elifLen;
                        continue;
                    }

                case 0x0019: // EndIf
                    indent = Math.Max(0, indent - 1);
                    sb.AppendLine($"{Indent()}endif");
                    pos += 4;
                    continue;

                case 0x001C: // SetRef - sets implicit reference for next call
                    {
                        var refIdx = BinaryUtils.ReadUInt16LE(bytecode, pos + 2);
                        _currentRef = $"SCRO#{refIdx}";
                        pos += 4;
                        continue;
                    }

                case 0x001D: // ScriptName
                    sb.AppendLine($"{Indent()}ScriptName");
                    pos += 4;
                    continue;

                case 0x001E: // Return
                    sb.AppendLine($"{Indent()}Return");
                    pos += 4;
                    continue;
            }

            // FUNCTION_* opcodes (0x100+)
            if (opcode >= 0x100)
            {
                var name = _opcodeTable.TryGetValue(opcode, out var info) ? info.Name : $"Function_{opcode:X4}";
                var paramLen = BinaryUtils.ReadUInt16LE(bytecode, pos + 2);

                string call;
                if (paramLen == 0)
                {
                    call = name;
                }
                else
                {
                    var paramCount = BinaryUtils.ReadUInt16LE(bytecode, pos + 4);
                    var paramStr = ParseParameters(bytecode, pos + 6, paramLen - 2, paramCount);
                    call = paramStr.Length > 0 ? $"{name} {paramStr}" : name;
                }

                if (_currentRef != null)
                {
                    sb.AppendLine($"{Indent()}{_currentRef}.{call}");
                    _currentRef = null;
                }
                else
                {
                    sb.AppendLine($"{Indent()}{call}");
                }

                pos += 4 + paramLen;
                continue;
            }

            // Unknown opcode - skip
            sb.AppendLine($"{Indent()}; Unknown opcode 0x{opcode:X4}");
            pos += 2;
        }

        return sb.ToString();
    }

    private static string GetBlockTypeName(int mode)
    {
        return mode switch
        {
            0 => "GameMode",
            1 => "MenuMode",
            2 => "OnActivate",
            3 => "OnAdd",
            4 => "OnDrop",
            5 => "OnEquip",
            6 => "OnUnequip",
            7 => "OnHit",
            8 => "OnHitWith",
            9 => "OnDeath",
            10 => "OnMurder",
            11 => "OnCombatEnd",
            12 => "OnLoad",
            13 => "OnMagicEffectHit",
            14 => "ScriptEffectStart",
            15 => "ScriptEffectFinish",
            16 => "ScriptEffectUpdate",
            17 => "OnTriggerEnter",
            18 => "OnTriggerLeave",
            19 => "OnTrigger",
            20 => "OnReset",
            21 => "OnOpen",
            22 => "OnClose",
            23 => "OnGrab",
            24 => "OnRelease",
            25 => "OnDestructionStageChange",
            _ => $"BlockType{mode}"
        };
    }

    private static (string Name, int BytesConsumed) ParseVariable(byte[] bytes, int offset)
    {
        if (offset >= bytes.Length) return ("?", 1);

        var marker = bytes[offset];
        return marker switch
        {
            0x72 when offset + 5 < bytes.Length => // RefCall - reference.variable
                ParseRefVariable(bytes, offset),
            0x73 when offset + 2 < bytes.Length => // Short/Long local
                ($"iLocal{BinaryUtils.ReadUInt16LE(bytes, offset + 1)}", 3),
            0x66 when offset + 2 < bytes.Length => // Float local
                ($"fLocal{BinaryUtils.ReadUInt16LE(bytes, offset + 1)}", 3),
            0x47 when offset + 2 < bytes.Length => // Global
                ($"Global{BinaryUtils.ReadUInt16LE(bytes, offset + 1)}", 3),
            _ => ($"?var0x{marker:X2}", 1)
        };
    }

    private static (string Name, int BytesConsumed) ParseRefVariable(byte[] bytes, int offset)
    {
        var refIdx = BinaryUtils.ReadUInt16LE(bytes, offset + 1);
        var varMarker = bytes[offset + 3];
        var varIdx = BinaryUtils.ReadUInt16LE(bytes, offset + 4);
        var varType = varMarker == 0x66 ? "f" : "i";
        return ($"SCRO#{refIdx}.{varType}Local{varIdx}", 6);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3776:Cognitive Complexity", Justification = "Expression parsing requires complex switch logic")]
    private string ParseExpression(byte[] bytes, int offset, int length)
    {
        var stack = new Stack<string>();
        var pos = 0;

        while (pos < length)
        {
            if (offset + pos >= bytes.Length) break;

            var marker = bytes[offset + pos];

            switch (marker)
            {
                case 0x20: // Push
                    pos++;
                    if (pos >= length || offset + pos >= bytes.Length) continue;
                    ParsePushValue(bytes, offset, length, ref pos, stack);
                    continue;

                case 0x58: // Standalone function call
                    ParseFunctionCall(bytes, offset, length, ref pos, stack);
                    continue;

                case 0x6E when pos + 5 <= length: // Long param
                    stack.Push(((int)BinaryUtils.ReadUInt32LE(bytes, offset + pos + 1)).ToString(CultureInfo.InvariantCulture));
                    pos += 5;
                    continue;

                case 0x7A when pos + 9 <= length: // Float param (double)
                    stack.Push(BitConverter.ToDouble(bytes, offset + pos + 1).ToString("G", CultureInfo.InvariantCulture));
                    pos += 9;
                    continue;

                case 0x72 when pos + 3 <= length: // Reference
                    stack.Push($"SCRO#{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
                    pos += 3;
                    continue;

                case 0x73 when pos + 3 <= length: // Int local
                    stack.Push($"iLocal{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
                    pos += 3;
                    continue;

                case 0x66 when pos + 3 <= length: // Float local
                    stack.Push($"fLocal{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
                    pos += 3;
                    continue;

                default:
                    if (TryParseOperator(bytes, offset, length, ref pos, stack)) continue;
                    if (marker >= 0x30 && marker <= 0x39) stack.Push(((char)marker).ToString());
                    pos++;
                    continue;
            }
        }

        return stack.Count > 0 ? string.Join(" ", stack.Reverse()) : "0";
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3776:Cognitive Complexity", Justification = "Push value parsing requires complex conditional logic")]
    private void ParsePushValue(byte[] bytes, int offset, int length, ref int pos, Stack<string> stack)
    {
        if (offset + pos >= bytes.Length) return;

        var next = bytes[offset + pos];

        // ASCII digits ('0'-'9')
        if (next >= 0x30 && next <= 0x39)
        {
            stack.Push(((char)next).ToString());
            pos++;
            return;
        }

        // Reference with possible variable access
        if (next == 0x72 && pos + 3 <= length)
        {
            var refIdx = BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1);
            pos += 3;

            if (pos < length && offset + pos < bytes.Length)
            {
                var varMarker = bytes[offset + pos];
                if ((varMarker == 0x73 || varMarker == 0x66) && pos + 3 <= length)
                {
                    var varIdx = BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1);
                    var varPrefix = varMarker == 0x66 ? "f" : "i";
                    stack.Push($"SCRO#{refIdx}.{varPrefix}Local{varIdx}");
                    pos += 3;
                    return;
                }
            }

            stack.Push($"SCRO#{refIdx}");
            return;
        }

        // Int local
        if (next == 0x73 && pos + 3 <= length)
        {
            stack.Push($"iLocal{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
            pos += 3;
            return;
        }

        // Float local
        if (next == 0x66 && pos + 3 <= length)
        {
            stack.Push($"fLocal{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
            pos += 3;
            return;
        }

        // Function call
        if (next == 0x58 && pos + 5 <= length)
        {
            pos++; // skip 0x58
            ParseFunctionCall(bytes, offset, length, ref pos, stack);
            return;
        }

        // Operators after push
        if (TryParseOperator(bytes, offset, length, ref pos, stack)) return;

        // Other literal values
        stack.Push(next.ToString(CultureInfo.InvariantCulture));
        pos++;
    }

    private void ParseFunctionCall(byte[] bytes, int offset, int length, ref int pos, Stack<string> stack)
    {
        if (pos + 6 > length || offset + pos + 6 > bytes.Length)
        {
            pos++;
            return;
        }

        if (bytes[offset + pos] == 0x58) pos++; // skip marker if present

        var funcCode = BinaryUtils.ReadUInt16LE(bytes, offset + pos);
        var funcName = _opcodeTable.TryGetValue(funcCode, out var info) ? info.Name : $"Function_{funcCode:X4}";
        pos += 2;

        var paramLen = BinaryUtils.ReadUInt16LE(bytes, offset + pos);
        pos += 2;

        if (paramLen == 0)
        {
            stack.Push(funcName);
            return;
        }

        if (pos + 2 > length)
        {
            stack.Push(funcName);
            return;
        }

        var paramCount = BinaryUtils.ReadUInt16LE(bytes, offset + pos);
        pos += 2;

        var args = new List<string>();
        var paramEnd = pos + paramLen - 2;

        for (var p = 0; p < paramCount && pos < paramEnd && pos < length; p++)
        {
            var (arg, consumed) = ParseSingleValue(bytes, offset + pos, Math.Min(length - pos, bytes.Length - offset - pos));
            args.Add(arg);
            pos += consumed;
        }

        pos = Math.Min(paramEnd, length);
        stack.Push(args.Count > 0 ? $"{funcName} {string.Join(" ", args)}" : funcName);
    }

    private static bool TryParseOperator(byte[] bytes, int offset, int length, ref int pos, Stack<string> stack)
    {
        if (offset + pos >= bytes.Length) return false;

        var marker = bytes[offset + pos];
        var hasNext = pos + 1 < length && offset + pos + 1 < bytes.Length;
        var next = hasNext ? bytes[offset + pos + 1] : (byte)0;

        string? op = (marker, next) switch
        {
            (0x26, 0x26) => "&&",
            (0x7C, 0x7C) => "||",
            (0x3D, 0x3D) => "==",
            (0x21, 0x3D) => "!=",
            (0x3E, 0x3D) => ">=",
            (0x3C, 0x3D) => "<=",
            _ => null
        };

        if (op != null)
        {
            var right = stack.Count > 0 ? stack.Pop() : "?";
            var left = stack.Count > 0 ? stack.Pop() : "?";
            stack.Push($"{left} {op} {right}");
            pos += 2;
            return true;
        }

        // Single-char operators
        op = marker switch
        {
            0x3E => ">",
            0x3C => "<",
            0x2B => "+",
            0x2D => "-",
            0x2A => "*",
            0x2F => "/",
            _ => null
        };

        if (op != null)
        {
            var right = stack.Count > 0 ? stack.Pop() : "?";
            var left = stack.Count > 0 ? stack.Pop() : "?";
            stack.Push($"{left} {op} {right}");
            pos++;
            return true;
        }

        return false;
    }

    private static (string Value, int BytesConsumed) ParseSingleValue(byte[] bytes, int offset, int maxLen)
    {
        if (maxLen <= 0 || offset >= bytes.Length) return ("?", 1);

        var marker = bytes[offset];
        return marker switch
        {
            0x6E when maxLen >= 5 => (((int)BinaryUtils.ReadUInt32LE(bytes, offset + 1)).ToString(CultureInfo.InvariantCulture), 5),
            0x7A when maxLen >= 9 => (BitConverter.ToDouble(bytes, offset + 1).ToString("G", CultureInfo.InvariantCulture), 9),
            0x72 when maxLen >= 3 => ($"SCRO#{BinaryUtils.ReadUInt16LE(bytes, offset + 1)}", 3),
            0x73 when maxLen >= 3 => ($"iLocal{BinaryUtils.ReadUInt16LE(bytes, offset + 1)}", 3),
            0x66 when maxLen >= 3 => ($"fLocal{BinaryUtils.ReadUInt16LE(bytes, offset + 1)}", 3),
            _ => ($"{marker}", 1)
        };
    }

    private static string ParseParameters(byte[] bytes, int offset, int length, int count)
    {
        var parts = new List<string>();
        var pos = 0;

        for (var i = 0; i < count && pos < length; i++)
        {
            var maxLen = Math.Min(length - pos, bytes.Length - offset - pos);
            var (val, consumed) = ParseSingleValue(bytes, offset + pos, maxLen);
            parts.Add(val);
            pos += consumed;
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    ///     Load commonly used opcodes without requiring external CSV.
    /// </summary>
    private void LoadDefaultOpcodeTable()
    {
        // Core functions commonly found in quest scripts
        var defaults = new (ushort Opcode, string Name)[]
        {
            (0x109E, "MoveTo"),
            (0x1097, "AddScriptPackage"),
            (0x1191, "ApplyImagespaceModifier"),
            (0x10A3, "Enable"),
            (0x10A4, "Disable"),
            (0x109F, "PlaceAtMe"),
            (0x10E4, "SetOpenState"),
            (0x108C, "Activate"),
            (0x11A3, "SetObjectiveDisplayed"),
            (0x11A4, "SetObjectiveCompleted"),
            (0x1100, "ShowMessage"),
            (0x10D5, "PlaySound"),
            (0x10B8, "SetStage"),
            (0x10B9, "GetStage"),
            (0x1145, "SetConversation"),
            (0x10F9, "StartConversation"),
            (0x116F, "EvaluatePackage"),
            (0x116A, "StopCombat"),
            (0x10DC, "SetUnconscious"),
            (0x115E, "AddNote"),
            (0x117A, "ShowMap"),
            (0x10BB, "CompleteQuest"),
            (0x10BA, "StartQuest"),
            (0x119A, "ForceTerminalBack"),
            (0x10ED, "GetLinkedRef"),
            (0x10EE, "SetLinkedRef"),
            (0x115D, "AddPerk"),
            (0x1161, "RemovePerk"),
            (0x10FF, "Say"),
            (0x1098, "RemoveScriptPackage"),
            (0x11B0, "SetPlayerTeammate"),
            (0x10AC, "Kill"),
            (0x10A9, "AddItem"),
            (0x10AA, "RemoveItem"),
            (0x10B4, "EquipItem"),
            (0x10B5, "UnequipItem"),
            (0x1082, "GetActorValue"),
            (0x1083, "SetActorValue"),
            (0x1084, "ModActorValue"),
            (0x1089, "DamageActorValue"),
            (0x108A, "RestoreActorValue"),
            (0x1094, "PlayGroup"),
            (0x1095, "LoopGroup"),
            (0x114E, "SetDestroyed"),
            (0x1059, "Wait"),
            (0x10C5, "Lock"),
            (0x10C6, "Unlock"),
            (0x10B2, "IsActionRef"),
            (0x10C9, "GetLocked"),
            (0x11D9, "SetActorAlpha"),
            (0x1179, "SetWeather"),
            (0x117B, "ForceWeather"),
            (0x1087, "GetAV"),
            (0x1088, "SetAV"),
            (0x1086, "ModAV"),
            (0x1085, "ForceAV"),
        };

        foreach (var (opcode, name) in defaults)
        {
            _opcodeTable[opcode] = (name, "Function");
        }
    }
}
