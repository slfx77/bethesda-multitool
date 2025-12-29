using System.Text;
using Xbox360MemoryCarver.Core.Minidump;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Scanner for ScriptInfo structures in Xbox 360 memory dumps.
///     
///     ScriptInfo structure (from xNVSE, 20 bytes / 0x14):
///     - 0x00: unusedVariableCount (4 bytes, big-endian on Xbox)
///     - 0x04: numRefs (4 bytes)
///     - 0x08: dataLength (4 bytes) - compiled bytecode length
///     - 0x0C: varCount (4 bytes)
///     - 0x10: type (2 bytes) - 0=Object, 1=Quest, 0x100=Magic
///     - 0x12: compiled (1 byte) - TRUE (1) if compiled
///     - 0x13: unk (1 byte)
///     
///     After ScriptInfo in the Script object:
///     - text pointer (4 bytes) - NULL in release builds
///     - data pointer (4 bytes) - points to compiled bytecode (virtual address)
/// </summary>
public static class ScriptInfoScanner
{
    public const int ScriptInfoSize = 0x14; // 20 bytes

    /// <summary>
    ///     Scan for potential ScriptInfo structures in memory.
    /// </summary>
    public static List<ScriptInfoMatch> ScanForScriptInfo(
        ReadOnlySpan<byte> data,
        int startOffset = 0,
        int maxResults = 1000,
        bool verbose = false)
    {
        var results = new List<ScriptInfoMatch>();

        for (var i = startOffset; i < data.Length - ScriptInfoSize && results.Count < maxResults; i++)
        {
            var match = TryParseScriptInfo(data, i);
            if (match != null)
            {
                results.Add(match);
                
                if (verbose)
                {
                    Console.WriteLine($"  Found ScriptInfo at 0x{i:X8}: dataLen={match.DataLength}, refs={match.NumRefs}, vars={match.VarCount}, type={match.ScriptType}");
                }
            }
        }

        return results;
    }

    /// <summary>
    ///     Try to parse a ScriptInfo structure at the given offset.
    /// </summary>
    public static ScriptInfoMatch? TryParseScriptInfo(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + ScriptInfoSize + 8 > data.Length) return null; // +8 for text/data pointers

        // Read fields as big-endian (Xbox 360 / PowerPC)
        var unusedVarCount = BinaryUtils.ReadUInt32BE(data, offset);
        var numRefs = BinaryUtils.ReadUInt32BE(data, offset + 4);
        var dataLength = BinaryUtils.ReadUInt32BE(data, offset + 8);
        var varCount = BinaryUtils.ReadUInt32BE(data, offset + 12);
        var type = BinaryUtils.ReadUInt16BE(data, offset + 16);
        var compiled = data[offset + 18];
        var unk = data[offset + 19];
        
        // Read pointers after ScriptInfo
        var textPtr = BinaryUtils.ReadUInt32BE(data, offset + ScriptInfoSize);
        var dataPtr = BinaryUtils.ReadUInt32BE(data, offset + ScriptInfoSize + 4);

        // Validate constraints for a plausible ScriptInfo
        
        // compiled MUST be 1 for us to find compiled scripts
        if (compiled != 1) return null;

        // dataLength should be reasonable (8 to 10KB for most scripts)
        if (dataLength < 8 || dataLength > 10 * 1024) return null;
        
        // Avoid very round numbers that are likely coincidental
        if (dataLength == 8192 || dataLength == 4096 || dataLength == 2048 || 
            dataLength == 1024 || dataLength == 512 || dataLength == 256 ||
            dataLength == 16) return null;

        // type should be 0 (Object), 1 (Quest), or 0x100 (Magic)
        if (type != 0 && type != 1 && type != 0x100) return null;

        // numRefs should be 1-50 for most scripts (need at least 1)
        if (numRefs == 0 || numRefs > 50) return null;

        // varCount can be 0 but shouldn't be huge
        if (varCount > 30) return null;

        // unusedVarCount should typically be 0 or very small
        if (unusedVarCount > 10) return null;
        
        // unk byte should be 0
        if (unk != 0) return null;
        
        // For Release builds, text pointer should be 0 (NULL)
        // For Debug builds, it points to source text
        // Data pointer should be non-zero and look like a valid address
        if (dataPtr == 0) return null;
        
        // Xbox 360 virtual addresses are typically in the 0x40000000-0x90000000 range
        if (dataPtr < 0x40000000 || dataPtr > 0xA0000000) return null;

        return new ScriptInfoMatch
        {
            Offset = offset,
            UnusedVarCount = unusedVarCount,
            NumRefs = numRefs,
            DataLength = dataLength,
            VarCount = varCount,
            ScriptType = type switch
            {
                0 => "Object",
                1 => "Quest",
                0x100 => "Magic",
                _ => $"Unknown({type})"
            },
            IsCompiled = compiled == 1,
            TextPointer = textPtr,
            DataPointer = dataPtr
        };
    }

    /// <summary>
    ///     Try to extract bytecode using minidump memory mapping.
    /// </summary>
    public static byte[]? TryExtractBytecode(
        ReadOnlySpan<byte> fileData,
        ScriptInfoMatch scriptInfo,
        MinidumpInfo minidump)
    {
        if (scriptInfo.DataPointer == 0) return null;
        
        // Convert virtual address to file offset
        var fileOffset = minidump.VirtualAddressToFileOffset(scriptInfo.DataPointer);
        if (!fileOffset.HasValue) return null;
        
        var offset = (int)fileOffset.Value;
        var length = (int)scriptInfo.DataLength;
        
        if (offset < 0 || offset + length > fileData.Length) return null;
        
        var bytecode = new byte[length];
        fileData.Slice(offset, length).CopyTo(bytecode);
        
        return bytecode;
    }
}

/// <summary>
///     Represents a potential ScriptInfo structure found in memory.
/// </summary>
public class ScriptInfoMatch
{
    public int Offset { get; init; }
    public uint UnusedVarCount { get; init; }
    public uint NumRefs { get; init; }
    public uint DataLength { get; init; }
    public uint VarCount { get; init; }
    public string ScriptType { get; init; } = "Unknown";
    public bool IsCompiled { get; init; }
    public uint TextPointer { get; init; }
    public uint DataPointer { get; init; }
}
