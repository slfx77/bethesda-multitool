using System.Numerics;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;

/// <summary>
///     Reads common NiObjectNET and NiAVObject data used by the renderer.
/// </summary>
internal static class NifObjectBlockReader
{
    internal static Matrix4x4 ParseNiAVObjectTransform(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be))
        {
            return Matrix4x4.Identity;
        }

        if (bsVersion > 26)
        {
            if (pos + 4 > end)
            {
                return Matrix4x4.Identity;
            }

            pos += 4;
        }
        else
        {
            if (pos + 2 > end)
            {
                return Matrix4x4.Identity;
            }

            pos += 2;
        }

        if (pos + 12 + 36 + 4 > end)
        {
            return Matrix4x4.Identity;
        }

        var tx = BinaryUtils.ReadFloat(data, pos, be);
        var ty = BinaryUtils.ReadFloat(data, pos + 4, be);
        var tz = BinaryUtils.ReadFloat(data, pos + 8, be);
        pos += 12;

        var rotation = new float[9];
        for (var i = 0; i < rotation.Length; i++)
        {
            rotation[i] = BinaryUtils.ReadFloat(data, pos + i * 4, be);
        }

        pos += 36;
        var scale = BinaryUtils.ReadFloat(data, pos, be);
        return new Matrix4x4(
            rotation[0] * scale,
            rotation[3] * scale,
            rotation[6] * scale,
            0,
            rotation[1] * scale,
            rotation[4] * scale,
            rotation[7] * scale,
            0,
            rotation[2] * scale,
            rotation[5] * scale,
            rotation[8] * scale,
            0,
            tx,
            ty,
            tz,
            1);
    }

    internal static string? ReadBlockName(byte[] data, BlockInfo block, NifInfo nif)
    {
        if (block.Size < 4)
        {
            return null;
        }

        var nameIndex = BinaryUtils.ReadInt32(
            data,
            block.DataOffset,
            nif.IsBigEndian);
        return nameIndex < 0 || nameIndex >= nif.Strings.Count
            ? null
            : nif.Strings[nameIndex];
    }
}
