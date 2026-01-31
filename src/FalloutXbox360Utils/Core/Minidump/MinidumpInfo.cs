namespace FalloutXbox360Utils.Core.Minidump;

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
        {
            if (fileOffset >= region.FileOffset && fileOffset < region.FileOffset + region.Size)
            {
                var offsetInRegion = fileOffset - region.FileOffset;
                return region.VirtualAddress + offsetInRegion;
            }
        }

        return null;
    }

    /// <summary>
    ///     Convert a virtual address to a file offset using memory regions.
    /// </summary>
    public long? VirtualAddressToFileOffset(long virtualAddress)
    {
        foreach (var region in MemoryRegions)
        {
            if (virtualAddress >= region.VirtualAddress && virtualAddress < region.VirtualAddress + region.Size)
            {
                var offsetInRegion = virtualAddress - region.VirtualAddress;
                return region.FileOffset + offsetInRegion;
            }
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
        // Pre-sort regions once rather than using LINQ OrderBy in iteration
        var sortedRegions = GetSortedRegionsAfter(regionEnd);
        foreach (var region in sortedRegions)
        {
            if (region.VirtualAddress != currentVa)
            {
                break;
            }

            if (region.VirtualAddress >= moduleEnd)
            {
                break;
            }

            var regionCapturedEnd = Math.Min(region.VirtualAddress + region.Size, moduleEnd);
            totalCaptured += regionCapturedEnd - region.VirtualAddress;
            currentVa = region.VirtualAddress + region.Size;

            if (currentVa >= moduleEnd)
            {
                break;
            }
        }

        return totalCaptured;
    }

    /// <summary>
    ///     Get memory regions after a given virtual address, sorted by address.
    ///     Used to avoid repeated LINQ Where().OrderBy() allocations.
    /// </summary>
    private List<MinidumpMemoryRegion> GetSortedRegionsAfter(long minVirtualAddress)
    {
        // Sort once and filter - more efficient than Where().OrderBy() which allocates
        var sorted = new List<MinidumpMemoryRegion>();
        foreach (var r in MemoryRegions)
        {
            if (r.VirtualAddress >= minVirtualAddress)
            {
                sorted.Add(r);
            }
        }

        sorted.Sort((a, b) => a.VirtualAddress.CompareTo(b.VirtualAddress));
        return sorted;
    }

    /// <summary>
    ///     Read a big-endian uint32 at a file offset from a stream.
    /// </summary>
    public static uint ReadBigEndianUInt32(Stream stream, long fileOffset)
    {
        stream.Seek(fileOffset, SeekOrigin.Begin);
        Span<byte> buf = stackalloc byte[4];
        stream.ReadExactly(buf);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    /// <summary>
    ///     Read a null-terminated ASCII string at a virtual address from a stream.
    /// </summary>
    public string? ReadStringAtVA(Stream stream, long va, int maxLen = 256)
    {
        var fo = VirtualAddressToFileOffset(va);
        if (fo == null)
        {
            return null;
        }

        stream.Seek(fo.Value, SeekOrigin.Begin);
        var bytes = new byte[maxLen];
        var read = stream.Read(bytes, 0, maxLen);

        for (var i = 0; i < read; i++)
        {
            if (bytes[i] == 0)
            {
                return i == 0 ? null : System.Text.Encoding.ASCII.GetString(bytes, 0, i);
            }

            if (bytes[i] < 32 || bytes[i] > 126)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    ///     Find the Fallout game module.
    /// </summary>
    public MinidumpModule? FindGameModule()
    {
        return Modules.FirstOrDefault(m =>
        {
            var nameLower = m.Name.ToLowerInvariant();
            return nameLower.Contains("fallout") && nameLower.EndsWith(".exe");
        });
    }

    #region Region Grouping for Parallel Scanning

    /// <summary>
    ///     Group memory regions by contiguous virtual address ranges.
    ///     Each group represents a continuous block of memory that can be scanned as a unit.
    ///     Non-contiguous VA gaps indicate potential file truncation boundaries.
    /// </summary>
    /// <returns>List of contiguous region groups, each group is a list of adjacent regions.</returns>
    public List<ContiguousRegionGroup> GetContiguousRegionGroups()
    {
        if (MemoryRegions.Count == 0)
        {
            return [];
        }

        var groups = new List<ContiguousRegionGroup>();
        var sorted = MemoryRegions.OrderBy(r => r.VirtualAddress).ToList();

        var currentRegions = new List<MinidumpMemoryRegion> { sorted[0] };
        var expectedNextVa = sorted[0].VirtualAddress + sorted[0].Size;

        for (var i = 1; i < sorted.Count; i++)
        {
            var region = sorted[i];

            if (region.VirtualAddress == expectedNextVa)
            {
                // Contiguous - add to current group
                currentRegions.Add(region);
            }
            else
            {
                // Gap detected - finalize current group and start new one
                groups.Add(CreateRegionGroup(currentRegions));
                currentRegions = [region];
            }

            expectedNextVa = region.VirtualAddress + region.Size;
        }

        // Add final group
        groups.Add(CreateRegionGroup(currentRegions));

        return groups;
    }

    private static ContiguousRegionGroup CreateRegionGroup(List<MinidumpMemoryRegion> regions)
    {
        var first = regions[0];
        var last = regions[^1];

        return new ContiguousRegionGroup
        {
            Regions = regions.ToList(),
            StartVirtualAddress = first.VirtualAddress,
            EndVirtualAddress = last.VirtualAddress + last.Size,
            StartFileOffset = first.FileOffset,
            TotalSize = regions.Sum(r => r.Size)
        };
    }

    /// <summary>
    ///     Find the memory region containing a given file offset.
    /// </summary>
    public MinidumpMemoryRegion? FindRegionByFileOffset(long fileOffset)
    {
        foreach (var region in MemoryRegions)
        {
            if (fileOffset >= region.FileOffset && fileOffset < region.FileOffset + region.Size)
            {
                return region;
            }
        }

        return null;
    }

    /// <summary>
    ///     Get the number of contiguous bytes available starting from a file offset.
    ///     Useful for determining maximum safe read size before hitting a VA gap.
    /// </summary>
    /// <param name="fileOffset">Starting file offset.</param>
    /// <returns>Number of contiguous bytes available, or 0 if offset is not in a region.</returns>
    public long GetContiguousBytesFromFileOffset(long fileOffset)
    {
        var region = FindRegionByFileOffset(fileOffset);
        if (region == null)
        {
            return 0;
        }

        // Start with remaining bytes in this region
        var remaining = region.Size - (fileOffset - region.FileOffset);
        var currentVaEnd = region.VirtualAddress + region.Size;

        // Check for contiguous following regions
        var sortedRegions = GetSortedRegionsAfter(currentVaEnd);
        foreach (var next in sortedRegions)
        {
            if (next.VirtualAddress != currentVaEnd)
            {
                break; // VA gap - stop accumulating
            }

            remaining += next.Size;
            currentVaEnd = next.VirtualAddress + next.Size;
        }

        return remaining;
    }

    #endregion
}
