using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Shared context for reading runtime game structures from Xbox 360 memory dumps.
///     Holds the accessor, file size, and minidump info, plus core helper methods
///     used by all domain-specific readers.
/// </summary>
internal sealed class RuntimeMemoryContext(
    IMemoryAccessor accessor,
    long fileSize,
    MinidumpInfo minidumpInfo)
{
    /// <summary>Maximum number of items to read from linked lists (cycle prevention).</summary>
    public const int MaxListItems = 50;

    public IMemoryAccessor Accessor { get; } = accessor;
    public long FileSize { get; } = fileSize;
    public MinidumpInfo MinidumpInfo { get; } = minidumpInfo;

    /// <summary>
    ///     Check if a 32-bit value is a valid Xbox 360 pointer within captured memory.
    /// </summary>
    public bool IsValidPointer(uint value)
    {
        if (value == 0)
        {
            return false;
        }

        return MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(value)).HasValue;
    }

    /// <summary>
    ///     Convert a 32-bit Xbox 360 virtual address to a file offset in the dump.
    ///     Returns null if the VA is not in any captured memory region.
    /// </summary>
    public long? VaToFileOffset(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        return MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
    }

    /// <summary>
    ///     Check if a float is a normal (non-NaN, non-Infinity) value.
    /// </summary>
    public static bool IsNormalFloat(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    ///     Read a byte array from the dump file at a given file offset.
    ///     Returns null if the read fails.
    /// </summary>
    public byte[]? ReadBytes(long fileOffset, int count)
    {
        if (fileOffset + count > FileSize)
        {
            return null;
        }

        var buf = new byte[count];
        try
        {
            Accessor.ReadArray(fileOffset, buf, 0, count);
            return buf;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Read a byte array from the dump at a given virtual address, validating that the
    ///     entire VA range [va, va+count) falls within captured memory regions. This prevents
    ///     reading garbage data when a struct spans a gap between non-contiguous memory regions.
    ///     Returns null if the VA range is not fully captured or the read fails.
    /// </summary>
    public byte[]? ReadBytesAtVa(long va, int count)
    {
        if (!MinidumpInfo.IsVaRangeCaptured(va, count))
        {
            return null;
        }

        var fileOffset = MinidumpInfo.VirtualAddressToFileOffset(va);
        if (!fileOffset.HasValue || fileOffset.Value + count > FileSize)
        {
            return null;
        }

        return ReadBytes(fileOffset.Value, count);
    }

    public static int ReadInt32BE(byte[] data, int offset)
    {
        return (int)BinaryUtils.ReadUInt32BE(data, offset);
    }

    /// <summary>
    ///     Read a float and validate it's within an expected range.
    ///     Returns 0 if the value is NaN, Inf, or outside range.
    /// </summary>
    public static float ReadValidatedFloat(byte[] buffer, int offset, float min, float max)
    {
        if (offset + 4 > buffer.Length)
        {
            return 0;
        }

        var value = BinaryUtils.ReadFloatBE(buffer, offset);
        if (!IsNormalFloat(value) || value < min || value > max)
        {
            return 0;
        }

        return value;
    }

    /// <summary>
    ///     Follow a 4-byte big-endian pointer at the given buffer offset to a TESForm object,
    ///     then read and return the FormID (uint32 BE at offset 12 in TESForm header).
    ///     Returns null if the pointer is invalid or the target is not a valid TESForm.
    /// </summary>
    public uint? FollowPointerToFormId(byte[] buffer, int pointerOffset)
    {
        return FollowPointerToFormIdCore(buffer, pointerOffset, null);
    }

    /// <summary>
    ///     Follow a pointer to a TESForm, but only return the FormID if the target's
    ///     FormType matches the expected type. Returns null for type mismatches.
    ///     This prevents stale/garbage pointers from resolving to unrelated form types
    ///     (e.g., a speaker pointer resolving to a DIAL topic instead of an NPC).
    /// </summary>
    public uint? FollowPointerToFormId(byte[] buffer, int pointerOffset, byte expectedFormType)
    {
        return FollowPointerToFormIdCore(buffer, pointerOffset, expectedFormType);
    }

    private uint? FollowPointerToFormIdCore(byte[] buffer, int pointerOffset, byte? expectedFormType)
    {
        if (pointerOffset + 4 > buffer.Length)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, pointerOffset);
        if (pointer == 0)
        {
            return null;
        }

        if (!IsValidPointer(pointer))
        {
            return null;
        }

        var fileOffset = MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(pointer));
        if (!fileOffset.HasValue || fileOffset.Value + 24 > FileSize)
        {
            return null;
        }

        var tesFormBuffer = new byte[24];
        try
        {
            Accessor.ReadArray(fileOffset.Value, tesFormBuffer, 0, 24);
        }
        catch
        {
            return null;
        }

        var formType = tesFormBuffer[4];
        if (expectedFormType.HasValue)
        {
            if (formType != expectedFormType.Value)
            {
                return null;
            }
        }
        else if (formType > 200)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(tesFormBuffer, 12);
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        return formId;
    }

    /// <summary>
    ///     Follow a virtual address pointer to a TESForm and return its FormID.
    ///     Similar to FollowPointerToFormId but takes a VA directly (not buffer offset).
    /// </summary>
    public uint? FollowPointerVaToFormId(uint va)
    {
        return FollowPointerVaToFormIdCore(va, null);
    }

    /// <summary>
    ///     Follow a virtual address pointer to a TESForm and return its FormID if the
    ///     target matches the expected FormType.
    /// </summary>
    public uint? FollowPointerVaToFormId(uint va, byte expectedFormType)
    {
        return FollowPointerVaToFormIdCore(va, expectedFormType);
    }

    private uint? FollowPointerVaToFormIdCore(uint va, byte? expectedFormType)
    {
        if (va == 0)
        {
            return null;
        }

        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var formBuf = ReadBytes(fileOffset.Value, 16);
        if (formBuf == null)
        {
            return null;
        }

        var formType = formBuf[4];
        if (expectedFormType.HasValue)
        {
            if (formType != expectedFormType.Value)
            {
                return null;
            }
        }
        else if (formType > 200)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(formBuf, 12);
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        return formId;
    }

    /// <summary>
    ///     Read BSStringT header to extract the string file offset and VA.
    ///     Returns null if the BSStringT pointer is invalid or unmapped.
    /// </summary>
    public (long StringFileOffset, uint StringVa)? ReadBSStringTInfo(long tesFormFileOffset, int fieldOffset)
    {
        var bstOffset = tesFormFileOffset + fieldOffset;
        if (bstOffset + 8 > FileSize)
        {
            return null;
        }

        var bstBuffer = new byte[8];
        Accessor.ReadArray(bstOffset, bstBuffer, 0, 8);

        var pString = BinaryUtils.ReadUInt32BE(bstBuffer);
        var sLen = BinaryUtils.ReadUInt16BE(bstBuffer, 4);

        if (pString == 0 || sLen == 0 || sLen > EsmStringUtils.MaxBSStringLength || !IsValidPointer(pString))
        {
            return null;
        }

        var strFileOffset = MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(pString));
        if (!strFileOffset.HasValue || strFileOffset.Value + sLen > FileSize)
        {
            return null;
        }

        return (strFileOffset.Value, pString);
    }

    /// <summary>
    ///     Read a BSStringT string from a TESForm object.
    ///     BSStringT layout (8 bytes, big-endian):
    ///     Offset 0: pString (char* pointer, 4 bytes BE)
    ///     Offset 4: sLen (uint16 BE)
    /// </summary>
    public string? ReadBSStringT(long tesFormFileOffset, int fieldOffset)
    {
        return ReadBSStringTDiag(tesFormFileOffset, fieldOffset, out _);
    }

    /// <summary>
    ///     Read a BSStringT with diagnostic failure reason.
    /// </summary>
    public string? ReadBSStringTDiag(long tesFormFileOffset, int fieldOffset, out BSStringFailure failureReason)
    {
        return ReadBSStringTDiag(tesFormFileOffset, fieldOffset, out failureReason,
            out _, out _, out _, out _);
    }

    /// <summary>
    ///     Read a BSStringT with diagnostic failure reason and raw field values for sampling.
    /// </summary>
    public string? ReadBSStringTDiag(long tesFormFileOffset, int fieldOffset, out BSStringFailure failureReason,
        out uint rawPointer, out ushort rawLength, out string? rawHex, out string? partialData)
    {
        failureReason = BSStringFailure.None;
        rawPointer = 0;
        rawLength = 0;
        rawHex = null;
        partialData = null;

        var bstOffset = tesFormFileOffset + fieldOffset;
        if (bstOffset + 8 > FileSize)
        {
            failureReason = BSStringFailure.StructOutOfBounds;
            return null;
        }

        var bstBuffer = new byte[8];
        Accessor.ReadArray(bstOffset, bstBuffer, 0, 8);
        rawHex = Convert.ToHexString(bstBuffer);

        var pString = BinaryUtils.ReadUInt32BE(bstBuffer);
        var sLen = BinaryUtils.ReadUInt16BE(bstBuffer, 4);
        rawPointer = pString;
        rawLength = sLen;

        if (pString == 0)
        {
            failureReason = BSStringFailure.NullPointer;
            return null;
        }

        if (sLen == 0)
        {
            failureReason = BSStringFailure.ZeroLength;
            return null;
        }

        if (sLen > EsmStringUtils.MaxBSStringLength)
        {
            failureReason = BSStringFailure.LengthTooLarge;
            return null;
        }

        if (!IsValidPointer(pString))
        {
            failureReason = BSStringFailure.InvalidPointer;
            return null;
        }

        var strFileOffset = MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(pString));
        if (!strFileOffset.HasValue)
        {
            failureReason = BSStringFailure.VaNotMapped;
            return null;
        }

        if (strFileOffset.Value + sLen > FileSize)
        {
            failureReason = BSStringFailure.DataBeyondFile;
            return null;
        }

        var strBuffer = new byte[sLen];
        Accessor.ReadArray(strFileOffset.Value, strBuffer, 0, sLen);

        var result = EsmStringUtils.ValidateAndDecodeAscii(strBuffer, sLen);
        if (result == null)
        {
            failureReason = BSStringFailure.InvalidAscii;
            // Capture first 32 bytes as hex for diagnostics
            partialData = Convert.ToHexString(strBuffer, 0, Math.Min(strBuffer.Length, 32));
        }

        return result;
    }

    /// <summary>
    ///     Reasons a BSStringT read can fail.
    /// </summary>
    internal enum BSStringFailure
    {
        None,
        StructOutOfBounds,
        NullPointer,
        ZeroLength,
        LengthTooLarge,
        InvalidPointer,
        VaNotMapped,
        DataBeyondFile,
        InvalidAscii
    }
}
