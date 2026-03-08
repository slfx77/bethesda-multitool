using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;

/// <summary>
///     Reads scene-graph and shape linkage fields from common NIF block types.
/// </summary>
internal static class NifSceneGraphBlockReader
{
    internal static int ParseShapeSkinInstanceRef(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!SkipNiGeometryHeader(
                data,
                ref pos,
                end,
                bsVersion,
                be))
        {
            return -1;
        }

        if (pos + 4 > end)
        {
            return -1;
        }

        pos += 4;

        if (pos + 4 > end)
        {
            return -1;
        }

        return BinaryUtils.ReadInt32(data, pos, be);
    }

    internal static int[]? ParseDismemberPartitions(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (pos + 12 > end)
        {
            return null;
        }

        pos += 12;

        if (pos + 4 > end)
        {
            return null;
        }

        var numBones = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numBones, 500) * 4;

        if (pos + 4 > end)
        {
            return null;
        }

        var numPartitions = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numPartitions == 0 || numPartitions > 100 || pos + numPartitions * 4 > end)
        {
            return null;
        }

        var bodyParts = new int[(int)numPartitions];
        for (var i = 0; i < numPartitions; i++)
        {
            pos += 2;
            bodyParts[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        return bodyParts;
    }

    internal static bool IsDismemberGoreShape(int[]? bodyParts)
    {
        if (bodyParts == null || bodyParts.Length == 0)
        {
            return false;
        }

        foreach (var bodyPart in bodyParts)
        {
            if (bodyPart >= 100 && bodyPart <= 299)
            {
                return true;
            }
        }

        return false;
    }

    internal static int ParseGeometryAdditionalDataRef(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos += 4;

        if (pos + 2 > end)
        {
            return -1;
        }

        var numVertices = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;

        if (bsVersion >= 34)
        {
            pos += 2;
        }

        if (pos + 1 > end)
        {
            return -1;
        }

        if (data[pos++] != 0)
        {
            pos += numVertices * 12;
        }

        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end)
            {
                return -1;
            }

            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        if (pos + 1 > end)
        {
            return -1;
        }

        if (data[pos++] != 0)
        {
            pos += numVertices * 12;
            if (bsVersion >= 34)
            {
                pos += 16;
                if ((bsVectorFlags & 0x1000) != 0)
                {
                    pos += numVertices * 24;
                }
            }
        }
        else if (bsVersion >= 34)
        {
            pos += 16;
        }

        if (pos + 1 > end)
        {
            return -1;
        }

        if (data[pos++] != 0)
        {
            pos += numVertices * 16;
        }

        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            pos += numVertices * numUvSets * 8;
        }
        else
        {
            if (pos + 4 > end)
            {
                return -1;
            }

            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4;
            pos += numVertices * numUvSets * 8;
        }

        pos += 2;
        if (pos + 4 > end)
        {
            return -1;
        }

        return BinaryUtils.ReadInt32(data, pos, be);
    }

    internal static int ReadVertexCount(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset + 4;
        return pos + 2 > block.DataOffset + block.Size
            ? -1
            : BinaryUtils.ReadUInt16(data, pos, be);
    }

    internal static List<int>? ParseNodeChildren(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!SkipNiGeometryHeader(
                data,
                ref pos,
                end,
                bsVersion,
                be))
        {
            return null;
        }

        if (pos + 4 > end)
        {
            return null;
        }

        var numChildren = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numChildren > 500 || pos + numChildren * 4 > end)
        {
            return null;
        }

        var children = new List<int>((int)numChildren);
        for (var i = 0; i < numChildren; i++)
        {
            var childRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
            if (childRef >= 0)
            {
                children.Add(childRef);
            }
        }

        return children;
    }

    internal static int ParseShapeDataRef(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!SkipNiGeometryHeader(
                data,
                ref pos,
                end,
                bsVersion,
                be) ||
            pos + 4 > end)
        {
            return -1;
        }

        return BinaryUtils.ReadInt32(data, pos, be);
    }

    internal static List<int>? ParseShapePropertyRefs(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be)
    {
        if (bsVersion > 34)
        {
            return null;
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be))
        {
            return null;
        }

        pos += bsVersion > 26 ? 4 : 2;
        pos += 12 + 36 + 4;

        if (pos + 4 > end)
        {
            return null;
        }

        var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numProperties == 0 || numProperties > 100 || pos + numProperties * 4 > end)
        {
            return null;
        }

        var refs = new List<int>((int)numProperties);
        for (var i = 0; i < numProperties; i++)
        {
            var propRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
            if (propRef >= 0)
            {
                refs.Add(propRef);
            }
        }

        return refs;
    }

    private static bool SkipNiGeometryHeader(
        byte[] data,
        ref int pos,
        int end,
        uint bsVersion,
        bool be)
    {
        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be))
        {
            return false;
        }

        pos += bsVersion > 26 ? 4 : 2;
        pos += 12 + 36 + 4;

        if (bsVersion <= 34)
        {
            if (pos + 4 > end)
            {
                return false;
            }

            var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += (int)Math.Min(numProperties, 100) * 4;
        }

        if (pos + 4 > end)
        {
            return false;
        }

        pos += 4;
        return pos <= end;
    }
}
