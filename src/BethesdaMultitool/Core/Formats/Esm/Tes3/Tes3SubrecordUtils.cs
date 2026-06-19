using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Tes3;

/// <summary>
///     Iterates subrecords inside a Morrowind (TES3) record's data section. TES3 differs from
///     TES4+ in two ways that matter here: the subrecord size field is <b>4 bytes</b> (vs 2 in
///     TES4), and the stream is always little-endian (Morrowind shipped on PC only). This is the
///     single chokepoint for the TES3 framing difference — everything downstream reuses the same
///     <see cref="ParsedSubrecord" /> shape as the TES4 path.
/// </summary>
public static class Tes3SubrecordUtils
{
    /// <summary>Subrecord header size: 4-byte signature + 4-byte length.</summary>
    public const int SubrecordHeaderSize = 8;

    /// <summary>
    ///     Iterates through subrecords in a TES3 record's data section, yielding
    ///     (signature, data offset, data length) for each. Stops cleanly on a truncated trailer.
    /// </summary>
    public static IEnumerable<ParsedSubrecord> IterateSubrecords(byte[] data, int dataSize)
    {
        var offset = 0;

        while (offset + SubrecordHeaderSize <= dataSize)
        {
            var sig = Encoding.ASCII.GetString(data, offset, 4);
            var subSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));

            if (subSize < 0 || offset + SubrecordHeaderSize + subSize > dataSize)
            {
                yield break;
            }

            yield return new ParsedSubrecord(sig, offset + SubrecordHeaderSize, subSize);

            offset += SubrecordHeaderSize + subSize;
        }
    }
}
