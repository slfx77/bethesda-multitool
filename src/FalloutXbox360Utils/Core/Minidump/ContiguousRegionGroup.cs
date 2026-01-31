namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     A group of memory regions with contiguous virtual addresses.
///     Can be scanned as a single unit for parallel processing.
/// </summary>
public class ContiguousRegionGroup
{
    /// <summary>
    ///     The individual memory regions in this group (sorted by VA).
    /// </summary>
    public required List<MinidumpMemoryRegion> Regions { get; init; }

    /// <summary>
    ///     Starting virtual address of the group.
    /// </summary>
    public long StartVirtualAddress { get; init; }

    /// <summary>
    ///     Ending virtual address of the group (exclusive).
    /// </summary>
    public long EndVirtualAddress { get; init; }

    /// <summary>
    ///     Starting file offset of the group.
    /// </summary>
    public long StartFileOffset { get; init; }

    /// <summary>
    ///     Total size in bytes across all regions in the group.
    /// </summary>
    public long TotalSize { get; init; }

    /// <summary>
    ///     Number of regions in this group.
    /// </summary>
    public int RegionCount => Regions.Count;
}
