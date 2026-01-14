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
    public string Decompile(byte[] bytecode)
    {
        var context = new DecompileContext();

        while (context.Pos < bytecode.Length - 1)
        {
            var opcode = BinaryUtils.ReadUInt16LE(bytecode, context.Pos);
            context.Pos = DecompileOpcode(bytecode, opcode, context);
        }

        return context.Output.ToString();
    }

    private int DecompileOpcode(byte[] bytecode, ushort opcode, DecompileContext ctx)
    {
        return opcode switch
        {
            0x0010 => DecompileBegin(bytecode, ctx),
            0x0011 => DecompileEnd(ctx),
            0x0015 => DecompileSet(bytecode, ctx),
            0x0016 => DecompileIf(bytecode, ctx),
            0x0017 => DecompileElse(bytecode, ctx),
            0x0018 => DecompileElseIf(bytecode, ctx),
            0x0019 => DecompileEndIf(ctx),
            0x001C => DecompileSetRef(bytecode, ctx),
            0x001D => DecompileSimple(ctx, "ScriptName"),
            0x001E => DecompileSimple(ctx, "Return"),
            >= 0x100 => DecompileFunctionCall(bytecode, opcode, ctx),
            _ => DecompileUnknown(opcode, ctx)
        };
    }

    private static int DecompileBegin(byte[] bytecode, DecompileContext ctx)
    {
        var modeLen = BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 2);
        var mode = modeLen > 0 ? BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 4) : 0;
        ctx.Output.Append(ctx.GetIndent()).Append("Begin ").AppendLine(GetBlockTypeName(mode));
        ctx.IncreaseIndent();
        return ctx.Pos + 4 + modeLen;
    }

    private static int DecompileEnd(DecompileContext ctx)
    {
        ctx.DecreaseIndent();
        ctx.Output.Append(ctx.GetIndent()).AppendLine("End");
        return ctx.Pos + 4;
    }

    private int DecompileSet(byte[] bytecode, DecompileContext ctx)
    {
        var setLen = BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 2);
        var (varName, varBytes) = ParseVariable(bytecode, ctx.Pos + 4);
        var exprLenOffset = ctx.Pos + 4 + varBytes;
        var exprLen = BinaryUtils.ReadUInt16LE(bytecode, exprLenOffset);
        var expr = ParseExpression(bytecode, exprLenOffset + 2, exprLen);
        ctx.Output.Append(ctx.GetIndent()).Append("set ").Append(varName).Append(" to ").AppendLine(expr);
        return ctx.Pos + 4 + setLen;
    }

    private int DecompileIf(byte[] bytecode, DecompileContext ctx)
    {
        var compLen = BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 2);
        var exprLen = BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 6);
        var expr = ParseExpression(bytecode, ctx.Pos + 8, exprLen);
        ctx.Output.Append(ctx.GetIndent()).Append("if (").Append(expr).AppendLine(")");
        ctx.IncreaseIndent();
        return ctx.Pos + 4 + compLen;
    }

    private static int DecompileElse(byte[] bytecode, DecompileContext ctx)
    {
        var elseLen = BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 2);
        ctx.DecreaseIndent();
        ctx.Output.Append(ctx.GetIndent()).AppendLine("else");
        ctx.IncreaseIndent();
        return ctx.Pos + 4 + elseLen;
    }

    private int DecompileElseIf(byte[] bytecode, DecompileContext ctx)
    {
        var elifLen = BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 2);
        var exprLen = BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 6);
        var expr = ParseExpression(bytecode, ctx.Pos + 8, exprLen);
        ctx.DecreaseIndent();
        ctx.Output.Append(ctx.GetIndent()).Append("elseif (").Append(expr).AppendLine(")");
        ctx.IncreaseIndent();
        return ctx.Pos + 4 + elifLen;
    }

    private static int DecompileEndIf(DecompileContext ctx)
    {
        ctx.DecreaseIndent();
        ctx.Output.Append(ctx.GetIndent()).AppendLine("endif");
        return ctx.Pos + 4;
    }

    private int DecompileSetRef(byte[] bytecode, DecompileContext ctx)
    {
        var refIdx = BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 2);
        _currentRef = $"SCRO#{refIdx}";
        return ctx.Pos + 4;
    }

    private static int DecompileSimple(DecompileContext ctx, string keyword)
    {
        ctx.Output.Append(ctx.GetIndent()).AppendLine(keyword);
        return ctx.Pos + 4;
    }

    private int DecompileFunctionCall(byte[] bytecode, ushort opcode, DecompileContext ctx)
    {
        var name = _opcodeTable.TryGetValue(opcode, out var info) ? info.Name : string.Create(CultureInfo.InvariantCulture, $"Function_{opcode:X4}");
        var paramLen = BinaryUtils.ReadUInt16LE(bytecode, ctx.Pos + 2);

        var call = paramLen == 0
            ? name
            : FormatFunctionWithParams(bytecode, ctx.Pos, paramLen, name);

        if (_currentRef != null)
        {
            ctx.Output.Append(ctx.GetIndent()).Append(_currentRef).Append('.').AppendLine(call);
            _currentRef = null;
        }
        else
        {
            ctx.Output.Append(ctx.GetIndent()).AppendLine(call);
        }

        return ctx.Pos + 4 + paramLen;
    }

    private static string FormatFunctionWithParams(byte[] bytecode, int pos, int paramLen, string name)
    {
        var paramCount = BinaryUtils.ReadUInt16LE(bytecode, pos + 4);
        var paramStr = ParseParameters(bytecode, pos + 6, paramLen - 2, paramCount);
        return paramStr.Length > 0 ? $"{name} {paramStr}" : name;
    }

    private static int DecompileUnknown(ushort opcode, DecompileContext ctx)
    {
        ctx.Output.Append(ctx.GetIndent()).Append("; Unknown opcode 0x").AppendLine(opcode.ToString("X4", CultureInfo.InvariantCulture));
        return ctx.Pos + 2;
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

    private sealed class DecompileContext
    {
        public StringBuilder Output { get; } = new();
        public int Indent { get; set; }
        public int Pos { get; set; }

        public string GetIndent()
        {
            return new string('\t', Indent);
        }

        public void IncreaseIndent()
        {
            Indent++;
        }

        public void DecreaseIndent()
        {
            Indent = Math.Max(0, Indent - 1);
        }
    }
}
