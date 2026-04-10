using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
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
