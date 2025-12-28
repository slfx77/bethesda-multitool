namespace Xbox360MemoryCarver.Core.Minidump;

/// <summary>
///     Information about a module loaded in the minidump.
/// </summary>
public class MinidumpModule
{
    public required string Name { get; init; }
    public long BaseAddress { get; init; }
    public int Size { get; init; }
    public uint Checksum { get; init; }
    public uint TimeDateStamp { get; init; }

    /// <summary>
    ///     Get the 32-bit base address (Xbox 360 uses 32-bit addresses).
    /// </summary>
    public uint BaseAddress32 => (uint)(BaseAddress & 0xFFFFFFFF);
}

/// <summary>
///     Represents a memory region in the minidump.
/// </summary>
public class MinidumpMemoryRegion
{
    public long VirtualAddress { get; init; }
    public long Size { get; init; }
    public long FileOffset { get; init; }
}
