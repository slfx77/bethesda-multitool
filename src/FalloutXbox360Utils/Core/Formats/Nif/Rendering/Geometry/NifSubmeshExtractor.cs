using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Extracts renderable submeshes from NiTriShapeData and NiTriStripsData blocks.
/// </summary>
internal static class NifSubmeshExtractor
{
    private static readonly Logger Log = Logger.Instance;

    internal static RenderableSubmesh? ExtractSubmesh(
        byte[] data,
        NifInfo nif,
        int shapeIndex,
        int dataIndex,
        Dictionary<int, Matrix4x4> worldTransforms,
        string? shapeName = null,
        NifShaderTextureMetadata? shaderMetadata = null,
        string? diffuseTexturePath = null,
        string? normalMapTexturePath = null,
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
        float materialAlpha = 1f,
        float materialGlossiness = 10f,
        (float R, float G, float B) specularColor = default,
        bool useDualQuaternionSkinning = false,
        float[]? preSkinMorphDeltas = null)
    {
        var dataBlock = nif.Blocks[dataIndex];
        worldTransforms.TryGetValue(shapeIndex, out var transform);
        if (transform == default)
        {
            transform = Matrix4x4.Identity;
        }

        var submesh = dataBlock.TypeName switch
        {
            "NiTriShapeData" => ExtractTriShapeData(
                data,
                dataBlock,
                nif.IsBigEndian,
                nif.BsVersion,
                transform,
                skinning,
                useDualQuaternionSkinning,
                preSkinMorphDeltas,
                shapeName),
            "NiTriStripsData" => ExtractTriStripsData(
                data,
                dataBlock,
                nif.IsBigEndian,
                nif.BsVersion,
                transform,
                skinning,
                useDualQuaternionSkinning,
                preSkinMorphDeltas,
                shapeName),
            // BSTriShape and its variants are self-contained (the shape block IS its own data block,
            // so dataIndex == shapeIndex). Skyrim SE / Fallout 4 / Fallout 76 geometry.
            "BSTriShape" or "BSSubIndexTriShape" or "BSMeshLODTriShape" or "BSDynamicTriShape"
                => ExtractBsTriShape(data, dataBlock, nif.IsBigEndian, nif.BsVersion, transform, shapeName),
            _ => null
        };

        if (submesh == null)
        {
            return null;
        }

        return new RenderableSubmesh
        {
            ShapeName = shapeName,
            Positions = submesh.Positions,
            Triangles = submesh.Triangles,
            Normals = submesh.Normals,
            UVs = submesh.UVs,
            VertexColors = submesh.VertexColors,
            Tangents = submesh.Tangents,
            Bitangents = submesh.Bitangents,
            BindPosePositions = submesh.BindPosePositions,
            ShaderMetadata = shaderMetadata,
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
            MaterialAlpha = materialAlpha,
            MaterialGlossiness = materialGlossiness,
            SpecularColor = specularColor
        };
    }

    internal static RenderableSubmesh? ExtractTriShapeData(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useDualQuaternionSkinning = false,
        float[]? preSkinMorphDeltas = null,
        string? shapeName = null)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos += 4;
        if (pos + 2 > end)
        {
            return null;
        }

        var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numVerts == 0)
        {
            return null;
        }

        if (bsVersion >= 34)
        {
            pos += 2;
        }

        if (pos + 1 > end)
        {
            return null;
        }

        float[]? positions = null;
        if (data[pos++] != 0)
        {
            positions = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;
        }

        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end)
            {
                return null;
            }

            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;

            // Skyrim and later (BS stream > 34) insert a 4-byte Material CRC into NiGeometryData
            // after the BS Data Flags, before the normals. FNV (BS 34) does not.
            if (bsVersion > 34)
            {
                pos += 4;
            }
        }

        if (pos + 1 > end)
        {
            return null;
        }

        float[]? normals = null;
        float[]? tangents = null;
        float[]? bitangents = null;
        if (data[pos++] != 0)
        {
            normals = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;

            if (bsVersion >= 34)
            {
                pos += 16;
                if ((bsVectorFlags & 0x1000) != 0)
                {
                    if (pos + numVerts * 24 <= end)
                    {
                        tangents = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
                        bitangents = NifGeometryDataReader.ReadVertexPositions(
                            data,
                            pos + numVerts * 12,
                            numVerts,
                            be);
                    }

                    pos += numVerts * 24;
                }
            }
        }
        else if (bsVersion >= 34)
        {
            pos += 16;
        }

        byte[]? vertexColors = null;
        if (pos + 1 > end)
        {
            return null;
        }

        if (data[pos++] != 0)
        {
            if (pos + numVerts * 16 <= end)
            {
                vertexColors = NifGeometryDataReader.ReadVertexColors(data, pos, numVerts, be);
            }

            pos += numVerts * 16;
        }

        float[]? uvs = null;
        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = NifGeometryDataReader.ReadUvs(data, pos, numVerts, be);
            }

            pos += numVerts * numUvSets * 8;
        }
        else
        {
            if (pos + 4 > end)
            {
                return null;
            }

            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = NifGeometryDataReader.ReadUvs(data, pos, numVerts, be);
            }

            pos += numVerts * numUvSets * 8;
        }

        pos += 2 + 4;
        if (pos + 2 > end)
        {
            return null;
        }

        var numTriangles = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (pos + 5 > end)
        {
            return null;
        }

        pos += 4;
        if (data[pos++] == 0 || numTriangles == 0 || positions == null)
        {
            return null;
        }

        if (pos + numTriangles * 6 > end)
        {
            return null;
        }

        var triangles = new ushort[numTriangles * 3];
        for (var i = 0; i < triangles.Length; i++)
        {
            triangles[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // Apply EGM morph deltas in bind-pose space BEFORE skinning transforms vertices
        if (preSkinMorphDeltas != null)
        {
            var count = Math.Min(positions.Length, preSkinMorphDeltas.Length);
            for (var i = 0; i < count; i++)
                positions[i] += preSkinMorphDeltas[i];
        }

        // Capture bind-pose world positions before skinning for boundary vertex stitching
        var bindPosePositions = skinning.HasValue
            ? NifGeometryTransformUtils.TransformPositions(positions, transform)
            : null;

        var transformed = ApplySkinningOrTransform(
            positions,
            normals,
            tangents,
            bitangents,
            transform,
            skinning,
            useDualQuaternionSkinning,
            shapeName);

        return new RenderableSubmesh
        {
            Positions = transformed.Positions,
            Triangles = triangles,
            Normals = transformed.Normals
                      ?? NifGeometryTransformUtils.RecomputeSmoothNormals(transformed.Positions, triangles),
            UVs = uvs,
            VertexColors = vertexColors,
            Tangents = transformed.Tangents,
            Bitangents = transformed.Bitangents,
            BindPosePositions = bindPosePositions
        };
    }

    internal static RenderableSubmesh? ExtractTriStripsData(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useDualQuaternionSkinning = false,
        float[]? preSkinMorphDeltas = null,
        string? shapeName = null)
    {
        var triangles = NifTriStripExtractor.ExtractTrianglesFromTriStripsData(data, block, be);
        if (triangles == null || triangles.Length == 0)
        {
            return null;
        }

        var pos = block.DataOffset + 4;
        var end = block.DataOffset + block.Size;
        if (pos + 2 > end)
        {
            return null;
        }

        var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numVerts == 0)
        {
            return null;
        }

        if (bsVersion >= 34)
        {
            pos += 2;
        }

        if (pos + 1 > end || data[pos++] == 0)
        {
            return null;
        }

        var positions = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
        pos += numVerts * 12;

        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end)
            {
                return null;
            }

            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;

            // Skyrim and later (BS stream > 34) insert a 4-byte Material CRC into NiGeometryData
            // after the BS Data Flags, before the normals. FNV (BS 34) does not.
            if (bsVersion > 34)
            {
                pos += 4;
            }
        }

        if (pos + 1 > end)
        {
            return null;
        }

        float[]? normals = null;
        float[]? tangents = null;
        float[]? bitangents = null;
        if (data[pos++] != 0)
        {
            normals = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;

            if (bsVersion >= 34)
            {
                pos += 16;
                if ((bsVectorFlags & 0x1000) != 0)
                {
                    if (pos + numVerts * 24 <= end)
                    {
                        tangents = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
                        bitangents = NifGeometryDataReader.ReadVertexPositions(
                            data,
                            pos + numVerts * 12,
                            numVerts,
                            be);
                    }

                    pos += numVerts * 24;
                }
            }
        }
        else if (bsVersion >= 34)
        {
            pos += 16;
        }

        byte[]? vertexColors = null;
        if (pos + 1 <= end && data[pos++] != 0)
        {
            if (pos + numVerts * 16 <= end)
            {
                vertexColors = NifGeometryDataReader.ReadVertexColors(data, pos, numVerts, be);
            }

            pos += numVerts * 16;
        }

        float[]? uvs = null;
        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = NifGeometryDataReader.ReadUvs(data, pos, numVerts, be);
            }
        }
        else if (pos + 4 <= end)
        {
            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = NifGeometryDataReader.ReadUvs(data, pos, numVerts, be);
            }
        }

        // Apply EGM morph deltas in bind-pose space BEFORE skinning transforms vertices
        if (preSkinMorphDeltas != null)
        {
            var count = Math.Min(positions.Length, preSkinMorphDeltas.Length);
            for (var i = 0; i < count; i++)
                positions[i] += preSkinMorphDeltas[i];
        }

        // Capture bind-pose world positions before skinning for boundary vertex stitching
        var bindPosePositions = skinning.HasValue
            ? NifGeometryTransformUtils.TransformPositions(positions, transform)
            : null;

        var transformed = ApplySkinningOrTransform(
            positions,
            normals,
            tangents,
            bitangents,
            transform,
            skinning,
            useDualQuaternionSkinning,
            shapeName);

        return new RenderableSubmesh
        {
            Positions = transformed.Positions,
            Triangles = triangles,
            Normals = transformed.Normals
                      ?? NifGeometryTransformUtils.RecomputeSmoothNormals(transformed.Positions, triangles),
            UVs = uvs,
            VertexColors = vertexColors,
            Tangents = transformed.Tangents,
            Bitangents = transformed.Bitangents,
            BindPosePositions = bindPosePositions
        };
    }

    /// <summary>
    ///     Extract geometry from a self-contained BSTriShape block (Skyrim SE / Fallout 4 /
    ///     Fallout 76). The interleaved vertex buffer is decoded per the BSVertexDesc bitfield, which
    ///     gives both the per-attribute presence flags (high 12 bits) and each attribute's byte offset
    ///     within the vertex. Positions are full float3 in Skyrim SE (and FO4 with the Full_Precision
    ///     flag) and half3 otherwise. Static path only: applies the node world transform, no skinning.
    /// </summary>
    internal static RenderableSubmesh? ExtractBsTriShape(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        Matrix4x4 transform,
        string? shapeName = null)
    {
        var parsed = NifSceneGraphBlockReader.ParseBsTriShape(data, block, bsVersion, be);
        if (parsed is not { } info || info.NumVertices == 0 || info.NumTriangles == 0)
        {
            return null;
        }

        var vertexSize = (int)(info.VertexDesc & 0xF) * 4;

        // Vertex Attributes occupy the high 12 bits of the descriptor (see nif.xml BSVertexDesc).
        var attributes = (info.VertexDesc >> 44) & 0xFFF;
        var hasNormal = (attributes & 0x8) != 0;
        var hasUv = (attributes & 0x2) != 0;
        var hasColor = (attributes & 0x20) != 0;

        // Skyrim SE stores full-precision float3 positions; Fallout 4 uses half3 unless the
        // Full_Precision attribute (0x400) is set.
        var fullPrecisionPos = bsVersion < 130 || (attributes & 0x400) != 0;

        // Per-attribute byte offsets within the vertex (descriptor nibbles, in 4-byte units).
        var uvOffset = (int)((info.VertexDesc >> 8) & 0xF) * 4;
        var normalOffset = (int)((info.VertexDesc >> 16) & 0xF) * 4;
        var colorOffset = (int)((info.VertexDesc >> 24) & 0xF) * 4;

        var positions = new float[info.NumVertices * 3];
        var normals = hasNormal ? new float[info.NumVertices * 3] : null;
        var uvs = hasUv ? new float[info.NumVertices * 2] : null;
        var colors = hasColor ? new byte[info.NumVertices * 4] : null;

        for (var v = 0; v < info.NumVertices; v++)
        {
            var vbase = info.VertexBufferOffset + v * vertexSize;

            if (fullPrecisionPos)
            {
                positions[v * 3] = BinaryUtils.ReadFloat(data, vbase, be);
                positions[v * 3 + 1] = BinaryUtils.ReadFloat(data, vbase + 4, be);
                positions[v * 3 + 2] = BinaryUtils.ReadFloat(data, vbase + 8, be);
            }
            else
            {
                positions[v * 3] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, vbase, be));
                positions[v * 3 + 1] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, vbase + 2, be));
                positions[v * 3 + 2] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, vbase + 4, be));
            }

            if (normals != null)
            {
                // ByteVector3, [0,255] mapped to [-1,1].
                var n = vbase + normalOffset;
                normals[v * 3] = data[n] / 127.5f - 1f;
                normals[v * 3 + 1] = data[n + 1] / 127.5f - 1f;
                normals[v * 3 + 2] = data[n + 2] / 127.5f - 1f;
            }

            if (uvs != null)
            {
                var u = vbase + uvOffset;
                uvs[v * 2] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, u, be));
                uvs[v * 2 + 1] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, u + 2, be));
            }

            if (colors != null)
            {
                // ByteColor4 — already RGBA bytes, matching RenderableSubmesh.VertexColors.
                var c = vbase + colorOffset;
                colors[v * 4] = data[c];
                colors[v * 4 + 1] = data[c + 1];
                colors[v * 4 + 2] = data[c + 2];
                colors[v * 4 + 3] = data[c + 3];
            }
        }

        var triangles = new ushort[info.NumTriangles * 3];
        for (var i = 0; i < triangles.Length; i++)
        {
            triangles[i] = BinaryUtils.ReadUInt16(data, info.TriangleBufferOffset + i * 2, be);
        }

        var transformed = ApplySkinningOrTransform(positions, normals, null, null, transform, null, false, shapeName);

        return new RenderableSubmesh
        {
            Positions = transformed.Positions,
            Triangles = triangles,
            Normals = transformed.Normals
                      ?? NifGeometryTransformUtils.RecomputeSmoothNormals(transformed.Positions, triangles),
            UVs = uvs,
            VertexColors = colors,
            Tangents = transformed.Tangents,
            Bitangents = transformed.Bitangents,
            BindPosePositions = null
        };
    }

    private static (
        float[] Positions,
        float[]? Normals,
        float[]? Tangents,
        float[]? Bitangents) ApplySkinningOrTransform(
            float[] positions,
            float[]? normals,
            float[]? tangents,
            float[]? bitangents,
            Matrix4x4 transform,
            ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning,
            bool useDualQuaternionSkinning,
            string? shapeName)
    {
        if (!skinning.HasValue)
        {
            return (
                NifGeometryTransformUtils.TransformPositions(positions, transform),
                normals != null
                    ? NifGeometryTransformUtils.TransformNormals(normals, transform)
                    : null,
                tangents != null
                    ? NifGeometryTransformUtils.TransformNormals(tangents, transform)
                    : null,
                bitangents != null
                    ? NifGeometryTransformUtils.TransformNormals(bitangents, transform)
                    : null);
        }

        var (perVertexInfluences, boneSkinMatrices) = skinning.Value;
        if (useDualQuaternionSkinning)
        {
            var compatibility = NifSkinningExtractor.AnalyzeDualQuaternionCompatibility(
                boneSkinMatrices);
            if (!compatibility.CanUse)
            {
                Log.Debug(
                    "Submesh '{0}': using linear skinning instead of DQS; matrix {1} is non-rigid (scale={2:F3}/{3:F3}/{4:F3}, axisDot={5:F3}, det={6:F3})",
                    shapeName ?? "(unnamed)",
                    compatibility.MatrixIndex,
                    compatibility.ScaleX,
                    compatibility.ScaleY,
                    compatibility.ScaleZ,
                    compatibility.MaxAxisDot,
                    compatibility.Determinant);
            }
            else
            {
                return (
                    NifSkinningExtractor.ApplySkinningPositionsDQS(
                        positions,
                        perVertexInfluences,
                        boneSkinMatrices),
                    normals != null
                        ? NifSkinningExtractor.ApplySkinningNormalsDQS(
                            normals,
                            perVertexInfluences,
                            boneSkinMatrices)
                        : null,
                    tangents != null
                        ? NifSkinningExtractor.ApplySkinningNormalsDQS(
                            tangents,
                            perVertexInfluences,
                            boneSkinMatrices)
                        : null,
                    bitangents != null
                        ? NifSkinningExtractor.ApplySkinningNormalsDQS(
                            bitangents,
                            perVertexInfluences,
                            boneSkinMatrices)
                        : null);
            }
        }

        return (
            NifSkinningExtractor.ApplySkinningPositions(
                positions,
                perVertexInfluences,
                boneSkinMatrices),
            normals != null
                ? NifSkinningExtractor.ApplySkinningNormals(
                    normals,
                    perVertexInfluences,
                    boneSkinMatrices)
                : null,
            tangents != null
                ? NifSkinningExtractor.ApplySkinningNormals(
                    tangents,
                    perVertexInfluences,
                    boneSkinMatrices)
                : null,
            bitangents != null
                ? NifSkinningExtractor.ApplySkinningNormals(
                    bitangents,
                    perVertexInfluences,
                    boneSkinMatrices)
                : null);
    }
}
