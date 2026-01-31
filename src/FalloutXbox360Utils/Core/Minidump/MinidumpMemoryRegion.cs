namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Represents a memory region in the minidump.
/// </summary>
public class MinidumpMemoryRegion
{
    public long VirtualAddress { get; init; }
    public long Size { get; init; }
    public long FileOffset { get; init; }
}
