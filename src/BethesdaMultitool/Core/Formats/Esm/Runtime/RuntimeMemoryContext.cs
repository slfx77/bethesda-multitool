using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

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
    /// <summary>
    ///     Maximum number of logical list nodes to visit. The inline head consumes one slot,
    ///     even when its item pointer is null.
    /// </summary>
    public const int MaxListItems = 50;

    public IMemoryAccessor Accessor { get; } = accessor;
    public long FileSize { get; } = fileSize;
    public MinidumpInfo MinidumpInfo { get; } = minidumpInfo;

    /// <summary>
    ///     FormID → enumerated runtime entry (editor id, form type, base offset). Populated
    ///     by
    ///     <see
    ///         cref="RuntimeStructReader.CreateWithAutoDetect(IMemoryAccessor,long,MinidumpInfo,System.Collections.Generic.IReadOnlyList{RuntimeEditorIdEntry},System.Collections.Generic.IReadOnlyList{RuntimeEditorIdEntry},System.Collections.Generic.IReadOnlyList{RuntimeEditorIdEntry},System.Collections.Generic.IReadOnlyList{RuntimeEditorIdEntry},System.Collections.Generic.IReadOnlyList{RuntimeEditorIdEntry},System.Collections.Generic.IReadOnlyList{RuntimeEditorIdEntry})" />
    ///     when an <c>allEntries</c> list is available. The QUST script scan uses this to
    ///     resolve candidate Script* pointers to EditorIds before validating via the
    ///     Script.pOwnerQuest backpointer; other specialized readers may use it for
    ///     similar resolve-then-validate flows.
    ///     Null in test fixtures and other lightweight construction paths; consumers must
    ///     gracefully degrade (typically: skip the probe, return null, and let downstream
    ///     editor-id-suffix heuristics handle the missing value).
    /// </summary>
    public IReadOnlyDictionary<uint, RuntimeEditorIdEntry>? EditorIdsByFormId { get; internal set; }

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
    ///     Historical helper name for the broad runtime-float check: accepts every finite value,
    ///     including zero and subnormals, and rejects only NaN/infinity.
    /// </summary>
    public static bool IsNormalFloat(float value)
    {
        return float.IsFinite(value);
    }

    /// <summary>
    ///     Accepts IEEE-normal values and exact zero. Use this stricter plausibility check only
    ///     where a subnormal is evidence of a misaligned pointer/field read.
    /// </summary>
    public static bool IsNormalOrZeroFloat(float value)
    {
        var magnitudeBits = BitConverter.SingleToUInt32Bits(value) & 0x7FFF_FFFFu;
        return magnitudeBits == 0 || float.IsNormal(value);
    }

    /// <summary>
    ///     Read a byte array from the dump file at a given file offset.
    ///     Returns null if the read fails.
    /// </summary>
    public byte[]? ReadBytes(long fileOffset, int count)
    {
        if (fileOffset < 0 || count < 0 || count > FileSize || fileOffset > FileSize - count)
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
    ///     Read bytes starting at the <c>TESForm</c> subobject retained on a runtime entry.
    ///     A retained file offset is required for provenance and fallback. The entry's captured
    ///     pointer is authoritative; when it is unavailable, recover the equivalent VA from that
    ///     offset. A mapped entry is always read in VA space so a struct spanning VA-adjacent
    ///     regions is reassembled from each region's own file offset. Flat reads are reserved for
    ///     lightweight synthetic contexts that have no region map.
    /// </summary>
    public byte[]? ReadTesFormBytes(RuntimeEditorIdEntry entry, int count)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (count < 0 || !entry.TesFormOffset.HasValue)
        {
            return null;
        }

        var tesFormVa = entry.TesFormPointer is { } pointer && pointer != 0
            ? pointer
            : MinidumpInfo.FileOffsetToVirtualAddress(entry.TesFormOffset.Value);
        if (tesFormVa.HasValue)
        {
            return ReadBytesAtVa(tesFormVa.Value, count);
        }

        return MinidumpInfo.MemoryRegions.Count == 0
            ? ReadBytes(entry.TesFormOffset.Value, count)
            : null;
    }

    /// <summary>
    ///     Read a byte array from the dump at a given virtual address, validating that the
    ///     entire VA range [va, va+count) falls within captured memory regions. This prevents
    ///     reading garbage data when a struct spans a gap between non-contiguous memory regions.
    ///     Returns null if the VA range is not fully captured or the read fails.
    /// </summary>
    public byte[]? ReadBytesAtVa(long va, int count)
    {
        if (count < 0)
        {
            return null;
        }

        if (count == 0)
        {
            return [];
        }

        // Validate before allocating. Besides failing closed across capture gaps, this keeps a
        // malformed or adversarially large request from allocating a result that can never be read.
        if (va > long.MaxValue - count || !MinidumpInfo.IsVaRangeCaptured(va, count))
        {
            return null;
        }

        var result = new byte[count];
        return ReadBytesAtVaInto(va, result, 0, count) ? result : null;
    }

    /// <summary>
    ///     Read from a raw 32-bit Xbox 360 pointer. Module-space addresses must be sign-extended
    ///     before lookup because minidump descriptors store addresses such as 0x82XXXXXX as Int64.
    /// </summary>
    public byte[]? ReadBytesAtVa(uint va, int count)
    {
        return ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(va), count);
    }

    /// <summary>
    ///     Same contract as <see cref="ReadBytesAtVa(long, int)" />, but copies into a caller-owned buffer so
    ///     scanning loops can reuse one allocation. Returns false — leaving the buffer untouched
    ///     past whatever it managed to copy — when the range is not fully captured or a read fails.
    /// </summary>
    public bool ReadBytesAtVaInto(long va, byte[] target, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (count < 0 || offset < 0 || offset > target.Length - count)
        {
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        if (va > long.MaxValue - count)
        {
            return false;
        }

        var endVa = va + count;
        if (!MinidumpInfo.IsVaRangeCaptured(va, count))
        {
            return false;
        }

        var copied = 0;
        var currentVa = va;

        try
        {
            foreach (var region in MinidumpInfo.GetRegionsInRange(va, endVa))
            {
                if (copied == count)
                {
                    break;
                }

                // IsVaRangeCaptured guarantees VA-contiguous regions. Keep the check here so
                // this copy loop remains fail-closed if the region index ever changes.
                if (currentVa < region.VirtualAddress ||
                    currentVa >= region.VirtualAddress + region.Size)
                {
                    return false;
                }

                var available = region.VirtualAddress + region.Size - currentVa;
                var chunkSize = checked((int)Math.Min(available, count - copied));
                var fileOffset = region.FileOffset + (currentVa - region.VirtualAddress);
                if (fileOffset < 0 || fileOffset > FileSize - chunkSize)
                {
                    return false;
                }

                var bytesRead = Accessor.ReadArray(fileOffset, target, offset + copied, chunkSize);
                if (bytesRead != chunkSize)
                {
                    return false;
                }

                copied += chunkSize;
                currentVa += chunkSize;
            }
        }
        catch
        {
            return false;
        }

        return copied == count;
    }

    /// <summary>
    ///     How many bytes starting at <paramref name="va" /> are captured as one VA-contiguous run,
    ///     capped at <paramref name="max" />. Lets a caller that wants "as much as is there" — a
    ///     null-terminated string, a variable-length scan — shrink its request instead of failing
    ///     the whole read the way <see cref="ReadBytesAtVa(long, int)" /> does.
    /// </summary>
    public int GetCapturedVaRunLength(long va, int max)
    {
        if (max <= 0 || va > long.MaxValue - max)
        {
            return 0;
        }

        var run = 0L;
        var currentVa = va;
        foreach (var region in MinidumpInfo.GetRegionsInRange(va, va + max))
        {
            if (currentVa < region.VirtualAddress || currentVa >= region.VirtualAddress + region.Size)
            {
                break; // VA gap (or a region starting past our cursor) — the run ends here.
            }

            var regionEnd = region.VirtualAddress + region.Size;
            run += regionEnd - currentVa;
            currentVa = regionEnd;
            if (run >= max)
            {
                return max;
            }
        }

        return (int)Math.Min(run, max);
    }

    /// <summary>
    ///     Read a null-terminated printable ASCII string from a runtime char pointer.
    /// </summary>
    public string? ReadNullTerminatedAsciiString(uint ptr, int maxBytes = 256)
    {
        if (ptr == 0 || maxBytes <= 0 || !IsValidPointer(ptr))
        {
            return null;
        }

        // VA-based, and clamped to the captured run rather than fail-closed: this is a
        // null-terminated read, so a string that ends before the region does is a complete
        // success. A flat file read here used to run past the region boundary and pick up
        // printable bytes from an unrelated allocation, producing a real-looking wrong string.
        var va = Xbox360MemoryUtils.VaToLong(ptr);
        var available = GetCapturedVaRunLength(va, maxBytes);
        if (available <= 0)
        {
            return null;
        }

        var buffer = ReadBytesAtVa(va, available);
        if (buffer == null)
        {
            return null;
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == 0)
            {
                return i == 0 ? null : Encoding.ASCII.GetString(buffer, 0, i);
            }

            if (buffer[i] < 32 || buffer[i] > 126)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    ///     Walk an inline BSSimpleList where the struct stores the first item pointer
    ///     at <paramref name="listOffset" /> and the first heap node pointer at +4.
    ///     Heap nodes are 8 bytes: item pointer, next node pointer. The traversal budget
    ///     counts the inline head and every successfully read heap node, including nodes
    ///     whose item pointer is null.
    /// </summary>
    public IEnumerable<uint> WalkInlineBSSimpleListItemPointers(
        byte[] structBuffer,
        int listOffset,
        int maxItems = MaxListItems)
    {
        ArgumentNullException.ThrowIfNull(structBuffer);
        if (listOffset < 0 || listOffset > structBuffer.Length - 8 || maxItems <= 0)
        {
            return [];
        }

        return WalkInlineBSSimpleListItemPointersCore(structBuffer, listOffset, maxItems);
    }

    private IEnumerable<uint> WalkInlineBSSimpleListItemPointersCore(
        byte[] structBuffer,
        int listOffset,
        int maxItems)
    {
        var itemPtr = BinaryUtils.ReadUInt32BE(structBuffer, listOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(structBuffer, listOffset + 4);
        if (itemPtr != 0)
        {
            yield return itemPtr;
        }

        var visited = new HashSet<uint>();
        var nodeCount = 1;
        while (nextPtr != 0 &&
               nodeCount < maxItems &&
               IsValidPointer(nextPtr) &&
               visited.Add(nextPtr))
        {
            var nodeBuffer = ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nextPtr), 8);
            if (nodeBuffer == null)
            {
                yield break;
            }

            nodeCount++;
            itemPtr = BinaryUtils.ReadUInt32BE(nodeBuffer);
            nextPtr = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);
            if (itemPtr != 0)
            {
                yield return itemPtr;
            }
        }
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
        // Reject NaN/Inf, out-of-range, AND subnormals. A subnormal (|value| < ~1.2e-38, e.g. ~1e-40)
        // is never a legitimate game float — it is the signature of a misread, typically a pointer's
        // low bytes decoded as a float when a struct offset is wrong for the captured build. Exact zero
        // stays valid (IsSubnormal(0) is false).
        if (!IsNormalOrZeroFloat(value) || value < min || value > max)
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
        return FollowPointerToFormIdCore(buffer, pointerOffset, formType => formType == expectedFormType);
    }

    /// <summary>
    ///     Follow a pointer to a TESForm and accept any of <paramref name="acceptableFormTypes" />.
    ///     For a pointer declared as a C++ base class, the accepted set is that class plus every
    ///     record class deriving from it — a single-FormType demand would reject the derived
    ///     instance the field normally holds.
    /// </summary>
    public uint? FollowPointerToFormId(byte[] buffer, int pointerOffset, IReadOnlySet<byte> acceptableFormTypes)
    {
        ArgumentNullException.ThrowIfNull(acceptableFormTypes);

        return acceptableFormTypes.Count == 0
            ? null
            : FollowPointerToFormIdCore(buffer, pointerOffset, acceptableFormTypes.Contains);
    }

    private uint? FollowPointerToFormIdCore(byte[] buffer, int pointerOffset, Func<byte, bool>? isAcceptableFormType)
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

        var tesFormBuffer = ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(pointer), 24);
        if (tesFormBuffer == null)
        {
            return null;
        }

        var formType = tesFormBuffer[4];
        if (isAcceptableFormType != null)
        {
            if (!isAcceptableFormType(formType))
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

        var formBuf = ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(va), 16);
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
    ///     When the containing struct's file offset maps to a captured VA, the member offset is
    ///     applied in VA space and the full header fails closed across capture gaps. Flat header
    ///     reads are attempted only by lightweight synthetic contexts with no memory-region map.
    /// </summary>
    public (long StringFileOffset, uint StringVa)? ReadBSStringTInfo(long tesFormFileOffset, int fieldOffset)
    {
        if (fieldOffset < 0 || tesFormFileOffset < 0)
        {
            return null;
        }

        var tesFormVa = MinidumpInfo.FileOffsetToVirtualAddress(tesFormFileOffset);
        byte[]? bstBuffer;
        if (tesFormVa.HasValue)
        {
            if (tesFormVa.Value > long.MaxValue - fieldOffset)
            {
                return null;
            }

            // Add the member offset in VA space. Adding it to the starting file offset is wrong
            // when the struct crosses VA-adjacent regions stored at disjoint dump offsets.
            bstBuffer = ReadBytesAtVa(tesFormVa.Value + fieldOffset, 8);
        }
        else if (MinidumpInfo.MemoryRegions.Count == 0)
        {
            if (tesFormFileOffset > long.MaxValue - fieldOffset)
            {
                return null;
            }

            var bstOffset = tesFormFileOffset + fieldOffset;
            bstBuffer = ReadBytes(bstOffset, 8);
        }
        else
        {
            return null;
        }

        return bstBuffer == null ? null : ReadBSStringTInfoHeader(bstBuffer);
    }

    /// <summary>
    ///     Read BSStringT ownership metadata from a complete-object buffer that was already
    ///     captured safely. The pointed-to payload is still validated through its VA.
    /// </summary>
    public (long StringFileOffset, uint StringVa)? ReadBSStringTInfo(byte[] structData, int fieldOffset)
    {
        ArgumentNullException.ThrowIfNull(structData);
        if (fieldOffset < 0 || fieldOffset > structData.Length - 8)
        {
            return null;
        }

        return ReadBSStringTInfoHeader(structData.AsSpan(fieldOffset, 8));
    }

    private (long StringFileOffset, uint StringVa)? ReadBSStringTInfoHeader(ReadOnlySpan<byte> bstBuffer)
    {
        var pString = BinaryUtils.ReadUInt32BE(bstBuffer);
        var sLen = BinaryUtils.ReadUInt16BE(bstBuffer, 4);

        if (pString == 0 || sLen == 0 || sLen > EsmStringUtils.MaxBSStringLength || !IsValidPointer(pString))
        {
            return null;
        }

        var strFileOffset = MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(pString));
        if (!strFileOffset.HasValue || ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(pString), sLen) == null)
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
    public string? ReadBsStringT(long tesFormFileOffset, int fieldOffset)
    {
        return ReadBSStringTDiag(tesFormFileOffset, fieldOffset, out _);
    }

    /// <summary>
    ///     Read a BSStringT whose header is already present in a safely captured struct buffer.
    /// </summary>
    public string? ReadBsStringT(byte[] structData, int fieldOffset)
    {
        return ReadBSStringTDiag(structData, fieldOffset, out _);
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
        if (fieldOffset < 0 || tesFormFileOffset < 0)
        {
            failureReason = BSStringFailure.StructOutOfBounds;
            rawPointer = 0;
            rawLength = 0;
            rawHex = null;
            partialData = null;
            return null;
        }

        var tesFormVa = MinidumpInfo.FileOffsetToVirtualAddress(tesFormFileOffset);
        byte[]? bstBuffer;
        if (tesFormVa.HasValue)
        {
            if (tesFormVa.Value > long.MaxValue - fieldOffset)
            {
                bstBuffer = null;
            }
            else
            {
                // The relative member offset belongs to the virtual object, not to its first
                // region's file location. Resolve the header after adding in VA space.
                bstBuffer = ReadBytesAtVa(tesFormVa.Value + fieldOffset, 8);
            }
        }
        else if (MinidumpInfo.MemoryRegions.Count == 0 && tesFormFileOffset <= long.MaxValue - fieldOffset)
        {
            bstBuffer = ReadBytes(tesFormFileOffset + fieldOffset, 8);
        }
        else
        {
            bstBuffer = null;
        }

        if (bstBuffer == null)
        {
            failureReason = BSStringFailure.StructOutOfBounds;
            rawPointer = 0;
            rawLength = 0;
            rawHex = null;
            partialData = null;
            return null;
        }

        return DecodeBSStringTHeader(bstBuffer, out failureReason,
            out rawPointer, out rawLength, out rawHex, out partialData);
    }

    /// <summary>
    ///     Read a BSStringT whose 8-byte header is already present in a VA-safe struct buffer.
    ///     The pointed-to string payload is still resolved through its virtual address.
    /// </summary>
    public string? ReadBSStringTDiag(byte[] structData, int fieldOffset, out BSStringFailure failureReason)
    {
        return ReadBSStringTDiag(structData, fieldOffset, out failureReason,
            out _, out _, out _, out _);
    }

    /// <summary>
    ///     Read a BSStringT from a captured struct buffer with diagnostic raw values for sampling.
    /// </summary>
    public string? ReadBSStringTDiag(byte[] structData, int fieldOffset, out BSStringFailure failureReason,
        out uint rawPointer, out ushort rawLength, out string? rawHex, out string? partialData)
    {
        ArgumentNullException.ThrowIfNull(structData);
        if (fieldOffset < 0 || fieldOffset > structData.Length - 8)
        {
            failureReason = BSStringFailure.StructOutOfBounds;
            rawPointer = 0;
            rawLength = 0;
            rawHex = null;
            partialData = null;
            return null;
        }

        var bstBuffer = structData.AsSpan(fieldOffset, 8).ToArray();
        return DecodeBSStringTHeader(bstBuffer, out failureReason,
            out rawPointer, out rawLength, out rawHex, out partialData);
    }

    private string? DecodeBSStringTHeader(byte[] bstBuffer, out BSStringFailure failureReason,
        out uint rawPointer, out ushort rawLength, out string? rawHex, out string? partialData)
    {
        failureReason = BSStringFailure.None;
        rawPointer = 0;
        rawLength = 0;
        rawHex = Convert.ToHexString(bstBuffer);
        partialData = null;

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

        var strBuffer = ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(pString), sLen);
        if (strBuffer == null)
        {
            failureReason = BSStringFailure.DataBeyondFile;
            return null;
        }

        var result = EsmStringUtils.ValidateAndDecodeGameText(strBuffer, sLen);
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
