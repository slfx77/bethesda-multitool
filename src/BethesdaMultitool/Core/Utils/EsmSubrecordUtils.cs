using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Utils;

/// <summary>
///     Utilities for iterating through ESM subrecords.
/// </summary>
public static class EsmSubrecordUtils
{
    /// <summary>
    ///     Subrecord header size: 4-byte signature + 2-byte length.
    /// </summary>
    public const int SubrecordHeaderSize = 6;

    /// <summary>
    ///     Iterates through subrecords in a record's data section.
    ///     Returns (signature, data offset, data length) for each subrecord.
    ///     Honors Bethesda extended-size (XXXX) subrecords: an <c>XXXX</c> subrecord carries the true
    ///     4-byte length of the FOLLOWING subrecord (whose own u16 length field is then 0), used when a
    ///     subrecord's payload exceeds 0xFFFF (e.g. NVNM navmesh geometry, OFST worldspace offset tables).
    ///     The XXXX marker itself is not yielded; the following subrecord is yielded with its true length.
    /// </summary>
    /// <param name="data">Record data buffer.</param>
    /// <param name="dataSize">Size of valid data in buffer.</param>
    /// <param name="bigEndian">True for Xbox 360 big-endian format.</param>
    public static IEnumerable<ParsedSubrecord> IterateSubrecords(byte[] data, int dataSize, bool bigEndian)
    {
        var offset = 0;
        uint? pendingExtendedSize = null;

        while (offset + SubrecordHeaderSize <= dataSize)
        {
            // Read subrecord signature (4 bytes), pooled — see InternSignature.
            var sig = InternSignature(data, offset, bigEndian);

            // Read subrecord size (2 bytes)
            var subSize = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));

            // XXXX carries the real size of the next subrecord; stash it and skip the marker itself.
            if (sig == "XXXX" && subSize == 4 && offset + SubrecordHeaderSize + 4 <= dataSize)
            {
                pendingExtendedSize = bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + SubrecordHeaderSize))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + SubrecordHeaderSize));
                offset += SubrecordHeaderSize + 4;
                continue;
            }

            // The following subrecord's own u16 length is 0 when an XXXX preceded it — use the stashed size.
            var actualSize = pendingExtendedSize ?? subSize;
            pendingExtendedSize = null;

            if ((long)offset + SubrecordHeaderSize + actualSize > dataSize)
            {
                yield break;
            }

            yield return new ParsedSubrecord(sig, offset + SubrecordHeaderSize, (int)actualSize);

            offset += SubrecordHeaderSize + (int)actualSize;
        }
    }

    /// <summary>
    ///     Returns a shared instance of the 4-character signature at <paramref name="offset" />,
    ///     via the process-wide <see cref="EsmSignaturePool" /> that record headers and GRUP labels
    ///     now share. Non-ASCII bytes (corrupt or misaligned data) fall back to a fresh string, so
    ///     nothing is silently normalized away and the pool cannot be filled by garbage.
    /// </summary>
    private static string InternSignature(byte[] data, int offset, bool bigEndian)
    {
        var b0 = data[offset];
        var b1 = data[offset + 1];
        var b2 = data[offset + 2];
        var b3 = data[offset + 3];
        if (bigEndian)
        {
            (b0, b3) = (b3, b0);
            (b1, b2) = (b2, b1);
        }

        return EsmSignaturePool.TryIntern(b0, b1, b2, b3, out var pooled)
            ? pooled
            : new string([(char)b0, (char)b1, (char)b2, (char)b3]);
    }


    /// <summary>
    ///     Read subrecord signature as a uint for fast comparison.
    /// </summary>
    public static uint ReadSignatureAsUInt32(ReadOnlySpan<byte> data, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    /// <summary>
    ///     Convert a 4-character signature string to uint32 for fast comparison.
    /// </summary>
    public static uint SignatureToUInt32(string sig)
    {
        if (sig.Length != 4)
        {
            throw new ArgumentException("Signature must be exactly 4 characters", nameof(sig));
        }

        return (uint)(sig[0] | (sig[1] << 8) | (sig[2] << 16) | (sig[3] << 24));
    }

    /// <summary>
    ///     Get subrecord length, trying both endianness.
    /// </summary>
    public static ushort GetSubrecordLength(byte[] data, int offset, int maxLen)
    {
        var lenLe = BinaryUtils.ReadUInt16LE(data, offset);
        var lenBe = BinaryUtils.ReadUInt16BE(data, offset);

        // Prefer LE if it's valid
        if (lenLe > 0 && lenLe <= maxLen)
        {
            return lenLe;
        }

        if (lenBe > 0 && lenBe <= maxLen)
        {
            return lenBe;
        }

        return 0;
    }

    /// <summary>
    ///     Get FormID, trying both endianness.
    /// </summary>
    public static uint GetFormId(byte[] data, int offset)
    {
        var formIdLe = BinaryUtils.ReadUInt32LE(data, offset);
        var formIdBe = BinaryUtils.ReadUInt32BE(data, offset);

        // Valid FormIDs have plugin index 0x00-0x0F
        if (formIdLe >> 24 <= 0x0F && formIdLe != 0)
        {
            return formIdLe;
        }

        if (formIdBe >> 24 <= 0x0F && formIdBe != 0)
        {
            return formIdBe;
        }

        return 0;
    }

    /// <summary>
    ///     Validate a FormID value.
    /// </summary>
    public static bool IsValidFormId(uint formId)
    {
        // FormID should not be 0 or 0xFFFFFFFF
        return formId != 0 && formId != 0xFFFFFFFF;
    }
}
