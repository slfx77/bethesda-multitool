using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Low-level NIF block parsing utilities: header skipping, transform extraction, shape/node
///     field parsing, geometry data extraction, and vertex transform helpers.
/// </summary>
internal static class NifBlockParsers
{
    /// <summary>
    ///     Skip past the NiObjectNET header fields: Name(4) + NumExtraData(4) + refs + Controller(4).
    ///     Advances <paramref name="pos" /> past the header.  Returns false if the block is too small.
    /// </summary>
    internal static bool SkipNiObjectNET(byte[] data, ref int pos, int end, bool be)
    {
        // Name (string index, int32)
        if (pos + 4 > end) return false;
        pos += 4;

        // NumExtraData (uint32) + refs
        if (pos + 4 > end) return false;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;

        // Controller ref (int32)
        if (pos + 4 > end) return false;
        pos += 4;

        return pos <= end;
    }

    internal static Matrix4x4 ParseNiAVObjectTransform(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // NiObjectNET: Name (4) + NumExtraData (4) + ExtraData refs + Controller (4)
        if (pos + 4 > end) return Matrix4x4.Identity;
        pos += 4; // Name index

        if (pos + 4 > end) return Matrix4x4.Identity;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4; // Extra data refs

        if (pos + 4 > end) return Matrix4x4.Identity;
        pos += 4; // Controller ref

        // NiAVObject: Flags
        if (bsVersion > 26)
        {
            if (pos + 4 > end) return Matrix4x4.Identity;
            pos += 4; // uint flags
        }
        else
        {
            if (pos + 2 > end) return Matrix4x4.Identity;
            pos += 2; // ushort flags
        }

        // Translation (3 floats = 12 bytes)
        if (pos + 12 > end) return Matrix4x4.Identity;
        var tx = BinaryUtils.ReadFloat(data, pos, be);
        var ty = BinaryUtils.ReadFloat(data, pos + 4, be);
        var tz = BinaryUtils.ReadFloat(data, pos + 8, be);
        pos += 12;

        // Rotation (3x3 matrix = 36 bytes)
        if (pos + 36 > end) return Matrix4x4.Identity;
        var m = new float[9];
        for (var i = 0; i < 9; i++)
        {
            m[i] = BinaryUtils.ReadFloat(data, pos + i * 4, be);
        }

        pos += 36;

        // Scale (1 float = 4 bytes)
        if (pos + 4 > end) return Matrix4x4.Identity;
        var scale = BinaryUtils.ReadFloat(data, pos, be);

        // Build 4x4 matrix: transposed rotation * scale + translation.
        // NIF stores column-vector rotation (M*v convention) in row-major order.
        // System.Numerics uses row-vector (v*M), so we transpose the 3x3 block.
        return new Matrix4x4(
            m[0] * scale, m[3] * scale, m[6] * scale, 0,
            m[1] * scale, m[4] * scale, m[7] * scale, 0,
            m[2] * scale, m[5] * scale, m[8] * scale, 0,
            tx, ty, tz, 1);
    }

    /// <summary>
    ///     Read the name string from a block's NiObjectNET header (first int32 = string index).
    /// </summary>
    internal static string? ReadBlockName(byte[] data, BlockInfo block, NifInfo nif)
    {
        if (block.Size < 4) return null;
        var nameIdx = BinaryUtils.ReadInt32(data, block.DataOffset, nif.IsBigEndian);
        if (nameIdx < 0 || nameIdx >= nif.Strings.Count) return null;
        return nif.Strings[nameIdx];
    }

    /// <summary>
    ///     Returns true if the shape name indicates a gore/dismembered variant that should be filtered out.
    /// </summary>
    internal static bool IsGoreShape(string? name)
    {
        if (name == null) return false;
        return name.Contains("gore", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("dismember", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("decap", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Parse the skin instance block reference from a NiTriShape/NiTriStrips block.
    ///     This is the field immediately after the data ref in the NiGeometry layout.
    /// </summary>
    internal static int ParseShapeSkinInstanceRef(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiObjectNET
        if (pos + 4 > end) return -1;
        pos += 4; // Name
        if (pos + 4 > end) return -1;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;
        if (pos + 4 > end) return -1;
        pos += 4; // Controller

        // Skip NiAVObject
        pos += bsVersion > 26 ? 4 : 2; // Flags
        pos += 12 + 36 + 4; // Translation + Rotation + Scale

        // Properties array (BS <= 34)
        if (bsVersion <= 34)
        {
            if (pos + 4 > end) return -1;
            var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += (int)Math.Min(numProperties, 100) * 4;
        }

        // Collision object ref
        if (pos + 4 > end) return -1;
        pos += 4;

        // NiGeometry: Data ref (skip)
        if (pos + 4 > end) return -1;
        pos += 4;

        // Skin Instance ref
        if (pos + 4 > end) return -1;
        return BinaryUtils.ReadInt32(data, pos, be);
    }

    /// <summary>
    ///     Parse body part IDs from a BSDismemberSkinInstance block.
    ///     Layout: NiSkinInstance fields (Data + SkinPartition + SkeletonRoot + NumBones + Bones[]),
    ///     then NumPartitions + BodyPartList[] where each entry is PartFlag(2) + BodyPart(2).
    /// </summary>
    internal static int[]? ParseDismemberPartitions(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // NiSkinInstance: Data(4) + SkinPartition(4) + SkeletonRoot(4) = 12 bytes
        if (pos + 12 > end) return null;
        pos += 12;

        // NumBones(4) + Bone refs
        if (pos + 4 > end) return null;
        var numBones = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numBones, 500) * 4;

        // BSDismemberSkinInstance: NumPartitions
        if (pos + 4 > end) return null;
        var numPartitions = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (numPartitions == 0 || numPartitions > 100 || pos + numPartitions * 4 > end)
            return null;

        var bodyParts = new int[(int)numPartitions];
        for (var i = 0; i < numPartitions; i++)
        {
            // BodyPartList: PartFlag(uint16) + BodyPart(uint16) = 4 bytes
            pos += 2; // Skip PartFlag
            bodyParts[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        return bodyParts;
    }

    /// <summary>
    ///     Returns true if all body part IDs are dismember gore caps.
    ///     FO3/FNV dismember body parts: 0-13 = intact, 1000+ = torso sections (intact),
    ///     100-113 = section caps (gore stump on limb side),
    ///     200-213 = torso caps (gore stump on torso side).
    /// </summary>
    internal static bool IsDismemberGoreShape(int[]? bodyParts)
    {
        if (bodyParts == null || bodyParts.Length == 0)
            return false;

        // Gore cap body parts use IDs 100-299 (section caps, torso caps, etc.).
        // Normal body parts use IDs 0-99 (Head=0, Torso=3, LeftHand=4, etc.).
        // Any partition in the gore range means the shape is a dismemberment cap.
        foreach (var bp in bodyParts)
        {
            if (bp >= 100 && bp <= 299)
                return true;
        }

        return false;
    }

    internal static int ParseGeometryAdditionalDataRef(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // GroupId (4)
        pos += 4;

        // NumVertices (2)
        if (pos + 2 > end) return -1;
        var numVertices = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;

        // KeepFlags + CompressFlags (BS >= 34)
        if (bsVersion >= 34) pos += 2;

        // HasVertices (1) + vertex data
        if (pos + 1 > end) return -1;
        if (data[pos++] != 0)
        {
            pos += numVertices * 12; // Vector3 per vertex
        }

        // BSVectorFlags (BS >= 34)
        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end) return -1;
            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // HasNormals (1) + normal/tangent data
        if (pos + 1 > end) return -1;
        if (data[pos++] != 0)
        {
            pos += numVertices * 12; // Normals
            if (bsVersion >= 34)
            {
                pos += 16; // Tangent space center + radius
                if ((bsVectorFlags & 0x1000) != 0)
                {
                    pos += numVertices * 24; // Tangents + Bitangents
                }
            }
        }
        else if (bsVersion >= 34)
        {
            pos += 16; // Center + radius even without normals
        }

        // HasVertexColors (1) + vertex color data
        if (pos + 1 > end) return -1;
        if (data[pos++] != 0)
        {
            pos += numVertices * 16; // Color4 per vertex
        }

        // UV sets
        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            pos += numVertices * numUvSets * 8;
        }
        else
        {
            if (pos + 4 > end) return -1;
            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4; // numUvSets + tSpaceFlag
            pos += numVertices * numUvSets * 8;
        }

        // ConsistencyFlags (2)
        pos += 2;

        // Additional Data ref (4)
        if (pos + 4 > end) return -1;
        return BinaryUtils.ReadInt32(data, pos, be);
    }

    /// <summary>
    ///     Read vertex count from a geometry data block header (GroupId(4) + NumVertices(2)).
    /// </summary>
    internal static int ReadVertexCount(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset + 4; // Skip GroupId
        if (pos + 2 > block.DataOffset + block.Size) return -1;
        return BinaryUtils.ReadUInt16(data, pos, be);
    }

    /// <summary>
    ///     Parse the children array from a NiNode block.
    /// </summary>
    internal static List<int>? ParseNodeChildren(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiObjectNET
        if (pos + 4 > end) return null;
        pos += 4; // Name
        if (pos + 4 > end) return null;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;
        if (pos + 4 > end) return null;
        pos += 4; // Controller

        // Skip NiAVObject: flags + translation(12) + rotation(36) + scale(4) = 52 or 54 bytes
        pos += bsVersion > 26 ? 4 : 2; // Flags
        pos += 12 + 36 + 4; // Translation + Rotation + Scale

        // Properties array (BS <= 34)
        if (bsVersion <= 34)
        {
            if (pos + 4 > end) return null;
            var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += (int)Math.Min(numProperties, 100) * 4;
        }

        // Collision object ref
        if (pos + 4 > end) return null;
        pos += 4;

        // NiNode: Children array
        if (pos + 4 > end) return null;
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

    /// <summary>
    ///     Parse the data block reference from a NiTriShape/NiTriStrips block.
    ///     Layout: NiAVObject header, then NiGeometry fields (data ref, skin instance, etc.)
    /// </summary>
    internal static int ParseShapeDataRef(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiObjectNET
        if (pos + 4 > end) return -1;
        pos += 4; // Name
        if (pos + 4 > end) return -1;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;
        if (pos + 4 > end) return -1;
        pos += 4; // Controller

        // Skip NiAVObject
        pos += bsVersion > 26 ? 4 : 2; // Flags
        pos += 12 + 36 + 4; // Translation + Rotation + Scale

        // Properties array (BS <= 34)
        if (bsVersion <= 34)
        {
            if (pos + 4 > end) return -1;
            var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += (int)Math.Min(numProperties, 100) * 4;
        }

        // Collision object ref
        if (pos + 4 > end) return -1;
        pos += 4;

        // NiGeometry: Data ref (this is what we want)
        if (pos + 4 > end) return -1;
        return BinaryUtils.ReadInt32(data, pos, be);
    }

    /// <summary>
    ///     Parse the property block references from a NiTriShape/NiTriStrips block.
    ///     Same NiObjectNET/NiAVObject header skip as ParseShapeDataRef, but returns property refs.
    /// </summary>
    internal static List<int>? ParseShapePropertyRefs(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        if (bsVersion > 34)
        {
            return null; // No properties array in newer versions
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiObjectNET
        if (pos + 4 > end) return null;
        pos += 4; // Name
        if (pos + 4 > end) return null;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;
        if (pos + 4 > end) return null;
        pos += 4; // Controller

        // Skip NiAVObject flags
        pos += bsVersion > 26 ? 4 : 2;
        // Skip Translation + Rotation + Scale
        pos += 12 + 36 + 4;

        // Properties array
        if (pos + 4 > end) return null;
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

    internal static RenderableSubmesh? ExtractSubmesh(byte[] data, NifInfo nif,
        int shapeIndex, int dataIndex, Dictionary<int, Matrix4x4> worldTransforms,
        string? diffuseTexturePath = null, string? normalMapTexturePath = null,
        bool isEmissive = false,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useVertexColors = true,
        bool isDoubleSided = false,
        bool hasAlphaBlend = false,
        bool hasAlphaTest = false,
        byte alphaTestThreshold = 128,
        byte alphaTestFunction = 4,
        bool isEyeEnvmap = false,
        float envMapScale = 0f,
        byte srcBlendMode = 6,
        byte dstBlendMode = 7,
        float materialAlpha = 1f)
    {
        var dataBlock = nif.Blocks[dataIndex];
        var be = nif.IsBigEndian;

        // Get world transform for this shape
        worldTransforms.TryGetValue(shapeIndex, out var transform);
        if (transform == default)
        {
            transform = Matrix4x4.Identity;
        }

        var typeName = dataBlock.TypeName;

        RenderableSubmesh? submesh = null;
        if (typeName is "NiTriShapeData")
        {
            submesh = ExtractTriShapeData(data, dataBlock, be, nif.BsVersion, transform, skinning);
        }
        else if (typeName is "NiTriStripsData")
        {
            submesh = ExtractTriStripsData(data, dataBlock, be, nif.BsVersion, transform, skinning);
        }

        // Attach texture paths and emissive flag
        if (submesh != null &&
            (isEmissive || (submesh.UVs != null &&
             (diffuseTexturePath != null || normalMapTexturePath != null))))
        {
            return new RenderableSubmesh
            {
                Positions = submesh.Positions,
                Triangles = submesh.Triangles,
                Normals = submesh.Normals,
                UVs = submesh.UVs,
                VertexColors = submesh.VertexColors,
                Tangents = submesh.Tangents,
                Bitangents = submesh.Bitangents,
                DiffuseTexturePath = diffuseTexturePath,
                NormalMapTexturePath = normalMapTexturePath,
                IsEmissive = isEmissive,
                UseVertexColors = useVertexColors,
                IsDoubleSided = isDoubleSided,
                HasAlphaBlend = hasAlphaBlend,
                HasAlphaTest = hasAlphaTest,
                AlphaTestThreshold = alphaTestThreshold,
                AlphaTestFunction = alphaTestFunction,
                IsEyeEnvmap = isEyeEnvmap,
                EnvMapScale = envMapScale,
                SrcBlendMode = srcBlendMode,
                DstBlendMode = dstBlendMode,
                MaterialAlpha = materialAlpha
            };
        }

        return submesh;
    }

    internal static RenderableSubmesh? ExtractTriShapeData(byte[] data, BlockInfo block, bool be,
        uint bsVersion, Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // GroupId
        pos += 4;

        // NumVertices
        if (pos + 2 > end) return null;
        var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numVerts == 0) return null;

        // KeepFlags, CompressFlags (BS >= 34)
        if (bsVersion >= 34) pos += 2;

        // HasVertices
        if (pos + 1 > end) return null;
        var hasVertices = data[pos++];

        float[]? positions = null;
        if (hasVertices != 0)
        {
            positions = ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;
        }

        // BS Vector Flags (BS >= 34)
        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end) return null;
            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // HasNormals
        if (pos + 1 > end) return null;
        var hasNormals = data[pos++];

        float[]? normals = null;
        float[]? tangents = null;
        float[]? bitangents = null;
        if (hasNormals != 0)
        {
            normals = ReadVertexPositions(data, pos, numVerts, be); // Same format: 3 floats
            pos += numVerts * 12;

            // Tangent space center + radius (BS >= 34)
            if (bsVersion >= 34) pos += 16;

            // Tangents + Bitangents (if flag set)
            if (bsVersion >= 34 && (bsVectorFlags & 0x1000) != 0)
            {
                if (pos + numVerts * 24 <= end)
                {
                    tangents = ReadVertexPositions(data, pos, numVerts, be);
                    bitangents = ReadVertexPositions(data, pos + numVerts * 12, numVerts, be);
                }

                pos += numVerts * 24;
            }
        }
        else if (bsVersion >= 34)
        {
            // Center + radius even without normals
            pos += 16;
        }

        // HasVertexColors
        if (pos + 1 > end) return null;
        var hasVertexColors = data[pos++];
        byte[]? vertexColors = null;
        if (hasVertexColors != 0)
        {
            if (pos + numVerts * 16 <= end)
            {
                vertexColors = ReadVertexColors(data, pos, numVerts, be);
            }

            pos += numVerts * 16;
        }

        // UV sets — read first UV set for texture mapping
        float[]? uvs = null;
        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = ReadUVs(data, pos, numVerts, be);
            }

            pos += numVerts * numUvSets * 8;
        }
        else
        {
            if (pos + 4 > end) return null;
            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4; // numUvSets + tSpaceFlag
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = ReadUVs(data, pos, numVerts, be);
            }

            pos += numVerts * numUvSets * 8;
        }

        // ConsistencyFlags + AdditionalData ref
        pos += 2 + 4;

        // NumTriangles
        if (pos + 2 > end) return null;
        var numTriangles = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;

        // NumTrianglePoints
        if (pos + 4 > end) return null;
        pos += 4;

        // HasTriangles
        if (pos + 1 > end) return null;
        var hasTriangles = data[pos++];

        if (hasTriangles == 0 || numTriangles == 0 || positions == null)
        {
            return null;
        }

        // Read triangle indices
        if (pos + numTriangles * 6 > end) return null;
        var triangles = new ushort[numTriangles * 3];
        for (var i = 0; i < numTriangles * 3; i++)
        {
            triangles[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // Apply skinning (if available) or simple node transform
        float[] finalPositions;
        float[]? finalNormals;
        float[]? finalTangents;
        float[]? finalBitangents;

        if (skinning.HasValue)
        {
            var (perVertexInfluences, boneSkinMatrices) = skinning.Value;
            finalPositions = NifSkinningExtractor.ApplySkinningPositions(positions, perVertexInfluences, boneSkinMatrices);
            finalNormals = normals != null
                ? NifSkinningExtractor.ApplySkinningNormals(normals, perVertexInfluences, boneSkinMatrices) : null;
            finalTangents = tangents != null
                ? NifSkinningExtractor.ApplySkinningNormals(tangents, perVertexInfluences, boneSkinMatrices) : null;
            finalBitangents = bitangents != null
                ? NifSkinningExtractor.ApplySkinningNormals(bitangents, perVertexInfluences, boneSkinMatrices) : null;
        }
        else
        {
            finalPositions = TransformPositions(positions, transform);
            finalNormals = normals != null ? TransformNormals(normals, transform) : null;
            finalTangents = tangents != null ? TransformNormals(tangents, transform) : null;
            finalBitangents = bitangents != null ? TransformNormals(bitangents, transform) : null;
        }

        return new RenderableSubmesh
        {
            Positions = finalPositions,
            Triangles = triangles,
            Normals = finalNormals,
            UVs = uvs,
            VertexColors = vertexColors,
            Tangents = finalTangents,
            Bitangents = finalBitangents
        };
    }

    internal static RenderableSubmesh? ExtractTriStripsData(byte[] data, BlockInfo block, bool be,
        uint bsVersion, Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null)
    {
        // Use the existing strip extractor for triangle indices
        var triangles = NifTriStripExtractor.ExtractTrianglesFromTriStripsData(data, block, be);
        if (triangles == null || triangles.Length == 0)
        {
            return null;
        }

        // Parse vertex positions (same header as NiTriShapeData)
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos += 4; // GroupId

        if (pos + 2 > end) return null;
        var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numVerts == 0) return null;

        if (bsVersion >= 34) pos += 2; // KeepFlags, CompressFlags

        if (pos + 1 > end) return null;
        var hasVertices = data[pos++];
        if (hasVertices == 0) return null;

        var positions = ReadVertexPositions(data, pos, numVerts, be);
        pos += numVerts * 12;

        // BS Vector Flags
        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end) return null;
            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // HasNormals
        if (pos + 1 > end) return null;
        var hasNormals = data[pos++];
        float[]? normals = null;
        float[]? tangents = null;
        float[]? bitangents = null;
        if (hasNormals != 0)
        {
            normals = ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;

            if (bsVersion >= 34) pos += 16; // Center + radius

            if (bsVersion >= 34 && (bsVectorFlags & 0x1000) != 0)
            {
                if (pos + numVerts * 24 <= end)
                {
                    tangents = ReadVertexPositions(data, pos, numVerts, be);
                    bitangents = ReadVertexPositions(data, pos + numVerts * 12, numVerts, be);
                }

                pos += numVerts * 24;
            }
        }
        else if (bsVersion >= 34)
        {
            pos += 16; // Center + radius even without normals
        }

        // HasVertexColors
        byte[]? vertexColors = null;
        if (pos + 1 <= end)
        {
            var hasVertexColors = data[pos++];
            if (hasVertexColors != 0)
            {
                if (pos + numVerts * 16 <= end)
                {
                    vertexColors = ReadVertexColors(data, pos, numVerts, be);
                }

                pos += numVerts * 16;
            }
        }

        // UV sets — read first UV set for texture mapping
        float[]? uvs = null;
        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = ReadUVs(data, pos, numVerts, be);
            }
        }
        else if (pos + 4 <= end)
        {
            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = ReadUVs(data, pos, numVerts, be);
            }
        }

        // Apply skinning (if available) or simple node transform
        float[] finalPositions;
        float[]? finalNormals;
        float[]? finalTangents;
        float[]? finalBitangents;

        if (skinning.HasValue)
        {
            var (perVertexInfluences, boneSkinMatrices) = skinning.Value;
            finalPositions = NifSkinningExtractor.ApplySkinningPositions(positions, perVertexInfluences, boneSkinMatrices);
            finalNormals = normals != null
                ? NifSkinningExtractor.ApplySkinningNormals(normals, perVertexInfluences, boneSkinMatrices) : null;
            finalTangents = tangents != null
                ? NifSkinningExtractor.ApplySkinningNormals(tangents, perVertexInfluences, boneSkinMatrices) : null;
            finalBitangents = bitangents != null
                ? NifSkinningExtractor.ApplySkinningNormals(bitangents, perVertexInfluences, boneSkinMatrices) : null;
        }
        else
        {
            finalPositions = TransformPositions(positions, transform);
            finalNormals = normals != null ? TransformNormals(normals, transform) : null;
            finalTangents = tangents != null ? TransformNormals(tangents, transform) : null;
            finalBitangents = bitangents != null ? TransformNormals(bitangents, transform) : null;
        }

        return new RenderableSubmesh
        {
            Positions = finalPositions,
            Triangles = triangles,
            Normals = finalNormals,
            UVs = uvs,
            VertexColors = vertexColors,
            Tangents = finalTangents,
            Bitangents = finalBitangents
        };
    }

    internal static float[] ReadVertexPositions(byte[] data, int offset, int numVerts, bool be)
    {
        var positions = new float[numVerts * 3];
        for (var v = 0; v < numVerts; v++)
        {
            positions[v * 3 + 0] = BinaryUtils.ReadFloat(data, offset + v * 12, be);
            positions[v * 3 + 1] = BinaryUtils.ReadFloat(data, offset + v * 12 + 4, be);
            positions[v * 3 + 2] = BinaryUtils.ReadFloat(data, offset + v * 12 + 8, be);
        }

        return positions;
    }

    internal static float[] ReadUVs(byte[] data, int offset, int numVerts, bool be)
    {
        var uvs = new float[numVerts * 2];
        for (var v = 0; v < numVerts; v++)
        {
            uvs[v * 2 + 0] = BinaryUtils.ReadFloat(data, offset + v * 8, be);
            uvs[v * 2 + 1] = BinaryUtils.ReadFloat(data, offset + v * 8 + 4, be);
        }

        return uvs;
    }

    /// <summary>
    ///     Check if any NiStencilProperty in the property refs has DrawMode = DRAW_BOTH (3).
    ///     NiStencilProperty format (FNV version): NiObjectNET header + Flags(ushort) where
    ///     bits [12:11] encode DrawMode (0=CCW_OR_BOTH, 1=CCW, 2=CW, 3=BOTH).
    /// </summary>
    internal static bool ReadIsDoubleSided(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
                continue;

            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName != "NiStencilProperty")
                continue;

            var be = nif.IsBigEndian;
            var pos = propBlock.DataOffset;
            var end = propBlock.DataOffset + propBlock.Size;

            // Skip NiObjectNET: Name(4) + NumExtraData(4) + refs + Controller(4)
            if (!SkipNiObjectNET(data, ref pos, end, be))
                return false;

            // NiStencilProperty flags (ushort) — bitfield encoding:
            // Bits [0]: Stencil enabled
            // Bits [4:1]: Stencil function
            // Bits [8:5]: Fail action
            // Bits [12:9]: Z-fail action
            // Bits [14:13]: Pass action — wait, let me use the actual observed layout
            // Actually for FNV the flags ushort has DrawMode in bits [12:11] (2 bits)
            if (pos + 2 > end) return false;
            var flags = BinaryUtils.ReadUInt16(data, pos, be);

            // DrawMode: bits [12:11] — extract 2-bit value
            var drawMode = (flags >> 11) & 0x3;
            return drawMode == 3; // DRAW_BOTH
        }

        // Gamebryo defaults to no backface culling (double-sided) when no NiStencilProperty
        return true;
    }

    /// <summary>
    ///     Read NiAlphaProperty to extract alpha blend/test flags, threshold, and blend modes.
    ///     NiAlphaProperty layout: NiObjectNET header + AlphaFlags(ushort) + Threshold(byte).
    ///     AlphaFlags: bit 0 = blend enable, bits 1-4 = src blend, bits 5-8 = dst blend,
    ///     bit 9 = test enable, bits 10-12 = test function.
    /// </summary>
    internal static void ReadAlphaProperty(byte[] data, NifInfo nif, List<int> propertyRefs,
        out bool hasAlphaBlend, out bool hasAlphaTest, out byte alphaTestThreshold,
        out byte alphaTestFunction, out byte srcBlendMode, out byte dstBlendMode)
    {
        hasAlphaBlend = false;
        hasAlphaTest = false;
        alphaTestThreshold = 128;
        alphaTestFunction = 4; // GREATER — matches existing a <= threshold semantics
        srcBlendMode = 6; // SRC_ALPHA
        dstBlendMode = 7; // INV_SRC_ALPHA

        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
                continue;

            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName != "NiAlphaProperty")
                continue;

            var be = nif.IsBigEndian;
            var pos = propBlock.DataOffset;
            var end = propBlock.DataOffset + propBlock.Size;

            // Skip NiObjectNET: Name(4) + NumExtraData(4) + refs + Controller(4)
            if (!SkipNiObjectNET(data, ref pos, end, be))
                return;

            // AlphaFlags (ushort): bit 0 = blend enable, bits 1-4 = src blend,
            // bits 5-8 = dst blend, bit 9 = test enable, bits 10-12 = test function
            if (pos + 2 > end) return;
            var alphaFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;

            // Threshold (byte)
            if (pos + 1 > end) return;
            var threshold = data[pos];

            hasAlphaBlend = (alphaFlags & 1) != 0;
            hasAlphaTest = (alphaFlags & (1 << 9)) != 0;
            alphaTestThreshold = threshold;
            alphaTestFunction = (byte)((alphaFlags >> 10) & 0x7);
            srcBlendMode = (byte)((alphaFlags >> 1) & 0xF);
            dstBlendMode = (byte)((alphaFlags >> 5) & 0xF);
            return;
        }
    }

    /// <summary>
    ///     Read material alpha from NiMaterialProperty.
    ///     Layout: NiObjectNET header + Ambient(12B) + Diffuse(12B) + Specular(12B) + Emissive(12B) + Glossiness(4B) + Alpha(4B).
    ///     Values &lt; 1.0 trigger alpha blending in the game engine (SetupGeometryAlphaBlending VA 0x82AAD430).
    /// </summary>
    internal static float ReadMaterialAlpha(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count) continue;
            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName != "NiMaterialProperty") continue;

            var pos = propBlock.DataOffset;
            var end = pos + propBlock.Size;
            if (!SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian))
                return 1f;

            // Ambient(12B) + Diffuse(12B) + Specular(12B) + Emissive(12B) + Glossiness(4B) = 52 bytes to Alpha
            var alphaOffset = pos + 52;
            if (alphaOffset + 4 > end) return 1f;
            return BinaryUtils.ReadFloat(data, alphaOffset, nif.IsBigEndian);
        }

        return 1f; // Default: fully opaque
    }

    /// <summary>
    ///     Read vertex colors: 4 floats (RGBA, 0.0-1.0) per vertex -> convert to byte[].
    /// </summary>
    internal static byte[] ReadVertexColors(byte[] data, int offset, int numVerts, bool be)
    {
        var colors = new byte[numVerts * 4];
        for (var v = 0; v < numVerts; v++)
        {
            var baseOffset = offset + v * 16;
            colors[v * 4 + 0] = (byte)(Math.Clamp(BinaryUtils.ReadFloat(data, baseOffset, be), 0f, 1f) * 255);
            colors[v * 4 + 1] = (byte)(Math.Clamp(BinaryUtils.ReadFloat(data, baseOffset + 4, be), 0f, 1f) * 255);
            colors[v * 4 + 2] = (byte)(Math.Clamp(BinaryUtils.ReadFloat(data, baseOffset + 8, be), 0f, 1f) * 255);
            colors[v * 4 + 3] = (byte)(Math.Clamp(BinaryUtils.ReadFloat(data, baseOffset + 12, be), 0f, 1f) * 255);
        }

        return colors;
    }

    /// <summary>
    ///     Transform vertex positions by a 4x4 matrix.
    /// </summary>
    internal static float[] TransformPositions(float[] positions, Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
        {
            return positions;
        }

        var result = new float[positions.Length];
        for (var i = 0; i < positions.Length; i += 3)
        {
            var v = Vector3.Transform(new Vector3(positions[i], positions[i + 1], positions[i + 2]), transform);
            result[i] = v.X;
            result[i + 1] = v.Y;
            result[i + 2] = v.Z;
        }

        return result;
    }

    /// <summary>
    ///     Transform normals by the rotation portion of a 4x4 matrix (no translation).
    /// </summary>
    internal static float[] TransformNormals(float[] normals, Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
        {
            return normals;
        }

        var result = new float[normals.Length];
        for (var i = 0; i < normals.Length; i += 3)
        {
            var n = Vector3.TransformNormal(new Vector3(normals[i], normals[i + 1], normals[i + 2]), transform);
            var len = n.Length();
            if (len > 0.001f)
            {
                n /= len;
            }

            result[i] = n.X;
            result[i + 1] = n.Y;
            result[i + 2] = n.Z;
        }

        return result;
    }

    /// <summary>
    ///     Recomputes smooth per-vertex normals from triangle geometry using area-weighted face normals.
    ///     Used when NIF-stored normals assume a skeleton rotation that we're not applying (e.g., eye meshes
    ///     extracted in bind-pose instead of skinned pose).
    /// </summary>
    public static float[] RecomputeSmoothNormals(float[] positions, ushort[] triangles)
    {
        var numVerts = positions.Length / 3;
        var normals = new float[positions.Length]; // initialized to zero

        // Accumulate area-weighted face normals to each vertex
        for (var t = 0; t < triangles.Length; t += 3)
        {
            var i0 = triangles[t];
            var i1 = triangles[t + 1];
            var i2 = triangles[t + 2];

            if (i0 >= numVerts || i1 >= numVerts || i2 >= numVerts)
            {
                continue;
            }

            var v0 = new Vector3(positions[i0 * 3], positions[i0 * 3 + 1], positions[i0 * 3 + 2]);
            var v1 = new Vector3(positions[i1 * 3], positions[i1 * 3 + 1], positions[i1 * 3 + 2]);
            var v2 = new Vector3(positions[i2 * 3], positions[i2 * 3 + 1], positions[i2 * 3 + 2]);

            // Cross product of edges — magnitude is proportional to triangle area (area-weighted)
            var faceNormal = Vector3.Cross(v1 - v0, v2 - v0);

            // Accumulate to each vertex of the triangle
            normals[i0 * 3] += faceNormal.X;
            normals[i0 * 3 + 1] += faceNormal.Y;
            normals[i0 * 3 + 2] += faceNormal.Z;

            normals[i1 * 3] += faceNormal.X;
            normals[i1 * 3 + 1] += faceNormal.Y;
            normals[i1 * 3 + 2] += faceNormal.Z;

            normals[i2 * 3] += faceNormal.X;
            normals[i2 * 3 + 1] += faceNormal.Y;
            normals[i2 * 3 + 2] += faceNormal.Z;
        }

        // Normalize each accumulated vertex normal
        for (var i = 0; i < normals.Length; i += 3)
        {
            var n = new Vector3(normals[i], normals[i + 1], normals[i + 2]);
            var len = n.Length();
            if (len > 0.001f)
            {
                n /= len;
            }

            normals[i] = n.X;
            normals[i + 1] = n.Y;
            normals[i + 2] = n.Z;
        }

        return normals;
    }
}
