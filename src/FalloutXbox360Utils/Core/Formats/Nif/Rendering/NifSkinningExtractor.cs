using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Facade for skinning-related extraction logic.
/// </summary>
internal static class NifSkinningExtractor
{
    internal static Dictionary<int, ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)>
        BuildShapeSkinningData(
            byte[] data,
            NifInfo nif,
            Dictionary<int, int> shapeSkinInstanceMap,
            Dictionary<int, int> shapeDataMap,
            Dictionary<int, Matrix4x4> worldTransforms,
            Dictionary<string, Matrix4x4>? externalBoneTransforms = null,
            Dictionary<string, Matrix4x4>? externalPoseDeltas = null)
        => NifShapeSkinningDataBuilder.Build(
            data,
            nif,
            shapeSkinInstanceMap,
            shapeDataMap,
            worldTransforms,
            externalBoneTransforms,
            externalPoseDeltas);

    internal static Matrix4x4 ParseNiTransform(byte[] data, int pos, bool be)
        => NifSkinBlockParser.ParseNiTransform(data, pos, be);

    internal static NifSkinInstanceData? ParseNiSkinInstance(
        byte[] data,
        BlockInfo block,
        bool be)
        => NifSkinBlockParser.ParseNiSkinInstance(data, block, be);

    internal static NifSkinData? ParseNiSkinData(byte[] data, BlockInfo block, bool be)
        => NifSkinBlockParser.ParseNiSkinData(data, block, be);

    internal static (int BoneIdx, float Weight)[][] BuildPerVertexInfluences(
        NifSkinData skinData,
        int numVertices)
        => NifSkinInfluenceBuilder.BuildPerVertexInfluences(skinData, numVertices);

    internal static (int BoneIdx, float Weight)[][]? BuildPerVertexInfluencesFromPartitions(
        byte[] data,
        NifInfo nif,
        NifSkinInstanceData skinInstance,
        int numVertices,
        out bool hasExpandedData)
        => NifSkinInfluenceBuilder.BuildPerVertexInfluencesFromPartitions(
            data,
            nif,
            skinInstance,
            numVertices,
            out hasExpandedData);

    internal static float[] ApplySkinningPositions(
        float[] positions,
        (int BoneIdx, float Weight)[][] perVertexInfluences,
        Matrix4x4[] boneSkinMatrices)
        => NifSkinningMath.ApplySkinningPositions(
            positions,
            perVertexInfluences,
            boneSkinMatrices);

    internal static float[] ApplySkinningNormals(
        float[] normals,
        (int BoneIdx, float Weight)[][] perVertexInfluences,
        Matrix4x4[] boneSkinMatrices)
        => NifSkinningMath.ApplySkinningNormals(
            normals,
            perVertexInfluences,
            boneSkinMatrices);

    internal static float[] ApplySkinningPositionsDQS(
        float[] positions,
        (int BoneIdx, float Weight)[][] perVertexInfluences,
        Matrix4x4[] boneSkinMatrices)
        => NifSkinningMath.ApplySkinningPositionsDqs(
            positions,
            perVertexInfluences,
            boneSkinMatrices);

    internal static float[] ApplySkinningNormalsDQS(
        float[] normals,
        (int BoneIdx, float Weight)[][] perVertexInfluences,
        Matrix4x4[] boneSkinMatrices)
        => NifSkinningMath.ApplySkinningNormalsDqs(
            normals,
            perVertexInfluences,
            boneSkinMatrices);
}
