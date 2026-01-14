namespace Xbox360MemoryCarver.Core.Minidump;

/// <summary>
///     Parsed minidump header and directory information.
/// </summary>
public class MinidumpInfo
{
    public bool IsValid { get; init; }
    public ushort ProcessorArchitecture { get; set; }
    public uint NumberOfStreams { get; init; }
    public List<MinidumpModule> Modules { get; init; } = [];
    public List<MinidumpMemoryRegion> MemoryRegions { get; init; } = [];

    /// <summary>
    ///     True if this is an Xbox 360 (PowerPC) minidump.
    /// </summary>
    public bool IsXbox360 => ProcessorArchitecture == 0x03; // PowerPC

    /// <summary>
    ///     Size of the minidump header and directory (before memory data starts).
    /// </summary>
    public long HeaderSize => MemoryRegions.Count > 0
        ? MemoryRegions.Min(r => r.FileOffset)
        : 0;

    /// <summary>
    ///     Find a module by virtual address.
    /// </summary>
    public MinidumpModule? FindModuleByVirtualAddress(long virtualAddress)
    {
        return Modules.FirstOrDefault(m =>
            virtualAddress >= m.BaseAddress &&
            virtualAddress < m.BaseAddress + m.Size);
    }

    /// <summary>
    ///     Convert a file offset to a virtual address using memory regions.
    /// </summary>
    public long? FileOffsetToVirtualAddress(long fileOffset)
    {
        foreach (var region in MemoryRegions)
            if (fileOffset >= region.FileOffset && fileOffset < region.FileOffset + region.Size)
            {
                var offsetInRegion = fileOffset - region.FileOffset;
                return region.VirtualAddress + offsetInRegion;
            }

        return null;
    }

    /// <summary>
    ///     Convert a virtual address to a file offset using memory regions.
    /// </summary>
    public long? VirtualAddressToFileOffset(long virtualAddress)
    {
        foreach (var region in MemoryRegions)
            if (virtualAddress >= region.VirtualAddress && virtualAddress < region.VirtualAddress + region.Size)
            {
                var offsetInRegion = virtualAddress - region.VirtualAddress;
                return region.FileOffset + offsetInRegion;
            }

        return null;
    }

    /// <summary>
    ///     Get the file offset range for a module (if its memory is captured in the dump).
    /// </summary>
    public (long fileOffset, long size)? GetModuleFileRange(MinidumpModule module)
    {
        var moduleStart = module.BaseAddress;

        foreach (var region in MemoryRegions)
        {
            var regionStart = region.VirtualAddress;
            var regionEnd = region.VirtualAddress + region.Size;

            if (moduleStart >= regionStart && moduleStart < regionEnd)
            {
                var offsetInRegion = moduleStart - regionStart;
                var fileOffset = region.FileOffset + offsetInRegion;
                var capturedSize = CalculateContiguousCapturedSize(module, region);
                return (fileOffset, capturedSize);
            }
        }

        return null;
    }

    private long CalculateContiguousCapturedSize(MinidumpModule module, MinidumpMemoryRegion startRegion)
    {
        var moduleStart = module.BaseAddress;
        var moduleEnd = module.BaseAddress + module.Size;

        var regionEnd = startRegion.VirtualAddress + startRegion.Size;
        var capturedEnd = Math.Min(regionEnd, moduleEnd);
        var totalCaptured = capturedEnd - moduleStart;

        var currentVa = regionEnd;
        foreach (var region in MemoryRegions.Where(r => r.VirtualAddress >= regionEnd).OrderBy(r => r.VirtualAddress))
        {
            if (region.VirtualAddress != currentVa) break;
            if (region.VirtualAddress >= moduleEnd) break;

            var regionCapturedEnd = Math.Min(region.VirtualAddress + region.Size, moduleEnd);
            totalCaptured += regionCapturedEnd - region.VirtualAddress;
            currentVa = region.VirtualAddress + region.Size;

            if (currentVa >= moduleEnd) break;
        }

        return totalCaptured;
    }
}
