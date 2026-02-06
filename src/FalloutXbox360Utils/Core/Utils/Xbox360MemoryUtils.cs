namespace FalloutXbox360Utils.Core.Utils;

/// <summary>
///     Xbox 360 memory layout and pointer utilities for minidump analysis.
/// </summary>
public static class Xbox360MemoryUtils
{
    /// <summary>
    ///     Xbox 360 heap base address.
    /// </summary>
    public const uint HeapBase = 0x40000000;

    /// <summary>
    ///     Xbox 360 heap end address.
    /// </summary>
    public const uint HeapEnd = 0x50000000;

    /// <summary>
    ///     Xbox 360 module space base address.
    /// </summary>
    public const uint ModuleBase = 0x82000000;

    /// <summary>
    ///     Convert a 32-bit Xbox 360 virtual address to the 64-bit representation
    ///     used by minidump memory regions. Xbox 360 addresses with bit 31 set
    ///     (e.g., module space at 0x82XXXXXX) are stored sign-extended in minidumps.
    /// </summary>
    public static long VaToLong(uint address)
    {
        return unchecked((int)address);
    }

    /// <summary>
    ///     Check if a 32-bit value could be a valid Xbox 360 pointer.
    ///     Valid pointers are typically in heap range (0x40000000-0x50000000)
    ///     or module range (0x82000000+).
    /// </summary>
    public static bool IsValidPointer(uint value)
    {
        // Heap range
        if (value >= HeapBase && value < HeapEnd)
        {
            return true;
        }

        // Module range (code/data segments)
        if (value >= ModuleBase)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Check if a value looks like a heap pointer (in the 0x40000000-0x50000000 range).
    /// </summary>
    public static bool IsHeapPointer(uint value)
    {
        return value >= HeapBase && value < HeapEnd;
    }

    /// <summary>
    ///     Check if a value looks like a module pointer (0x82000000+).
    /// </summary>
    public static bool IsModulePointer(uint value)
    {
        return value >= ModuleBase;
    }
}
