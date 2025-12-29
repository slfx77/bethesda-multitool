using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Scanner for compiled Bethesda script bytecode in memory dumps.
///     
///     Based on xNVSE's ScriptAnalyzer - uses correct opcodes:
///     - 0x1D = ScriptName (scripts start with this)
///     - 0x10 = Begin
///     - 0x11 = End
///     
///     This scanner can work with both little-endian (PC) and big-endian (Xbox 360) bytecode.
/// </summary>
public static class CompiledScriptScanner
{
    // From xNVSE ScriptStatementCode enum
    private const ushort Opcode_ScriptName = 0x1D;
    private const ushort Opcode_Begin = 0x10;

    /// <summary>
    ///     Scan a memory region for potential compiled scripts.
    ///     
    ///     NOTE: After extensive testing, it appears that Xbox 360 bytecode uses LITTLE-ENDIAN
    ///     for opcodes despite the PowerPC being big-endian. This is because the game engine
    ///     stores bytecode in a platform-independent format.
    /// </summary>
    /// <param name="data">The data to scan.</param>
    /// <param name="startOffset">Starting offset in the data.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="isBigEndian">If true, read as big-endian. Default is FALSE (bytecode is LE even on Xbox 360).</param>
    /// <returns>List of potential compiled script locations and sizes.</returns>
    public static List<CompiledScriptMatch> ScanForCompiledScripts(
        ReadOnlySpan<byte> data,
        int startOffset = 0,
        int maxResults = 1000,
        bool isBigEndian = false)
    {
        var results = new List<CompiledScriptMatch>();
        var parser = new CompiledScriptParser { IsBigEndian = isBigEndian };

        // Scan for potential script starts
        // ScriptName opcode 0x1D appears as:
        // - Little-endian: 0x1D 0x00 (bytes)
        // - Big-endian: 0x00 0x1D (bytes)
        var firstByte = isBigEndian ? (byte)0x00 : (byte)0x1D;
        var secondByte = isBigEndian ? (byte)0x1D : (byte)0x00;

        for (var i = startOffset; i < data.Length - 8 && results.Count < maxResults; i++)
        {
            // Quick check for ScriptName opcode
            if (data[i] != firstByte || data[i + 1] != secondByte) continue;

            // Get the length field
            var length = isBigEndian 
                ? BinaryUtils.ReadUInt16BE(data, i + 2) 
                : BinaryUtils.ReadUInt16LE(data, i + 2);

            // ScriptName typically has length 0-4 bytes (just padding/index data)
            if (length > 32) continue;

            // Try to parse as compiled script
            var result = parser.ParseHeader(data, i);
            if (result != null && result.EstimatedSize > 20)
            {
                var match = new CompiledScriptMatch
                {
                    Offset = i,
                    Size = result.EstimatedSize,
                    StatementCount = result.Metadata.TryGetValue("statementCount", out var sc) ? (int)sc : 0,
                    BeginCount = result.Metadata.TryGetValue("beginCount", out var bc) ? (int)bc : 0,
                    EndCount = result.Metadata.TryGetValue("endCount", out var ec) ? (int)ec : 0,
                    CommandCalls = result.Metadata.TryGetValue("commandCalls", out var cc) ? (int)cc : 0,
                    Confidence = CalculateConfidence(result)
                };

                // Skip past this script to avoid overlapping matches
                i += result.EstimatedSize - 1;

                results.Add(match);
            }
        }

        return results;
    }

    /// <summary>
    ///     Scan for scripts trying both endianness modes and return the best results.
    /// </summary>
    public static List<CompiledScriptMatch> ScanForCompiledScriptsAuto(
        ReadOnlySpan<byte> data,
        int startOffset = 0,
        int maxResults = 1000)
    {
        // Try little-endian first (most common even on Xbox 360)
        var leResults = ScanForCompiledScripts(data, startOffset, maxResults, isBigEndian: false);
        
        // Try big-endian
        var beResults = ScanForCompiledScripts(data, startOffset, maxResults, isBigEndian: true);
        
        // Return whichever found more high-confidence results
        var leHighConf = leResults.Count(m => m.Confidence >= 0.7f);
        var beHighConf = beResults.Count(m => m.Confidence >= 0.7f);
        
        return leHighConf >= beHighConf ? leResults : beResults;
    }

    /// <summary>
    ///     Calculate a confidence score for a potential compiled script match.
    /// </summary>
    private static float CalculateConfidence(ParseResult result)
    {
        var confidence = 0.5f;

        // More statements = higher confidence
        if (result.Metadata.TryGetValue("statementCount", out var scObj) && scObj is int sc)
        {
            if (sc >= 10) confidence += 0.1f;
            if (sc >= 25) confidence += 0.1f;
            if (sc >= 50) confidence += 0.1f;
        }

        // Balanced Begin/End = higher confidence
        if (result.Metadata.TryGetValue("beginCount", out var bcObj) && bcObj is int bc &&
            result.Metadata.TryGetValue("endCount", out var ecObj) && ecObj is int ec)
        {
            if (bc == ec && bc > 0) confidence += 0.15f;
            if (bc > 1 && ec > 1) confidence += 0.05f;
        }

        // Variable declarations = higher confidence
        if (result.Metadata.TryGetValue("variableDeclarations", out var vdObj) && vdObj is int vd)
        {
            if (vd > 0) confidence += 0.1f;
        }

        // Command calls = higher confidence
        if (result.Metadata.TryGetValue("commandCalls", out var ccObj) && ccObj is int cc)
        {
            if (cc > 0) confidence += 0.05f;
            if (cc > 5) confidence += 0.05f;
        }

        return Math.Min(confidence, 1.0f);
    }

    /// <summary>
    ///     Analyze a compiled script and extract detailed opcode information.
    /// </summary>
    public static CompiledScriptInfo? AnalyzeCompiledScript(ReadOnlySpan<byte> data, int offset, bool isBigEndian = false)
    {
        var parser = new CompiledScriptParser { IsBigEndian = isBigEndian };
        var result = parser.ParseHeader(data, offset);

        if (result == null) return null;

        var info = new CompiledScriptInfo
        {
            Offset = offset,
            Size = result.EstimatedSize,
            Opcodes = []
        };

        // Parse through the bytecode to extract opcode sequence
        var pos = offset;
        var end = offset + result.EstimatedSize;

        while (pos + 4 <= end && pos < data.Length)
        {
            var opcode = isBigEndian 
                ? BinaryUtils.ReadUInt16BE(data, pos) 
                : BinaryUtils.ReadUInt16LE(data, pos);
            
            ushort length;
            ushort refIdx = 0;

            // Handle ReferenceFunction (0x1C) specially
            if (opcode == 0x1C && pos + 8 <= end)
            {
                refIdx = isBigEndian 
                    ? BinaryUtils.ReadUInt16BE(data, pos + 2) 
                    : BinaryUtils.ReadUInt16LE(data, pos + 2);
                opcode = isBigEndian 
                    ? BinaryUtils.ReadUInt16BE(data, pos + 4) 
                    : BinaryUtils.ReadUInt16LE(data, pos + 4);
                length = isBigEndian 
                    ? BinaryUtils.ReadUInt16BE(data, pos + 6) 
                    : BinaryUtils.ReadUInt16LE(data, pos + 6);
                    
                info.Opcodes.Add(new OpcodeInfo
                {
                    Offset = pos,
                    Opcode = opcode,
                    Length = length,
                    IsRefCall = true,
                    RefIndex = refIdx
                });
                pos += 8 + length;
            }
            else
            {
                length = isBigEndian 
                    ? BinaryUtils.ReadUInt16BE(data, pos + 2) 
                    : BinaryUtils.ReadUInt16LE(data, pos + 2);
                    
                info.Opcodes.Add(new OpcodeInfo
                {
                    Offset = pos,
                    Opcode = opcode,
                    Length = length
                });
                pos += 4 + length;
            }

            if (info.Opcodes.Count > 500) break; // Safety limit
        }

        return info;
    }
}

/// <summary>
///     Represents a potential compiled script match.
/// </summary>
public class CompiledScriptMatch
{
    public int Offset { get; init; }
    public int Size { get; init; }
    public int StatementCount { get; init; }
    public int BeginCount { get; init; }
    public int EndCount { get; init; }
    public int CommandCalls { get; init; }
    public float Confidence { get; init; }
}

/// <summary>
///     Detailed information about a compiled script.
/// </summary>
public class CompiledScriptInfo
{
    public int Offset { get; init; }
    public int Size { get; init; }
    public required List<OpcodeInfo> Opcodes { get; init; }
}

/// <summary>
///     Information about a single opcode in compiled bytecode.
/// </summary>
public class OpcodeInfo
{
    public int Offset { get; init; }
    public ushort Opcode { get; init; }
    public ushort Length { get; init; }
    public bool IsRefCall { get; init; }
    public ushort RefIndex { get; init; }

    public string OpcodeName => Opcode switch
    {
        // Statement opcodes (from xNVSE ScriptStatementCode)
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
        // Commands
        >= 0x1000 => $"Command_0x{Opcode:X4}",
        _ => $"Unknown_0x{Opcode:X2}"
    };
}
