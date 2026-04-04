using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Pdb;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     Shared context for all buffer analysis collaborators.
/// </summary>
internal sealed class BufferAnalysisContext
{
    public BufferAnalysisContext(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        CoverageResult coverage,
        PdbAnalysisResult? pdbAnalysis,
        IReadOnlyList<RuntimeEditorIdEntry>? runtimeEditorIds,
        uint moduleStart,
        uint moduleEnd,
        IReadOnlyList<GmstRecord>? gameSettings = null)
    {
        Accessor = accessor;
        FileSize = fileSize;
        MinidumpInfo = minidumpInfo;
        Coverage = coverage;
        PdbAnalysis = pdbAnalysis;
        RuntimeEditorIds = runtimeEditorIds;
        ModuleStart = moduleStart;
        ModuleEnd = moduleEnd;
        GameSettings = gameSettings;
    }

    public MemoryMappedViewAccessor Accessor { get; }
    public long FileSize { get; }
    public MinidumpInfo MinidumpInfo { get; }
    public CoverageResult Coverage { get; }
    public PdbAnalysisResult? PdbAnalysis { get; }
    public IReadOnlyList<RuntimeEditorIdEntry>? RuntimeEditorIds { get; }
    public uint ModuleStart { get; }
    public uint ModuleEnd { get; }
    public IReadOnlyList<GmstRecord>? GameSettings { get; }

    /// <summary>
    ///     Check if a 32-bit value is a valid pointer in the minidump.
    /// </summary>
    public bool IsValidPointer(uint va)
    {
        return va != 0 && Xbox360MemoryUtils.IsValidPointerInDump(va, MinidumpInfo);
    }

    /// <summary>
    ///     Convert a 32-bit Xbox 360 virtual address to file offset.
    /// </summary>
    public long? VaToFileOffset(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        return MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
    }

    /// <summary>
    ///     Count valid pointers in a buffer (for structure analysis).
    /// </summary>
    public int CountValidPointers(byte[] buffer)
    {
        var count = 0;
        for (var i = 0; i <= buffer.Length - 4; i += 4)
        {
            var ptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (ptr != 0 && IsValidPointer(ptr))
            {
                count++;
            }
        }

        return count;
    }
}
