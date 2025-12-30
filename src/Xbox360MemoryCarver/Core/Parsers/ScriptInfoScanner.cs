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
///     After ScriptInfo in the Script object (RUNTIME layout):
///     - 0x14: text pointer (4 bytes) - NULL in release builds
///     - 0x18: data pointer (4 bytes) - points to compiled bytecode (virtual address)
///     - 0x1C: unk34 float (4 bytes)
///     - 0x20: questDelayTimeCounter float (4 bytes)
///     - 0x24: secondsPassed float (4 bytes)
///     - 0x28: quest pointer (4 bytes)
///     - 0x2C: refList.data (4 bytes) - pointer to first RefVariable
///     - 0x30: refList.next (4 bytes) - pointer to next list node
///     - 0x34: varList.data (4 bytes) - pointer to first VariableInfo
///     - 0x38: varList.next (4 bytes) - pointer to next list node
/// </summary>
public static class ScriptInfoScanner
{
    public const int ScriptInfoSize = 0x14; // 20 bytes
    
    // Offsets from ScriptInfo to other fields
    private const int TextPtrOffset = 0x14;
    private const int DataPtrOffset = 0x18;
    private const int RefListOffset = 0x2C;
    private const int VarListOffset = 0x34;

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
        var textPtr = BinaryUtils.ReadUInt32BE(data, offset + TextPtrOffset);
        var dataPtr = BinaryUtils.ReadUInt32BE(data, offset + DataPtrOffset);
        
        // Read refList and varList pointers (if enough data)
        uint refListPtr = 0, varListPtr = 0;
        if (offset + VarListOffset + 4 <= data.Length)
        {
            refListPtr = BinaryUtils.ReadUInt32BE(data, offset + RefListOffset);
            varListPtr = BinaryUtils.ReadUInt32BE(data, offset + VarListOffset);
        }

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
            DataPointer = dataPtr,
            RefListPointer = refListPtr,
            VarListPointer = varListPtr
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

    /// <summary>
    ///     Try to extract variable names from the varList linked list.
    /// </summary>
    public static List<VariableInfoData>? TryExtractVariables(
        ReadOnlySpan<byte> fileData,
        ScriptInfoMatch scriptInfo,
        MinidumpInfo minidump)
    {
        if (scriptInfo.VarListPointer == 0 || scriptInfo.VarCount == 0)
            return null;

        var variables = new List<VariableInfoData>();
        var currentPtr = scriptInfo.VarListPointer;
        var maxIterations = (int)Math.Max(scriptInfo.VarCount * 2, 100);
        var visited = new HashSet<uint>();

        // The tList structure is:
        // - data (4 bytes): pointer to VariableInfo
        // - next (4 bytes): pointer to next tList node
        
        for (var i = 0; i < maxIterations && currentPtr != 0; i++)
        {
            if (visited.Contains(currentPtr)) break;
            visited.Add(currentPtr);

            var listNodeOffset = minidump.VirtualAddressToFileOffset(currentPtr);
            if (!listNodeOffset.HasValue) break;

            var nodeOff = (int)listNodeOffset.Value;
            if (nodeOff + 8 > fileData.Length) break;

            var varInfoPtr = BinaryUtils.ReadUInt32BE(fileData, nodeOff);
            var nextPtr = BinaryUtils.ReadUInt32BE(fileData, nodeOff + 4);

            if (varInfoPtr != 0)
            {
                var varInfo = TryReadVariableInfo(fileData, varInfoPtr, minidump);
                if (varInfo != null)
                {
                    variables.Add(varInfo);
                }
            }

            currentPtr = nextPtr;
        }

        return variables.Count > 0 ? variables : null;
    }

    /// <summary>
    ///     Try to read a VariableInfo structure from memory.
    ///     
    ///     VariableInfo structure (0x20 bytes):
    ///     - 0x00: idx (4 bytes)
    ///     - 0x04: pad04 (4 bytes)
    ///     - 0x08: data (8 bytes, double)
    ///     - 0x10: type (1 byte)
    ///     - 0x11: pad11 (3 bytes)
    ///     - 0x14: unk14 (4 bytes)
    ///     - 0x18: name.m_data (4 bytes, pointer)
    ///     - 0x1C: name.m_dataLen (2 bytes)
    ///     - 0x1E: name.m_bufLen (2 bytes)
    /// </summary>
    private static VariableInfoData? TryReadVariableInfo(
        ReadOnlySpan<byte> fileData,
        uint varInfoPtr,
        MinidumpInfo minidump)
    {
        var fileOffset = minidump.VirtualAddressToFileOffset(varInfoPtr);
        if (!fileOffset.HasValue) return null;

        var offset = (int)fileOffset.Value;
        if (offset + 0x20 > fileData.Length) return null;

        var idx = BinaryUtils.ReadUInt32BE(fileData, offset);
        var type = fileData[offset + 0x10];
        var namePtr = BinaryUtils.ReadUInt32BE(fileData, offset + 0x18);
        var nameLen = BinaryUtils.ReadUInt16BE(fileData, offset + 0x1C);

        string? name = null;
        if (namePtr != 0 && nameLen > 0 && nameLen < 256)
        {
            var nameOffset = minidump.VirtualAddressToFileOffset(namePtr);
            if (nameOffset.HasValue && nameOffset.Value + nameLen <= fileData.Length)
            {
                name = Encoding.ASCII.GetString(
                    fileData.Slice((int)nameOffset.Value, nameLen).ToArray());
            }
        }

        return new VariableInfoData
        {
            Index = idx,
            Type = type switch
            {
                0 => "Float",
                1 => "Integer",
                2 => "String",
                3 => "Array",
                4 => "Ref",
                _ => $"Unknown({type})"
            },
            Name = name
        };
    }

    /// <summary>
    ///     Try to extract reference variable names from the refList linked list.
    /// </summary>
    public static List<RefVariableData>? TryExtractRefVariables(
        ReadOnlySpan<byte> fileData,
        ScriptInfoMatch scriptInfo,
        MinidumpInfo minidump)
    {
        if (scriptInfo.RefListPointer == 0 || scriptInfo.NumRefs == 0)
            return null;

        var refVars = new List<RefVariableData>();
        var currentPtr = scriptInfo.RefListPointer;
        var maxIterations = (int)Math.Max(scriptInfo.NumRefs * 2, 100);
        var visited = new HashSet<uint>();

        for (var i = 0; i < maxIterations && currentPtr != 0; i++)
        {
            if (visited.Contains(currentPtr)) break;
            visited.Add(currentPtr);

            var listNodeOffset = minidump.VirtualAddressToFileOffset(currentPtr);
            if (!listNodeOffset.HasValue) break;

            var nodeOff = (int)listNodeOffset.Value;
            if (nodeOff + 8 > fileData.Length) break;

            var refVarPtr = BinaryUtils.ReadUInt32BE(fileData, nodeOff);
            var nextPtr = BinaryUtils.ReadUInt32BE(fileData, nodeOff + 4);

            if (refVarPtr != 0)
            {
                var refVar = TryReadRefVariable(fileData, refVarPtr, minidump);
                if (refVar != null)
                {
                    refVars.Add(refVar);
                }
            }

            currentPtr = nextPtr;
        }

        return refVars.Count > 0 ? refVars : null;
    }

    /// <summary>
    ///     Try to read a RefVariable structure from memory.
    ///     
    ///     RefVariable structure (0x10 bytes):
    ///     - 0x00: name.m_data (4 bytes, pointer)
    ///     - 0x04: name.m_dataLen (2 bytes)
    ///     - 0x06: name.m_bufLen (2 bytes)
    ///     - 0x08: form (4 bytes, pointer)
    ///     - 0x0C: varIdx (4 bytes)
    /// </summary>
    private static RefVariableData? TryReadRefVariable(
        ReadOnlySpan<byte> fileData,
        uint refVarPtr,
        MinidumpInfo minidump)
    {
        var fileOffset = minidump.VirtualAddressToFileOffset(refVarPtr);
        if (!fileOffset.HasValue) return null;

        var offset = (int)fileOffset.Value;
        if (offset + 0x10 > fileData.Length) return null;

        var namePtr = BinaryUtils.ReadUInt32BE(fileData, offset);
        var nameLen = BinaryUtils.ReadUInt16BE(fileData, offset + 0x04);
        var formPtr = BinaryUtils.ReadUInt32BE(fileData, offset + 0x08);
        var varIdx = BinaryUtils.ReadUInt32BE(fileData, offset + 0x0C);

        string? name = null;
        if (namePtr != 0 && nameLen > 0 && nameLen < 256)
        {
            var nameOffset = minidump.VirtualAddressToFileOffset(namePtr);
            if (nameOffset.HasValue && nameOffset.Value + nameLen <= fileData.Length)
            {
                name = Encoding.ASCII.GetString(
                    fileData.Slice((int)nameOffset.Value, nameLen).ToArray());
            }
        }

        return new RefVariableData
        {
            Index = varIdx,
            FormPointer = formPtr,
            Name = name
        };
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
    public uint RefListPointer { get; init; }
    public uint VarListPointer { get; init; }
}

/// <summary>
///     Extracted variable information.
/// </summary>
public class VariableInfoData
{
    public uint Index { get; init; }
    public string Type { get; init; } = "Unknown";
    public string? Name { get; init; }
}

/// <summary>
///     Extracted reference variable information.
/// </summary>
public class RefVariableData
{
    public uint Index { get; init; }
    public uint FormPointer { get; init; }
    public string? Name { get; init; }
}
