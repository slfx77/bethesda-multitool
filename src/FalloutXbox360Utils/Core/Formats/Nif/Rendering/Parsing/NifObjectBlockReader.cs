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

    /// <summary>
    ///     Read the "Prn" (parent node) extra data from a NiNode/NiAVObject block.
    ///     Returns the bone name string (e.g., "Bip01 Spine2") if found, null otherwise.
    /// </summary>
    internal static string? ReadParentNodeExtraData(byte[] data, BlockInfo block, NifInfo nif)
    {
        return ReadStringExtraData(data, block, nif, "Prn");
    }

    /// <summary>
    ///     Read the "UPB" user property buffer string from a NiNode/NiAVObject block.
    /// </summary>
    internal static string? ReadUserPropertyBufferExtraData(byte[] data, BlockInfo block, NifInfo nif)
    {
        return ReadStringExtraData(data, block, nif, "UPB");
    }

    /// <summary>
    ///     Read attachment-bone metadata from a node. Most NIFs use "Prn"; some weapon
    ///     subtrees store the equivalent parent bone in the "UPB" string instead.
    /// </summary>
    internal static string? ReadAttachmentBoneExtraData(byte[] data, BlockInfo block, NifInfo nif)
    {
        var prn = ReadParentNodeExtraData(data, block, nif);
        if (!string.IsNullOrWhiteSpace(prn))
        {
            return prn;
        }

        var upb = ReadUserPropertyBufferExtraData(data, block, nif);
        return TryParseAttachmentBoneFromUserPropertyBuffer(upb);
    }

    private static string? ReadStringExtraData(
        byte[] data,
        BlockInfo block,
        NifInfo nif,
        string extraDataName)
    {
        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // NiObjectNET header: nameRef(4) + numExtraData(4) + extraDataRefs(N*4) + controllerRef(4)
        if (pos + 4 > end)
        {
            return null;
        }

        pos += 4;

        if (pos + 4 > end)
        {
            return null;
        }

        var numExtraData = (int)BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (numExtraData <= 0 || numExtraData > 100 || pos + numExtraData * 4 > end)
        {
            return null;
        }

        for (var i = 0; i < numExtraData; i++)
        {
            var edRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;

            if (edRef < 0 || edRef >= nif.Blocks.Count)
            {
                continue;
            }

            var edBlock = nif.Blocks[edRef];
            if (edBlock.TypeName != "NiStringExtraData" || edBlock.Size < 8)
            {
                continue;
            }

            var edNameIdx = BinaryUtils.ReadInt32(data, edBlock.DataOffset, be);
            if (edNameIdx < 0 || edNameIdx >= nif.Strings.Count)
            {
                continue;
            }

            if (!string.Equals(nif.Strings[edNameIdx], extraDataName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var valueIdx = BinaryUtils.ReadInt32(data, edBlock.DataOffset + 4, be);
            if (valueIdx >= 0 && valueIdx < nif.Strings.Count)
            {
                return nif.Strings[valueIdx];
            }
        }

        return null;
    }

    private static string? TryParseAttachmentBoneFromUserPropertyBuffer(string? userPropertyBuffer)
    {
        if (string.IsNullOrWhiteSpace(userPropertyBuffer))
        {
            return null;
        }

        var trimmed = userPropertyBuffer.Trim();
        if (trimmed.Length == 0 || trimmed.Contains('='))
        {
            return null;
        }

        return trimmed.StartsWith("Bip01", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "Weapon", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : null;
    }
}
