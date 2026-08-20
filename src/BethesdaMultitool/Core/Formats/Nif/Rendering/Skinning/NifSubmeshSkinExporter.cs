using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Builds <see cref="NifSubmeshSkin" /> blocks (keyed by shape block index) for a NIF whose
///     animation rig was collected — the CPU-skinning inputs the viewer re-poses per frame. Reuses
///     the existing skin parsers (<see cref="NifSkinBlockParser" /> / <see cref="NifSkinInfluenceBuilder" />)
///     and a <c>bindPoseOnly</c> geometry extraction for the raw skin-space base vertices; no new
///     binary parsing.
/// </summary>
internal static class NifSubmeshSkinExporter
{
    internal static Dictionary<int, NifSubmeshSkin>? Export(
        byte[] data, NifInfo nif, NifMeshAnimation animation)
    {
        var be = nif.IsBigEndian;

        // Shape → skin instance mapping via the walker (same classification the extractor uses).
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap,
            shapeSkinInstanceMap);
        if (shapeSkinInstanceMap.Count == 0)
        {
            return null;
        }

        // Raw skin-space base geometry: bindPoseOnly strips node transforms AND skinning, leaving
        // the shape data blocks' authored vertices — exactly the LBS input space.
        var bindPose = NifGeometryExtractor.Extract(data, nif, bindPoseOnly: true);
        if (bindPose is null)
        {
            return null;
        }

        // Skin-bone → anim-bone resolution: block index first (exact — the collectors record each
        // bone's source node block, and skin instances reference bones by block ref), name as the
        // fallback for rigs without source indices (cache round-trips, synthetic tests). Names
        // alone mis-map NIFs with duplicate node names (nv_ncr_flag_s ships two same-named chains).
        var animBoneByBlock = new Dictionary<int, int>(animation.Bones.Length);
        var animBoneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < animation.Bones.Length; i++)
        {
            if (animation.Bones[i].SourceBlockIndex >= 0)
            {
                animBoneByBlock.TryAdd(animation.Bones[i].SourceBlockIndex, i);
            }

            animBoneByName.TryAdd(animation.Bones[i].Name, i);
        }

        var result = new Dictionary<int, NifSubmeshSkin>();
        foreach (var sub in bindPose.Submeshes)
        {
            if (sub.SourceBlockIndex < 0 ||
                !shapeSkinInstanceMap.TryGetValue(sub.SourceBlockIndex, out var skinInstanceIndex))
            {
                continue;
            }

            var skinInstance = NifSkinBlockParser.ParseNiSkinInstance(
                data, nif.Blocks[skinInstanceIndex], be, nif.BinaryVersion);
            if (skinInstance is null ||
                skinInstance.DataRef < 0 || skinInstance.DataRef >= nif.Blocks.Count ||
                nif.Blocks[skinInstance.DataRef].TypeName != "NiSkinData")
            {
                continue;
            }

            var skinData = NifSkinBlockParser.ParseNiSkinData(
                data, nif.Blocks[skinInstance.DataRef], be, nif.BinaryVersion);
            if (skinData is null || skinData.Bones.Length == 0 || !skinData.HasVertexWeights)
            {
                continue;
            }

            var vertexCount = sub.Positions.Length / 3;
            var influences = NifSkinInfluenceBuilder.BuildPerVertexInfluences(skinData, vertexCount);

            // Pack top-4 influences per vertex, renormalized (TES3 authors ≤ 4 anyway).
            var boneIndices = new byte[vertexCount * 4];
            var boneWeights = new float[vertexCount * 4];
            for (var v = 0; v < vertexCount; v++)
            {
                var list = influences[v];
                var top = list.OrderByDescending(w => w.Weight).Take(4).ToArray();
                var total = top.Sum(w => w.Weight);
                if (total <= 0f)
                {
                    continue; // unweighted vertex: all-zero weights → skinner leaves it at base
                }

                for (var k = 0; k < top.Length; k++)
                {
                    boneIndices[v * 4 + k] = (byte)Math.Clamp(top[k].BoneIdx, 0, 255);
                    boneWeights[v * 4 + k] = top[k].Weight / total;
                }
            }

            var inverseBinds = new Matrix4x4[skinData.Bones.Length];
            var skinBoneToAnimBone = new int[skinData.Bones.Length];
            for (var b = 0; b < skinData.Bones.Length; b++)
            {
                inverseBinds[b] = skinData.Bones[b].InverseBindPose;
                var boneRef = b < skinInstance.BoneRefs.Length ? skinInstance.BoneRefs[b] : -1;
                if (animBoneByBlock.TryGetValue(boneRef, out var byBlock))
                {
                    skinBoneToAnimBone[b] = byBlock;
                    continue;
                }

                var boneName = boneRef >= 0 && boneRef < nif.Blocks.Count
                    ? NifBlockParsers.ReadBlockName(data, nif.Blocks[boneRef], nif)
                    : null;
                skinBoneToAnimBone[b] = boneName is not null &&
                                        animBoneByName.TryGetValue(boneName, out var animIdx)
                    ? animIdx
                    : -1;
            }

            result[sub.SourceBlockIndex] = new NifSubmeshSkin(
                sub.Positions,
                sub.Normals,
                inverseBinds,
                skinBoneToAnimBone,
                boneIndices,
                boneWeights);
        }

        return result.Count > 0 ? result : null;
    }
}
