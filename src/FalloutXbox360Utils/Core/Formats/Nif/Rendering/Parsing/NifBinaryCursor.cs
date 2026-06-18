using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;

/// <summary>
///     Shared cursor helpers for common Gamebryo block field layouts.
/// </summary>
internal static class NifBinaryCursor
{
    /// <summary>
    ///     Skip a NiObjectNET.Name field. Modern NIFs (string table, ≥20.1.0.1) store it as a 4-byte
    ///     index; older ones (Oblivion 20.0.0.x, Morrowind) store it inline as a <c>SizedString</c>
    ///     (uint32 length + ASCII). Set <paramref name="hasInlineStrings" /> for the latter.
    /// </summary>
    internal static bool SkipName(byte[] data, ref int pos, int end, bool be, bool hasInlineStrings)
    {
        if (!hasInlineStrings)
        {
            if (pos + 4 > end)
            {
                return false;
            }

            pos += 4;
            return true;
        }

        if (pos + 4 > end)
        {
            return false;
        }

        var len = (int)BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (len < 0 || pos + len > end)
        {
            return false;
        }

        pos += len;
        return true;
    }

    /// <summary>
    ///     Skip past the NiObjectNET header fields: Name + NumExtraData(4) + refs + Controller(4).
    ///     Advances <paramref name="pos" /> past the header and returns false if the block is too small.
    ///     <paramref name="hasInlineStrings" /> selects the inline-vs-index Name encoding.
    ///     <para>
    ///         <paramref name="morrowind" />: the legacy NiObjectNET form (3.0..4.2.2.0) has a single
    ///         Extra Data ref instead of the Num Extra Data List + refs added at 10.0.1.0, so the header
    ///         is just Name + Extra Data ref(4) + Controller(4).
    ///     </para>
    /// </summary>
    internal static bool SkipNiObjectNET(byte[] data, ref int pos, int end, bool be,
        bool hasInlineStrings = false, bool morrowind = false)
    {
        if (!SkipName(data, ref pos, end, be, hasInlineStrings))
        {
            return false;
        }

        if (morrowind)
        {
            // Extra Data ref(4) + Controller ref(4) — no Num Extra Data List.
            if (pos + 8 > end)
            {
                return false;
            }

            pos += 8;
            return pos <= end;
        }

        if (pos + 4 > end)
        {
            return false;
        }

        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;

        if (pos + 4 > end)
        {
            return false;
        }

        pos += 4;
        return pos <= end;
    }

    /// <summary>
    ///     Read the controller ref from a NiObjectNET header without advancing past it.
    ///     Returns the block index of the first controller, or -1 if none/invalid.
    /// </summary>
    internal static int ReadNiObjectNETControllerRef(byte[] data, int dataOffset, int end, bool be,
        bool hasInlineStrings = false, bool morrowind = false)
    {
        var pos = dataOffset;

        // Name (4-byte string-table index, or inline SizedString in older NIFs)
        if (!SkipName(data, ref pos, end, be, hasInlineStrings))
        {
            return -1;
        }

        if (morrowind)
        {
            // Single Extra Data ref(4), then the Controller ref.
            pos += 4;
            return pos + 4 > end ? -1 : BinaryUtils.ReadInt32(data, pos, be);
        }

        // NumExtraData + refs
        if (pos + 4 > end)
        {
            return -1;
        }

        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;

        // Controller ref
        if (pos + 4 > end)
        {
            return -1;
        }

        return BinaryUtils.ReadInt32(data, pos, be);
    }

    /// <summary>
    ///     Read a NIF SizedString (uint32 length + ASCII payload) at the current cursor position.
    /// </summary>
    internal static string? ReadSizedString(byte[] data, ref int pos, int end, bool be)
    {
        if (pos + 4 > end)
        {
            return null;
        }

        var strLen = (int)BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (strLen <= 0 || strLen > 512 || pos + strLen > end)
        {
            pos += Math.Max(0, strLen);
            return null;
        }

        var result = Encoding.ASCII.GetString(data, pos, strLen);
        pos += strLen;

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
