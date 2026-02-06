using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

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

    /// <summary>
    ///     Try to find and follow a TESForm pointer near an Editor ID string.
    /// </summary>
    private static (uint formId, byte formType, long fileOffset, long pointer)? TryFollowNearbyTesFormPointer(
        byte[] buffer,
        int stringStart,
        int stringEnd,
        int bufferLength,
        long baseOffset,
        MemoryMappedViewAccessor accessor,
        MinidumpInfo minidumpInfo,
        byte[] tesFormBuffer)
    {
        // Look for Xbox 360 pointers (0x40-0x7F range) within 32 bytes before/after string
        // Xbox 360 uses big-endian, so pointers look like: XX XX XX XX where first byte is 0x40-0x7F

        var searchStart = Math.Max(0, stringStart - 32);
        var searchEnd = Math.Min(bufferLength - 4, stringEnd + 32);

        for (var i = searchStart; i < searchEnd; i += 4) // Pointers are typically 4-byte aligned
        {
            // Read as big-endian (Xbox 360)
            var pointer = BinaryUtils.ReadUInt32BE(buffer, i);

            // Check if it looks like a valid pointer in captured memory
            if (!IsValidPointerInDump(pointer, minidumpInfo))
            {
                continue;
            }

            // Try to convert to file offset
            var fileOffset = minidumpInfo.VirtualAddressToFileOffset(Xbox360VaToLong(pointer));
            if (!fileOffset.HasValue)
            {
                continue;
            }

            // Read potential TESForm at this offset
            try
            {
                accessor.ReadArray(fileOffset.Value, tesFormBuffer, 0, 24);

                // Validate TESForm structure
                // Offset 4: cFormType (should be < 200 for valid types)
                // Offset 12: iFormID (read as big-endian)
                var formType = tesFormBuffer[4];
                if (formType > 200)
                {
                    continue;
                }

                var formId = BinaryUtils.ReadUInt32BE(tesFormBuffer, 12);

                // Basic validation: FormID should not be 0 or 0xFFFFFFFF
                if (formId == 0 || formId == 0xFFFFFFFF)
                {
                    continue;
                }

                // Plugin index validation (upper byte, typically 0x00-0xFF)
                var pluginIndex = formId >> 24;
                if (pluginIndex > 0xFF)
                {
                    continue;
                }

                return (formId, formType, fileOffset.Value, Xbox360VaToLong(pointer));
            }
            catch
            {
                // Ignore read errors
            }
        }

        return null;
    }

    #endregion
}
