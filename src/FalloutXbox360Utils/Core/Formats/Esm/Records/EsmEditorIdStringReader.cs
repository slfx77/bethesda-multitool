using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Records;

/// <summary>
///     Reads BSStringT&lt;char&gt; strings from TESForm objects in Xbox 360 memory dumps.
///     Shared by <see cref="EsmEditorIdExtractor" /> and <see cref="EditorIdLookupTables" />.
/// </summary>
internal static class EsmEditorIdStringReader
{
    /// <summary>
    ///     Read a BSStringT&lt;char&gt; string from a TESForm object in the dump.
    ///     BSStringT layout (8 bytes, big-endian on Xbox 360):
    ///     Offset 0: pString (char* pointer, 4 bytes BE)
    ///     Offset 4: sLen (uint16 BE)
    /// </summary>
    internal static string? ReadBsStringT(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        long tesFormFileOffset,
        int fieldOffset)
    {
        var bstOffset = tesFormFileOffset + fieldOffset;
        if (bstOffset + 8 > fileSize)
        {
            return null;
        }

        var bstBuffer = new byte[8];
        accessor.ReadArray(bstOffset, bstBuffer, 0, 8);

        var pString = BinaryUtils.ReadUInt32BE(bstBuffer);
        var sLen = BinaryUtils.ReadUInt16BE(bstBuffer, 4);

        if (pString == 0 || sLen == 0 || sLen > 4096)
        {
            return null;
        }

        if (!Xbox360MemoryUtils.IsValidPointerInDump(pString, minidumpInfo))
        {
            return null;
        }

        var strFileOffset = minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(pString));
        if (!strFileOffset.HasValue || strFileOffset.Value + sLen > fileSize)
        {
            return null;
        }

        var strBuffer = new byte[sLen];
        accessor.ReadArray(strFileOffset.Value, strBuffer, 0, sLen);

        return EsmStringUtils.ValidateAndDecodeGameText(strBuffer, sLen);
    }
}
