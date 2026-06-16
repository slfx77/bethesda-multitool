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
        // Skyrim and later (BS stream > 34) replaced the NiAVObject "Properties" array with two
        // dedicated NiGeometry refs: Shader Property + Alpha Property, located after the
        // Data ref, Skin Instance ref, and the variable-length Material Data block.
        if (bsVersion > 34)
        {
            return ParseNiGeometryBsPropertyRefs(data, block, bsVersion, be);
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

    /// <summary>
    ///     Read the Shader Property + Alpha Property refs from a Skyrim+ NiGeometry shape (e.g. the
    ///     NiTriShape used by Skyrim LE), which carry the BSLightingShaderProperty / NiAlphaProperty
    ///     in place of the old Properties array. Layout after the NiAVObject base: Data ref(4) +
    ///     Skin Instance ref(4) + MaterialData(variable) + Shader Property(4) + Alpha Property(4).
    /// </summary>
    private static List<int>? ParseNiGeometryBsPropertyRefs(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (!SkipNiGeometryHeader(data, ref pos, end, bsVersion, be))
        {
            return null;
        }

        pos += 4; // Data ref
        pos += 4; // Skin Instance ref

        if (!SkipMaterialData(data, ref pos, end, be) || pos + 8 > end)
        {
            return null;
        }

        var shaderRef = BinaryUtils.ReadInt32(data, pos, be);
        var alphaRef = BinaryUtils.ReadInt32(data, pos + 4, be);

        var refs = new List<int>(2);
        if (shaderRef >= 0 && shaderRef < int.MaxValue)
        {
            refs.Add(shaderRef);
        }

        if (alphaRef >= 0 && alphaRef < int.MaxValue)
        {
            refs.Add(alphaRef);
        }

        return refs.Count > 0 ? refs : null;
    }

    /// <summary>
    ///     Skip a NiGeometry MaterialData block (NIF 20.2.0.7 form): Num Materials(4) +
    ///     Material Names(NiFixedString index ×N) + Material Extra Data(int ×N) + Active Material(4)
    ///     + Material Needs Update(bool). Returns false if the block is too small/inconsistent.
    /// </summary>
    private static bool SkipMaterialData(byte[] data, ref int pos, int end, bool be)
    {
        if (pos + 4 > end)
        {
            return false;
        }

        var numMaterials = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numMaterials > 1000)
        {
            return false;
        }

        pos += (int)numMaterials * 4; // Material Name (NiFixedString string indices)
        pos += (int)numMaterials * 4; // Material Extra Data
        pos += 4; // Active Material
        pos += 1; // Material Needs Update
        return pos <= end;
    }

    /// <summary>
    ///     Header + buffer layout of a self-contained BSTriShape block (Skyrim SE / Fallout 4 /
    ///     Fallout 76). Unlike NiTriShape, the vertex and index buffers live in the shape block
    ///     itself rather than a separate NiTriShapeData block.
    /// </summary>
    internal readonly record struct BsTriShapeInfo(
        int SkinRef,
        int ShaderRef,
        int AlphaRef,
        ulong VertexDesc,
        int NumVertices,
        int NumTriangles,
        int VertexBufferOffset,
        int TriangleBufferOffset);

    /// <summary>
    ///     Parse a BSTriShape block's refs, vertex descriptor, counts, and buffer offsets. Returns
    ///     null if the block is too small or self-inconsistent. Only valid for bsVersion ≥ 100
    ///     (BSTriShape was introduced with Skyrim Special Edition).
    /// </summary>
    internal static BsTriShapeInfo? ParseBsTriShape(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // SkipNiGeometryHeader lands at the first NiAVObject-derived field. For bsVersion > 34 it
        // skips NiObjectNET + flags + transform + collision ref (no Properties array), which is
        // exactly the BSTriShape NiAVObject base — pos now points at the Bounding Sphere.
        if (!SkipNiGeometryHeader(data, ref pos, end, bsVersion, be))
        {
            return null;
        }

        pos += 16; // Bounding Sphere (NiBound: Center vec3 + Radius float). No Bounding Box pre-F76.

        // Skin / Shader Property / Alpha Property refs (3 × int32).
        if (pos + 12 + 8 > end)
        {
            return null;
        }

        var skinRef = BinaryUtils.ReadInt32(data, pos, be);
        var shaderRef = BinaryUtils.ReadInt32(data, pos + 4, be);
        var alphaRef = BinaryUtils.ReadInt32(data, pos + 8, be);
        pos += 12;

        var vertexDesc = BinaryUtils.ReadUInt64(data, pos, be);
        pos += 8;

        // Num Triangles widened to uint32 in Fallout 4 (bsVersion ≥ 130); ushort in Skyrim SE.
        int numTriangles;
        if (bsVersion >= 130)
        {
            if (pos + 4 > end)
            {
                return null;
            }

            numTriangles = (int)BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
        }
        else
        {
            if (pos + 2 > end)
            {
                return null;
            }

            numTriangles = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        if (pos + 6 > end)
        {
            return null;
        }

        var numVertices = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        pos += 4; // Data Size (recomputable from vertexDesc/counts; not needed)

        var vertexSize = (int)(vertexDesc & 0xF) * 4;
        var vertexBufferOffset = pos;
        var triangleBufferOffset = vertexBufferOffset + numVertices * vertexSize;

        if (vertexSize == 0 ||
            triangleBufferOffset + numTriangles * 6 > end)
        {
            return null;
        }

        return new BsTriShapeInfo(
            skinRef,
            shaderRef,
            alphaRef,
            vertexDesc,
            numVertices,
            numTriangles,
            vertexBufferOffset,
            triangleBufferOffset);
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
