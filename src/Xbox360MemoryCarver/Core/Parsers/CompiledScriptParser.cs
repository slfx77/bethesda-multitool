using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for compiled Bethesda ObScript bytecode.
///     
///     Based on xNVSE's ScriptAnalyzer.h - the bytecode format is consistent
///     across platforms (PC/Xbox 360).
///     
///     Compiled bytecode format (each statement):
///     - opcode (2 bytes)
///     - length (2 bytes)  
///     - variable-length data
///     
///     Statement opcodes (from xNVSE ScriptStatementCode):
///     - 0x10: Begin (event block)
///     - 0x11: End
///     - 0x12: Short (variable declaration)
///     - 0x13: Long (variable declaration)
///     - 0x14: Float (variable declaration)
///     - 0x15: SetTo
///     - 0x16: If
///     - 0x17: Else
///     - 0x18: ElseIf  
///     - 0x19: EndIf
///     - 0x1C: ReferenceFunction (calling ref.Function())
///     - 0x1D: ScriptName
///     - 0x1E: Return
///     - 0x1F: Ref (reference variable declaration)
///     
///     Commands are opcodes 0x1000 and higher.
/// </summary>
public class CompiledScriptParser : IFileParser
{
    // Statement opcodes from xNVSE ScriptAnalyzer.h
    private const ushort Opcode_Begin = 0x10;
    private const ushort Opcode_End = 0x11;
    private const ushort Opcode_Short = 0x12;
    private const ushort Opcode_Long = 0x13;
    private const ushort Opcode_Float = 0x14;
    private const ushort Opcode_SetTo = 0x15;
    private const ushort Opcode_If = 0x16;
    private const ushort Opcode_Else = 0x17;
    private const ushort Opcode_ElseIf = 0x18;
    private const ushort Opcode_EndIf = 0x19;
    private const ushort Opcode_ReferenceFunction = 0x1C;
    private const ushort Opcode_ScriptName = 0x1D;
    private const ushort Opcode_Return = 0x1E;
    private const ushort Opcode_Ref = 0x1F;

    // Valid statement opcodes range (0x10-0x1F)
    private static readonly HashSet<ushort> ValidStatementOpcodes =
    [
        Opcode_Begin, Opcode_End,
        Opcode_Short, Opcode_Long, Opcode_Float,
        Opcode_SetTo, Opcode_If, Opcode_Else, Opcode_ElseIf, Opcode_EndIf,
        Opcode_ReferenceFunction, Opcode_ScriptName, Opcode_Return, Opcode_Ref,
        0x1A, 0x1B // While/Loop (NVSE extensions, may not be in vanilla)
    ];

    /// <summary>
    ///     If true, read bytecode as big-endian (Xbox 360).
    ///     Note: The bytecode format itself is typically little-endian even on Xbox 360.
    /// </summary>
    public bool IsBigEndian { get; set; }

    private ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        return IsBigEndian 
            ? BinaryUtils.ReadUInt16BE(data, offset) 
            : BinaryUtils.ReadUInt16LE(data, offset);
    }

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        // Minimum size for compiled bytecode header
        if (data.Length < offset + 8) return null;

        try
        {
            // Read opcode
            var opcode = ReadUInt16(data, offset);

            // Must start with ScriptName opcode (0x1D)
            if (opcode != Opcode_ScriptName) return null;

            // Read length
            var length = ReadUInt16(data, offset + 2);

            // ScriptName length is typically 0-4 bytes (just padding/index data)
            if (length > 64) return null;

            // Validate the bytecode structure
            if (!ValidateCompiledBytecode(data, offset, out var totalSize, out var stats))
                return null;

            return new ParseResult
            {
                Format = "CompiledScript",
                EstimatedSize = totalSize,
                IsXbox360 = IsBigEndian,
                Metadata = new Dictionary<string, object>
                {
                    ["isCompiled"] = true,
                    ["isBigEndian"] = IsBigEndian,
                    ["statementCount"] = stats.StatementCount,
                    ["beginCount"] = stats.BeginCount,
                    ["endCount"] = stats.EndCount,
                    ["variableDeclarations"] = stats.VarDeclarations,
                    ["commandCalls"] = stats.CommandCalls
                }
            };
        }
        catch
        {
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
    /// </summary>
    private bool ValidateCompiledBytecode(ReadOnlySpan<byte> data, int offset, out int totalSize, out BytecodeStats stats)
    {
        totalSize = 0;
        stats = new BytecodeStats();

        var pos = offset;
        var maxScan = Math.Min(data.Length, offset + 64 * 1024); // Max 64KB for a script
        var maxStatements = 2000;

        while (pos + 4 <= maxScan && stats.StatementCount < maxStatements)
        {
            // Read opcode and length
            var opcode = ReadUInt16(data, pos);
            ushort length;
            var statementStart = pos;

            // Handle ReferenceFunction (0x1C) - has different structure
            if (opcode == Opcode_ReferenceFunction)
            {
                if (pos + 8 > maxScan) break;

                // Structure: 0x1C (2) + refIdx (2) + actualOpcode (2) + length (2) + data
                var refIdx = ReadUInt16(data, pos + 2);
                var actualOpcode = ReadUInt16(data, pos + 4);
                length = ReadUInt16(data, pos + 6);

                // Validate refIdx is reasonable (< 256 refs typically)
                if (refIdx > 512) break;
                
                // The actual opcode should be a command (0x1000+)
                if (actualOpcode < 0x1000) break;

                pos += 8 + length;
                stats.CommandCalls++;
            }
            else
            {
                length = ReadUInt16(data, pos + 2);

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
                    case Opcode_ScriptName:
                        // OK - expected at start
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
                // Look ahead - if next opcode looks invalid, we're done
                if (pos + 2 <= maxScan)
                {
                    var nextOpcode = ReadUInt16(data, pos);
                    // If next opcode is 0 or looks like garbage, we're done
                    if (nextOpcode == 0 || (nextOpcode > Opcode_Ref && nextOpcode < 0x1000))
                    {
                        break;
                    }
                }
                else
                {
                    break;
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
        if (stats.StatementCount < 3) return false;

        return true;
    }
}
