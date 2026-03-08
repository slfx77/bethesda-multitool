using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;

/// <summary>
///     Shared cursor helpers for common Gamebryo block field layouts.
/// </summary>
internal static class NifBinaryCursor
{
    /// <summary>
    ///     Skip past the NiObjectNET header fields: Name(4) + NumExtraData(4) + refs + Controller(4).
    ///     Advances <paramref name="pos" /> past the header and returns false if the block is too small.
    /// </summary>
    internal static bool SkipNiObjectNET(byte[] data, ref int pos, int end, bool be)
    {
        if (pos + 4 > end)
        {
            return false;
        }

        pos += 4;

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
