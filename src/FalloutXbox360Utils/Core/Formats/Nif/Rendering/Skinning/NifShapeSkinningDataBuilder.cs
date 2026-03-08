using System.Numerics;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Coordinates parsing and assembly of per-shape skinning data.
/// </summary>
internal static class NifShapeSkinningDataBuilder
{
    private static readonly Logger Log = Logger.Instance;

    internal static Dictionary<int, ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)>
        Build(
            byte[] data,
            NifInfo nif,
            Dictionary<int, int> shapeSkinInstanceMap,
            Dictionary<int, int> shapeDataMap,
            Dictionary<int, Matrix4x4> worldTransforms,
            Dictionary<string, Matrix4x4>? externalBoneTransforms = null,
            Dictionary<string, Matrix4x4>? externalPoseDeltas = null)
    {
        var result = new Dictionary<int, ((int, float)[][], Matrix4x4[])>();
        var be = nif.IsBigEndian;

        Log.Debug(
            $"  BuildShapeSkinningData: {shapeSkinInstanceMap.Count} skinned shapes, isBigEndian={be}");

        foreach (var (shapeIndex, skinInstanceBlockIndex) in shapeSkinInstanceMap)
        {
            var skinInstance = TryParseSkinInstance(data, nif, skinInstanceBlockIndex, be);
            if (skinInstance == null ||
                !TryParseSkinData(data, nif, skinInstance.DataRef, be, out var skinData) ||
                skinData == null)
            {
                continue;
            }

            var boneSkinMatrices = BuildBoneSkinMatrices(
                data,
                nif,
                skinInstance,
                skinData,
                worldTransforms,
                externalBoneTransforms,
                externalPoseDeltas,
                be);
            if (boneSkinMatrices == null)
            {
                continue;
            }

            if (!shapeDataMap.TryGetValue(shapeIndex, out var dataIndex))
            {
                continue;
            }

            var numVertices = NifBlockParsers.ReadVertexCount(
                data,
                nif.Blocks[dataIndex],
                be);
            if (numVertices <= 0)
            {
                continue;
            }

            var influences = BuildInfluences(
                data,
                nif,
                shapeIndex,
                skinInstance,
                skinData,
                numVertices);
            if (influences == null)
            {
                continue;
            }

            result[shapeIndex] = (influences, boneSkinMatrices);
        }

        return result;
    }

    private static NifSkinInstanceData? TryParseSkinInstance(
        byte[] data,
        NifInfo nif,
        int skinInstanceBlockIndex,
        bool be)
    {
        var skinBlock = nif.Blocks[skinInstanceBlockIndex];
        return NifSkinBlockParser.ParseNiSkinInstance(data, skinBlock, be);
    }

    private static bool TryParseSkinData(
        byte[] data,
        NifInfo nif,
        int dataRef,
        bool be,
        out NifSkinData? skinData)
    {
        skinData = null;
        if (dataRef < 0 || dataRef >= nif.Blocks.Count)
        {
            return false;
        }

        var skinDataBlock = nif.Blocks[dataRef];
        if (skinDataBlock.TypeName != "NiSkinData")
        {
            return false;
        }

        skinData = NifSkinBlockParser.ParseNiSkinData(data, skinDataBlock, be);
        return skinData?.Bones.Length > 0;
    }

    private static Matrix4x4[]? BuildBoneSkinMatrices(
        byte[] data,
        NifInfo nif,
        NifSkinInstanceData skinInstance,
        NifSkinData skinData,
        Dictionary<int, Matrix4x4> worldTransforms,
        Dictionary<string, Matrix4x4>? externalBoneTransforms,
        Dictionary<string, Matrix4x4>? externalPoseDeltas,
        bool be)
    {
        var boneSkinMatrices = new Matrix4x4[skinData.Bones.Length];
        for (var boneIndex = 0; boneIndex < skinData.Bones.Length; boneIndex++)
        {
            if (boneIndex >= skinInstance.BoneRefs.Length)
            {
                return null;
            }

            var boneWorldTransform = NifBoneTransformResolver.ResolveWorldTransform(
                data,
                nif,
                skinInstance.BoneRefs[boneIndex],
                worldTransforms,
                externalBoneTransforms,
                externalPoseDeltas,
                be);
            boneSkinMatrices[boneIndex] =
                skinData.Bones[boneIndex].InverseBindPose * boneWorldTransform;
        }

        return boneSkinMatrices;
    }

    private static (int BoneIdx, float Weight)[][]? BuildInfluences(
        byte[] data,
        NifInfo nif,
        int shapeIndex,
        NifSkinInstanceData skinInstance,
        NifSkinData skinData,
        int numVertices)
    {
        if (skinData.HasVertexWeights)
        {
            Log.Debug(
                $"    Shape {shapeIndex}: HasVertexWeights=true, using NiSkinData path ({skinData.Bones.Length} bones, {numVertices} verts)");
            var influences = NifSkinInfluenceBuilder.BuildPerVertexInfluences(
                skinData,
                numVertices);
            LogInfluenceStats(shapeIndex, influences, numVertices);
            return influences;
        }

        Log.Debug(
            $"    Shape {shapeIndex}: HasVertexWeights=false, falling back to NiSkinPartition path ({skinData.Bones.Length} bones, {numVertices} verts)");
        var partitionInfluences =
            NifSkinInfluenceBuilder.BuildPerVertexInfluencesFromPartitions(
                data,
                nif,
                skinInstance,
                numVertices,
                out _);
        if (partitionInfluences == null)
        {
            Log.Debug($"    Shape {shapeIndex}: partition fallback returned null, skipping");
        }

        return partitionInfluences;
    }

    private static void LogInfluenceStats(
        int shapeIndex,
        (int BoneIdx, float Weight)[][] influences,
        int numVertices)
    {
        var verticesWithInfluences = 0;
        var maxInfluences = 0;
        for (var vertexIndex = 0; vertexIndex < influences.Length; vertexIndex++)
        {
            var count = influences[vertexIndex].Length;
            if (count > 0)
            {
                verticesWithInfluences++;
            }

            if (count > maxInfluences)
            {
                maxInfluences = count;
            }
        }

        Log.Debug(
            $"    Shape {shapeIndex}: {verticesWithInfluences}/{numVertices} verts have influences (max {maxInfluences} per vert)");
    }
}
