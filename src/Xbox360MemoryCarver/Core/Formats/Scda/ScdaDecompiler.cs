using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Scda;

/// <summary>
///     Decompiles compiled Fallout: New Vegas script bytecode back into readable script format.
///     Based on Oblivion SCPT format documentation and opcode analysis from PDB symbols.
/// </summary>
public sealed partial class ScdaDecompiler
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
                _opcodeTable[opcode] = (parts[2], parts[3]);
        }
    }

    /// <summary>
    ///     Decompile bytecode to readable script format.
    /// </summary>
    [SuppressMessage("Sonar", "S3776:Cognitive Complexity",
        Justification = "Bytecode decompiler requires complex switch logic")]
    [SuppressMessage("Globalization", "CA1305:Specify IFormatProvider",
        Justification = "Script code output is not culture-sensitive")]
    public string Decompile(byte[] bytecode)
    {
        var sb = new StringBuilder();
        var indent = 0;
        var pos = 0;

        string Indent()
        {
            return new string('\t', indent);
        }

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
}
