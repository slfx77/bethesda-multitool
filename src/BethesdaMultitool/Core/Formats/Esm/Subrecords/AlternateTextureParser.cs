using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Subrecords;

/// <summary>
///     Decodes a <c>MODS</c> ("Alternate Textures") subrecord payload into its
///     <see cref="AlternateTextureEntry" /> list. Wire format (xEdit
///     <c>
///         wbArrayS(MODS, …,
///         wbAlternateTexture, -1)
///     </c>
///     ): a <c>u32</c> count, then per entry a length-prefixed
///     3D name (<c>u32 length</c> + ASCII bytes), a <c>u32</c> TXST FormID, and an <c>s32</c>
///     3D index. Integers are read with the record's endianness (Xbox 360 ESM is big-endian).
///     <para>
///         Defensive against truncation: parsing stops early and returns whatever entries fully
///         decoded rather than throwing, matching the tolerant style of the typed handlers.
///     </para>
/// </summary>
public static class AlternateTextureParser
{
    /// <summary>
    ///     Parses a <c>MODS</c> payload. Returns an empty list for empty / malformed / count-zero data.
    /// </summary>
    public static List<AlternateTextureEntry> Parse(ReadOnlySpan<byte> mods, bool isBigEndian)
    {
        var result = new List<AlternateTextureEntry>();
        if (mods.Length < 4)
        {
            return result;
        }

        var count = ReadUInt32(mods, isBigEndian);
        var pos = 4;

        for (var i = 0; i < count; i++)
        {
            // Length-prefixed 3D name.
            if (pos + 4 > mods.Length)
            {
                break;
            }

            var nameLen = (int)ReadUInt32(mods[pos..], isBigEndian);
            pos += 4;

            // Guard against a corrupt length that would over-read the payload (or the trailing
            // FormID + index that must follow the name). Compared against remaining space rather than
            // summed, so a huge nameLen can't integer-overflow the bounds check.
            if (nameLen < 0 || nameLen > mods.Length - pos - 8)
            {
                break;
            }

            var name = Encoding.ASCII.GetString(mods.Slice(pos, nameLen)).TrimEnd('\0');
            pos += nameLen;

            var txstFormId = ReadUInt32(mods[pos..], isBigEndian);
            pos += 4;

            var index = unchecked((int)ReadUInt32(mods[pos..], isBigEndian));
            pos += 4;

            if (!string.IsNullOrEmpty(name))
            {
                result.Add(new AlternateTextureEntry(name, txstFormId, index));
            }
        }

        return result;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(span)
            : BinaryPrimitives.ReadUInt32LittleEndian(span);
    }
}
