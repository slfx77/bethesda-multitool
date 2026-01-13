// Expression parsing helpers for ScdaDecompiler

using System.Globalization;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Scda;

// Expression parsing methods
public sealed partial class ScdaDecompiler
{
    private string ParseExpression(byte[] bytes, int offset, int length)
    {
        var stack = new Stack<string>();
        var pos = 0;

        while (pos < length)
        {
            if (offset + pos >= bytes.Length) break;
            ProcessExpressionToken(bytes, offset, length, ref pos, stack);
        }

        return stack.Count > 0 ? string.Join(" ", stack.Reverse()) : "0";
    }

    private void ProcessExpressionToken(byte[] bytes, int offset, int length, ref int pos, Stack<string> stack)
    {
        var marker = bytes[offset + pos];

        switch (marker)
        {
            case 0x20: // Push
                pos++;
                if (pos >= length || offset + pos >= bytes.Length) return;
                ParsePushValue(bytes, offset, length, ref pos, stack);
                return;

            case 0x58: // Standalone function call
                ParseFunctionCall(bytes, offset, length, ref pos, stack);
                return;

            case 0x6E when pos + 5 <= length: // Long param
                stack.Push(((int)BinaryUtils.ReadUInt32LE(bytes, offset + pos + 1)).ToString(CultureInfo
                    .InvariantCulture));
                pos += 5;
                return;

            case 0x7A when pos + 9 <= length: // Float param (double)
                stack.Push(BitConverter.ToDouble(bytes, offset + pos + 1)
                    .ToString("G", CultureInfo.InvariantCulture));
                pos += 9;
                return;

            case 0x72 when pos + 3 <= length: // Reference
                stack.Push($"SCRO#{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
                pos += 3;
                return;

            case 0x73 when pos + 3 <= length: // Int local
                stack.Push($"iLocal{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
                pos += 3;
                return;

            case 0x66 when pos + 3 <= length: // Float local
                stack.Push($"fLocal{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
                pos += 3;
                return;

            default:
                if (TryParseOperator(bytes, offset, length, ref pos, stack)) return;
                if (marker >= 0x30 && marker <= 0x39) stack.Push(((char)marker).ToString());
                pos++;
                return;
        }
    }

    private void ParsePushValue(byte[] bytes, int offset, int length, ref int pos, Stack<string> stack)
    {
        if (offset + pos >= bytes.Length) return;

        var next = bytes[offset + pos];

        if (TryParseAsciiDigit(next, stack, ref pos)) return;
        if (TryParseReferenceWithVariable(bytes, offset, length, ref pos, stack, next)) return;
        if (TryParseLocalVariable(bytes, offset, length, ref pos, stack, next)) return;
        if (TryParseFunctionCallMarker(bytes, offset, length, ref pos, stack, next)) return;
        if (TryParseOperator(bytes, offset, length, ref pos, stack)) return;

        // Other literal values
        stack.Push(next.ToString(CultureInfo.InvariantCulture));
        pos++;
    }

    private static bool TryParseAsciiDigit(byte next, Stack<string> stack, ref int pos)
    {
        if (next < 0x30 || next > 0x39) return false;
        stack.Push(((char)next).ToString());
        pos++;
        return true;
    }

    private static bool TryParseReferenceWithVariable(
        byte[] bytes, int offset, int length, ref int pos, Stack<string> stack, byte next)
    {
        if (next != 0x72 || pos + 3 > length) return false;

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
                return true;
            }
        }

        stack.Push($"SCRO#{refIdx}");
        return true;
    }

    private static bool TryParseLocalVariable(
        byte[] bytes, int offset, int length, ref int pos, Stack<string> stack, byte next)
    {
        if (next == 0x73 && pos + 3 <= length)
        {
            stack.Push($"iLocal{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
            pos += 3;
            return true;
        }

        if (next == 0x66 && pos + 3 <= length)
        {
            stack.Push($"fLocal{BinaryUtils.ReadUInt16LE(bytes, offset + pos + 1)}");
            pos += 3;
            return true;
        }

        return false;
    }

    private bool TryParseFunctionCallMarker(
        byte[] bytes, int offset, int length, ref int pos, Stack<string> stack, byte next)
    {
        if (next != 0x58 || pos + 5 > length) return false;
        pos++; // skip 0x58
        ParseFunctionCall(bytes, offset, length, ref pos, stack);
        return true;
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
            var (arg, consumed) =
                ParseSingleValue(bytes, offset + pos, Math.Min(length - pos, bytes.Length - offset - pos));
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

        var op = (marker, next) switch
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
            0x6E when maxLen >= 5 => (
                ((int)BinaryUtils.ReadUInt32LE(bytes, offset + 1)).ToString(CultureInfo.InvariantCulture), 5),
            0x7A when maxLen >= 9 => (
                BitConverter.ToDouble(bytes, offset + 1).ToString("G", CultureInfo.InvariantCulture), 9),
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
            (0x1085, "ForceAV")
        };

        foreach (var (opcode, name) in defaults) _opcodeTable[opcode] = (name, "Function");
    }
}
