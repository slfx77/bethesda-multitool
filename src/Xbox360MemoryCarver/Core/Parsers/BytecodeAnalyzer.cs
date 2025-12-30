using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Diagnostic tool for analyzing compiled script bytecode format.
///     Used to understand the structure of Xbox 360 bytecode.
/// </summary>
public static class BytecodeAnalyzer
{
    /// <summary>
    ///     Analyze bytecode and print detailed information about its structure.
    /// </summary>
    public static string AnalyzeBytecode(ReadOnlySpan<byte> data, bool tryBothEndian = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bytecode size: {data.Length} bytes");
        sb.AppendLine();

        // Show raw hex dump of first 64 bytes
        sb.AppendLine("Raw hex dump (first 64 bytes):");
        for (var i = 0; i < Math.Min(64, data.Length); i += 16)
        {
            var end = Math.Min(i + 16, data.Length);
            var hex = string.Join(" ", data[i..end].ToArray().Select(b => b.ToString("X2")));
            var ascii = string.Join("", data[i..end].ToArray().Select(b => b >= 32 && b < 127 ? (char)b : '.'));
            sb.AppendLine($"  {i:X4}: {hex,-48} {ascii}");
        }
        sb.AppendLine();

        if (tryBothEndian)
        {
            sb.AppendLine("=== Little-Endian interpretation ===");
            AnalyzeAsOpcodes(data, sb, isBigEndian: false);
            sb.AppendLine();

            sb.AppendLine("=== Big-Endian interpretation ===");
            AnalyzeAsOpcodes(data, sb, isBigEndian: true);
        }
        else
        {
            AnalyzeAsOpcodes(data, sb, isBigEndian: false);
        }

        return sb.ToString();
    }

    private static void AnalyzeAsOpcodes(ReadOnlySpan<byte> data, StringBuilder sb, bool isBigEndian)
    {
        var pos = 0;
        var statementNum = 0;

        while (pos + 4 <= data.Length && statementNum < 20)
        {
            var opcode = isBigEndian
                ? BinaryUtils.ReadUInt16BE(data, pos)
                : BinaryUtils.ReadUInt16LE(data, pos);
            var length = isBigEndian
                ? BinaryUtils.ReadUInt16BE(data, pos + 2)
                : BinaryUtils.ReadUInt16LE(data, pos + 2);

            var opcodeName = GetOpcodeName(opcode);
            var isValid = IsValidOpcode(opcode);

            sb.Append($"  [{statementNum:D2}] @{pos:X4}: opcode=0x{opcode:X4} ({opcodeName,-20}) len={length,4}");
            
            if (!isValid)
            {
                sb.Append(" [INVALID]");
            }
            
            // Show first few bytes of data
            if (length > 0 && pos + 4 + length <= data.Length)
            {
                var dataBytes = data.Slice(pos + 4, Math.Min(8, (int)length));
                var dataHex = string.Join(" ", dataBytes.ToArray().Select(b => b.ToString("X2")));
                sb.Append($" data: {dataHex}");
                if (length > 8) sb.Append("...");
            }

            sb.AppendLine();

            // Check for obviously wrong lengths
            if (length > 1000)
            {
                sb.AppendLine("    [Length too large - stopping]");
                break;
            }

            pos += 4 + length;
            statementNum++;
        }

        sb.AppendLine($"  Parsed {statementNum} statements, ended at offset 0x{pos:X4}");
    }

    private static string GetOpcodeName(ushort opcode)
    {
        return opcode switch
        {
            0x10 => "Begin",
            0x11 => "End",
            0x12 => "Short",
            0x13 => "Long",
            0x14 => "Float",
            0x15 => "SetTo",
            0x16 => "If",
            0x17 => "Else",
            0x18 => "ElseIf",
            0x19 => "EndIf",
            0x1A => "While",
            0x1B => "Loop",
            0x1C => "ReferenceFunction",
            0x1D => "ScriptName",
            0x1E => "Return",
            0x1F => "Ref",
            >= 0x1000 and < 0x1400 => $"VanillaCmd",
            >= 0x1400 => $"NVSECmd",
            _ => $"Unknown"
        };
    }

    private static bool IsValidOpcode(ushort opcode)
    {
        // Valid statement opcodes: 0x10-0x1F
        if (opcode >= 0x10 && opcode <= 0x1F) return true;
        // Valid command opcodes: 0x1000+
        if (opcode >= 0x1000) return true;
        return false;
    }

    /// <summary>
    ///     Analyze bytecode files from a directory and print results.
    /// </summary>
    public static async Task AnalyzeBytecodeFilesAsync(string directory, int maxFiles = 5)
    {
        if (!Directory.Exists(directory))
        {
            Console.WriteLine($"Directory not found: {directory}");
            return;
        }

        var files = Directory.GetFiles(directory, "*.bin").Take(maxFiles).ToList();
        Console.WriteLine($"Analyzing {files.Count} bytecode files from {directory}");
        Console.WriteLine(new string('=', 70));

        foreach (var file in files)
        {
            var data = await File.ReadAllBytesAsync(file);
            Console.WriteLine();
            Console.WriteLine($"=== {Path.GetFileName(file)} ===");
            Console.WriteLine(AnalyzeBytecode(data));

            // Also try to decompile with big-endian
            Console.WriteLine();
            Console.WriteLine("Decompiled output (Big-Endian):");
            Console.WriteLine(new string('-', 40));
            var decompiler = new ScriptDecompiler(data, 0, data.Length, isBigEndian: true);
            var result = decompiler.Decompile();
            Console.WriteLine(result.DecompiledText);
            if (!result.Success)
            {
                Console.WriteLine($"(Partial decompilation: {result.ErrorMessage})");
            }
        }
    }
}
