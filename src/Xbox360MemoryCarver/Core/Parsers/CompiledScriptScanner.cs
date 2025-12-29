using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Scanner for compiled Bethesda script bytecode in Xbox 360 memory dumps.
///     
///     IMPORTANT: Xbox 360 uses PowerPC (big-endian), so all multi-byte values
///     are read in big-endian format.
///     
///     This is experimental and may produce false positives. It's designed to
///     find scripts in Release builds where the source text is not available.
/// </summary>
public static class CompiledScriptScanner
{
    // Opcodes in big-endian format for Xbox 360
    private const ushort Opcode_ScriptName = 0x0010;
    private const ushort Opcode_Begin = 0x0011;
    private const ushort Opcode_End = 0x0012;

    /// <summary>
    ///     Scan a memory region for potential compiled scripts.
    /// </summary>
    /// <param name="data">The data to scan.</param>
    /// <param name="startOffset">Starting offset in the data.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <returns>List of potential compiled script locations and sizes.</returns>
    public static List<CompiledScriptMatch> ScanForCompiledScripts(
        ReadOnlySpan<byte> data,
        int startOffset = 0,
        int maxResults = 1000)
    {
        var results = new List<CompiledScriptMatch>();
        var parser = new CompiledScriptParser();

        // Scan for potential script starts
        // In big-endian, ScriptName opcode 0x0010 appears as bytes: 0x00 0x10
        for (var i = startOffset; i < data.Length - 8 && results.Count < maxResults; i++)
        {
            // Quick check for ScriptName opcode in big-endian (0x00 0x10)
            if (data[i] != 0x00 || data[i + 1] != 0x10) continue;

            // Get the length field (big-endian)
            var length = BinaryUtils.ReadUInt16BE(data, i + 2);

            // ScriptName typically has length 0-4
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

        return Math.Min(confidence, 1.0f);
    }

    /// <summary>
    ///     Analyze a compiled script and extract detailed opcode information.
    ///     All reads are big-endian for Xbox 360.
    /// </summary>
    public static CompiledScriptInfo? AnalyzeCompiledScript(ReadOnlySpan<byte> data, int offset)
    {
        var parser = new CompiledScriptParser();
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
            // Read as big-endian
            var opcode = BinaryUtils.ReadUInt16BE(data, pos);
            var length = BinaryUtils.ReadUInt16BE(data, pos + 2);

            // Handle ReferenceFunction specially
            if (opcode == 0x001D && pos + 8 <= end)
            {
                var refIdx = BinaryUtils.ReadUInt16BE(data, pos + 2);
                opcode = BinaryUtils.ReadUInt16BE(data, pos + 4);
                length = BinaryUtils.ReadUInt16BE(data, pos + 6);
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
        0x0010 => "ScriptName",
        0x0011 => "Begin",
        0x0012 => "End",
        0x0013 => "short",
        0x0014 => "long",
        0x0015 => "float",
        0x0016 => "Set",
        0x0017 => "If",
        0x0018 => "Else",
        0x0019 => "ElseIf",
        0x001A => "EndIf",
        0x001B => "While",
        0x001C => "ref",
        0x001D => "RefFunction",
        0x001E => "Return",
        0x001F => "Loop",
        >= 0x1000 => $"Command(0x{Opcode:X4})",
        _ => $"Unknown(0x{Opcode:X4})"
    };
}
