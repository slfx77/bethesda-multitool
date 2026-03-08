using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Resolves the effective world transform for a skinning bone.
/// </summary>
internal static class NifBoneTransformResolver
{
    internal static Matrix4x4 ResolveWorldTransform(
        byte[] data,
        NifInfo nif,
        int boneNodeIndex,
        Dictionary<int, Matrix4x4> worldTransforms,
        Dictionary<string, Matrix4x4>? externalBoneTransforms,
        Dictionary<string, Matrix4x4>? externalPoseDeltas,
        bool be)
    {
        if (externalPoseDeltas != null)
        {
            var boneWorldTransform = ResolveLocalWorldTransform(
                data,
                nif,
                boneNodeIndex,
                worldTransforms,
                be);
            if (TryGetBoneName(data, nif, boneNodeIndex, out var boneName) &&
                externalPoseDeltas.TryGetValue(boneName, out var delta))
            {
                boneWorldTransform *= delta;
            }

            return boneWorldTransform;
        }

        if (externalBoneTransforms != null &&
            TryGetBoneName(data, nif, boneNodeIndex, out var externalBoneName) &&
            externalBoneTransforms.TryGetValue(externalBoneName, out var skeletonTransform))
        {
            return skeletonTransform;
        }

        return ResolveLocalWorldTransform(data, nif, boneNodeIndex, worldTransforms, be);
    }

    private static bool TryGetBoneName(
        byte[] data,
        NifInfo nif,
        int boneNodeIndex,
        out string boneName)
    {
        boneName = string.Empty;
        if (boneNodeIndex < 0 || boneNodeIndex >= nif.Blocks.Count)
        {
            return false;
        }

        boneName = NifBlockParsers.ReadBlockName(
            data,
            nif.Blocks[boneNodeIndex],
            nif) ?? string.Empty;
        return boneName.Length > 0;
    }

    private static Matrix4x4 ResolveLocalWorldTransform(
        byte[] data,
        NifInfo nif,
        int boneNodeIndex,
        Dictionary<int, Matrix4x4> worldTransforms,
        bool be)
    {
        if (worldTransforms.TryGetValue(boneNodeIndex, out var worldTransform))
        {
            return worldTransform;
        }

        if (boneNodeIndex < 0 || boneNodeIndex >= nif.Blocks.Count)
        {
            return Matrix4x4.Identity;
        }

        return NifBlockParsers.ParseNiAVObjectTransform(
            data,
            nif.Blocks[boneNodeIndex],
            nif.BsVersion,
            be);
    }
}
