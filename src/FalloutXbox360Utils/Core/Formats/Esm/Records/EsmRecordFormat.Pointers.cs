using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class EsmRecordFormat
{
    #region Xbox 360 Virtual Address Conversion

    /// <summary>
    ///     Convert a 32-bit Xbox 360 virtual address to the 64-bit representation
    ///     used by minidump memory regions. Xbox 360 addresses with bit 31 set
    ///     (e.g., module space at 0x82XXXXXX) are stored sign-extended in minidumps.
    ///     Uses the shared utility from Xbox360MemoryUtils.
    /// </summary>
    internal static long Xbox360VaToLong(uint address)
    {
        return Xbox360MemoryUtils.VaToLong(address);
    }

    #endregion

    #region Pointer Validation

    internal static bool IsValidPointerInDump(uint value, MinidumpInfo minidumpInfo)
    {
        // Dynamically check if this pointer falls within any captured memory region
        // This handles all Xbox 360 builds regardless of memory layout
        if (value == 0)
        {
            return false;
        }

        return minidumpInfo.VirtualAddressToFileOffset(Xbox360VaToLong(value)).HasValue;
    }

    #endregion
    #region TESForm Pointer Following

    #endregion
}
