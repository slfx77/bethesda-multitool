using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Utils;

/// <summary>
///     Subrecord structure parsed from ESM record data.
/// </summary>
/// <param name="Signature">4-character subrecord type signature (e.g., "EDID", "FULL").</param>
/// <param name="DataOffset">Byte offset to subrecord data (after 6-byte header).</param>
/// <param name="DataLength">Length of subrecord data in bytes.</param>
public readonly record struct ParsedSubrecord(string Signature, int DataOffset, int DataLength);

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
}
