using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

public sealed partial class RuntimeStructReader
{
    /// <summary>
    ///     Read a BSStringT string from a TESForm object.
    ///     BSStringT layout (8 bytes, big-endian):
    ///     Offset 0: pString (char* pointer, 4 bytes BE)
    ///     Offset 4: sLen (uint16 BE)
    /// </summary>
    public string? ReadBSStringT(long tesFormFileOffset, int fieldOffset)
    {
        var bstOffset = tesFormFileOffset + fieldOffset;
        if (bstOffset + 8 > _fileSize)
        {
            return null;
        }

        var bstBuffer = new byte[8];
        _accessor.ReadArray(bstOffset, bstBuffer, 0, 8);

        var pString = BinaryUtils.ReadUInt32BE(bstBuffer);
        var sLen = BinaryUtils.ReadUInt16BE(bstBuffer, 4);

        if (pString == 0 || sLen == 0 || sLen > EsmStringUtils.MaxBSStringLength)
        {
            return null;
        }

        if (!IsValidPointer(pString))
        {
            return null;
        }

        var strFileOffset = _minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(pString));
        if (!strFileOffset.HasValue || strFileOffset.Value + sLen > _fileSize)
        {
            return null;
        }

        var strBuffer = new byte[sLen];
        _accessor.ReadArray(strFileOffset.Value, strBuffer, 0, sLen);

        // Validate and decode using EsmStringUtils
        return EsmStringUtils.ValidateAndDecodeAscii(strBuffer, sLen);
    }
}
