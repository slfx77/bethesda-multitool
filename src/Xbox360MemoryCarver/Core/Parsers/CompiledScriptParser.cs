using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for compiled Bethesda ObScript bytecode (Xbox 360 / Big-Endian).
///     
///     IMPORTANT: Xbox 360 uses PowerPC (big-endian), so all multi-byte values
///     in the bytecode are stored in big-endian format.
///     
///     Compiled bytecode format (each statement):
///     - opcode (2 bytes, BIG-ENDIAN)
///     - length (2 bytes, BIG-ENDIAN)  
///     - variable-length data
///     
///     Key opcodes:
///     - 0x0010: ScriptName
///     - 0x0011: Begin (event block)
///     - 0x0012: End
///     - 0x0013-0x0015: Variable declarations (short, long, float)
///     - 0x0016: Set ... To
///     - 0x0017: If
///     - 0x0018: Else
///     - 0x0019: ElseIf  
///     - 0x001A: EndIf
///     - 0x001C: ref declaration
///     - 0x001D: ReferenceFunction (calling ref.Function())
///     - 0x001E: Return
/// </summary>
public class CompiledScriptParser : IFileParser
{
    // Script statement opcodes (from xNVSE/Oblivion)
    private const ushort Opcode_ScriptName = 0x0010;
    private const ushort Opcode_Begin = 0x0011;
    private const ushort Opcode_End = 0x0012;
    private const ushort Opcode_Short = 0x0013;
    private const ushort Opcode_Long = 0x0014;
    private const ushort Opcode_Float = 0x0015;
    private const ushort Opcode_SetTo = 0x0016;
    private const ushort Opcode_If = 0x0017;
    private const ushort Opcode_Else = 0x0018;
    private const ushort Opcode_ElseIf = 0x0019;
    private const ushort Opcode_EndIf = 0x001A;
    private const ushort Opcode_Ref = 0x001C;
    private const ushort Opcode_ReferenceFunction = 0x001D;
    private const ushort Opcode_Return = 0x001E;

    // Valid statement opcodes range (0x10-0x1F)
    private static readonly HashSet<ushort> ValidStatementOpcodes =
    [
        Opcode_ScriptName, Opcode_Begin, Opcode_End,
        Opcode_Short, Opcode_Long, Opcode_Float,
        Opcode_SetTo, Opcode_If, Opcode_Else, Opcode_ElseIf, Opcode_EndIf,
        Opcode_Ref, Opcode_ReferenceFunction, Opcode_Return,
        0x001B, 0x001F // While, Loop (NVSE extensions)
    ];

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        // Minimum size for compiled bytecode header
        if (data.Length < offset + 8) return null;

        try
        {
            // Read opcode as BIG-ENDIAN (Xbox 360 / PowerPC)
            var opcode = BinaryUtils.ReadUInt16BE(data, offset);

            // Must start with ScriptName opcode (0x0010)
            if (opcode != Opcode_ScriptName) return null;

            // Read length as BIG-ENDIAN
            var length = BinaryUtils.ReadUInt16BE(data, offset + 2);

            // ScriptName length is typically 0-4 bytes (just padding/index data)
            if (length > 64) return null;

            // Validate the bytecode structure
            if (!ValidateCompiledBytecode(data, offset, out var totalSize, out var stats))
                return null;

            return new ParseResult
            {
                Format = "CompiledScript",
                EstimatedSize = totalSize,
                IsXbox360 = true,
                Metadata = new Dictionary<string, object>
                {
                    ["isCompiled"] = true,
                    ["isBigEndian"] = true,
                    ["statementCount"] = stats.StatementCount,
                    ["beginCount"] = stats.BeginCount,
                    ["endCount"] = stats.EndCount,
                    ["variableDeclarations"] = stats.VarDeclarations
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CompiledScriptParser] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private struct BytecodeStats
    {
        public int StatementCount;
        public int BeginCount;
        public int EndCount;
        public int VarDeclarations;
        public int CommandCalls;
    }

    /// <summary>
    ///     Validate compiled bytecode structure and gather statistics.
    ///     All multi-byte reads are BIG-ENDIAN for Xbox 360.
    /// </summary>
    private static bool ValidateCompiledBytecode(ReadOnlySpan<byte> data, int offset, out int totalSize, out BytecodeStats stats)
    {
        totalSize = 0;
        stats = new BytecodeStats();

        var pos = offset;
        var maxScan = Math.Min(data.Length, offset + 64 * 1024); // Max 64KB for a script
        var maxStatements = 2000;

        while (pos + 4 <= maxScan && stats.StatementCount < maxStatements)
        {
            // Read opcode and length as BIG-ENDIAN
            var opcode = BinaryUtils.ReadUInt16BE(data, pos);
            ushort length;
            var statementStart = pos;

            // Handle ReferenceFunction (0x001D) - has different structure
            if (opcode == Opcode_ReferenceFunction)
            {
                if (pos + 8 > maxScan) break;

                // Structure: 0x001D (2) + refIdx (2) + actualOpcode (2) + length (2) + data
                var refIdx = BinaryUtils.ReadUInt16BE(data, pos + 2);
                opcode = BinaryUtils.ReadUInt16BE(data, pos + 4);
                length = BinaryUtils.ReadUInt16BE(data, pos + 6);

                // Validate refIdx is reasonable (< 256 refs typically)
                if (refIdx > 512) break;

                pos += 8 + length;
                stats.CommandCalls++;
            }
            else
            {
                length = BinaryUtils.ReadUInt16BE(data, pos + 2);

                // Validate length is reasonable
                if (length > 4096) break;

                pos += 4 + length;

                // Track statement types
                switch (opcode)
                {
                    case Opcode_Begin:
                        stats.BeginCount++;
                        break;
                    case Opcode_End:
                        stats.EndCount++;
                        break;
                    case Opcode_Short:
                    case Opcode_Long:
                    case Opcode_Float:
                    case Opcode_Ref:
                        stats.VarDeclarations++;
                        break;
                    default:
                        // Check if it's a valid statement opcode or a command (0x1000+)
                        if (!ValidStatementOpcodes.Contains(opcode) && opcode < 0x1000)
                        {
                            // Unknown low opcode - likely end of bytecode or corruption
                            if (stats.BeginCount > 0 && stats.EndCount >= stats.BeginCount)
                            {
                                // We've seen complete blocks, this is probably end of script
                                totalSize = statementStart - offset;
                                return IsValidScript(stats);
                            }

                            // Continue if we haven't seen Begin yet (might be variable declarations)
                            if (stats.BeginCount == 0 && stats.StatementCount < 50)
                                break;
                        }
                        else if (opcode >= 0x1000)
                        {
                            stats.CommandCalls++;
                        }
                        break;
                }
            }

            stats.StatementCount++;

            // Check for end of script after End opcode
            if (stats.EndCount > 0 && stats.EndCount >= stats.BeginCount)
            {
                // Look ahead - if next opcode is 0 or ScriptName, we're done
                if (pos + 2 <= maxScan)
                {
                    var nextOpcode = BinaryUtils.ReadUInt16BE(data, pos);
                    // If next opcode is another ScriptName, we've hit a new script
                    if (nextOpcode == Opcode_ScriptName) break;
                    // If next opcode is 0 or looks like garbage, we're done
                    if (nextOpcode == 0 || (nextOpcode > 0x0020 && nextOpcode < 0x1000)) break;
                }
            }
        }

        totalSize = pos - offset;
        return IsValidScript(stats);
    }

    /// <summary>
    ///     Check if the gathered stats represent a valid script.
    /// </summary>
    private static bool IsValidScript(BytecodeStats stats)
    {
        // Must have at least one Begin/End pair
        if (stats.BeginCount == 0 || stats.EndCount == 0) return false;

        // Begin and End counts should roughly match
        if (Math.Abs(stats.BeginCount - stats.EndCount) > 1) return false;

        // Should have a reasonable number of statements
        if (stats.StatementCount < 2) return false;

        // Should have at least some content (commands or variable declarations)
        if (stats.CommandCalls == 0 && stats.VarDeclarations == 0 && stats.StatementCount < 5) return false;

        return true;
    }
}
