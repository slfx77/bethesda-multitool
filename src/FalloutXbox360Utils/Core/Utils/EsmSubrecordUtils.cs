using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Utils;

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
    /// </summary>
    /// <param name="data">Record data buffer.</param>
    /// <param name="dataSize">Size of valid data in buffer.</param>
    /// <param name="bigEndian">True for Xbox 360 big-endian format.</param>
    public static IEnumerable<ParsedSubrecord> IterateSubrecords(byte[] data, int dataSize, bool bigEndian)
    {
        var offset = 0;

        while (offset + SubrecordHeaderSize <= dataSize)
        {
            // Read subrecord signature (4 bytes)
            var sig = bigEndian
                ? new string([
                    (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]
                ])
                : Encoding.ASCII.GetString(data, offset, 4);

            // Read subrecord size (2 bytes)
            var subSize = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));

            if (offset + SubrecordHeaderSize + subSize > dataSize)
            {
                yield break;
            }

            yield return new ParsedSubrecord(sig, offset + SubrecordHeaderSize, subSize);

            offset += SubrecordHeaderSize + subSize;
        }
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
