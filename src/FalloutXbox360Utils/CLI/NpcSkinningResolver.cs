using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Skinning matrix computation, model attachment compensation, and transform utilities.
/// </summary>
internal static class NpcSkinningResolver
{
    internal static bool TryResolveModelAttachmentCompensation(
        byte[] data,
        NifInfo nif,
        string nodeName,
        out Matrix4x4 compensationTransform,
        out ModelAttachmentCompensationKind compensationKind,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        compensationTransform = Matrix4x4.Identity;
        compensationKind = ModelAttachmentCompensationKind.ExplicitAttachmentNode;

        var namedTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(data, nif, animOverrides);
        if (!namedTransforms.TryGetValue(nodeName, out var attachmentWorldTransform))
        {
            if (!NpcSkeletonLoader.TryReadNodeLocalTransform(data, nif, nodeName, out attachmentWorldTransform))
            {
                if (!namedTransforms.TryGetValue(NifGeometryExtractor.RootTransformKey, out attachmentWorldTransform) ||
                    IsNearlyIdentityTransform(attachmentWorldTransform))
                {
                    return false;
                }

                compensationKind = ModelAttachmentCompensationKind.RootFallback;
            }
        }

        return Matrix4x4.Invert(attachmentWorldTransform, out compensationTransform);
    }

    internal static bool TryResolveModelAttachmentCompensation(
        byte[] data,
        NifInfo nif,
        string nodeName,
        out Matrix4x4 compensationTransform,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        return TryResolveModelAttachmentCompensation(
            data,
            nif,
            nodeName,
            out compensationTransform,
            out _,
            animOverrides);
    }

    internal static bool ShouldApplyWeaponModelAttachmentCompensation(
        WeaponAttachmentMode attachmentMode,
        ModelAttachmentCompensationKind compensationKind)
    {
        return compensationKind == ModelAttachmentCompensationKind.ExplicitAttachmentNode ||
               attachmentMode != WeaponAttachmentMode.HolsterPose;
    }

    internal static HashSet<int> FindShapeBlockIndices(byte[] data, NifInfo nif)
    {
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap);
        return shapeDataMap.Keys.ToHashSet();
    }

    internal static bool TryResolveShapeGroupAttachmentCompensation(
        byte[] data,
        NifInfo nif,
        IReadOnlyCollection<int> shapeIndices,
        out Matrix4x4 compensationTransform,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        compensationTransform = Matrix4x4.Identity;
        if (shapeIndices.Count != 1)
        {
            return false;
        }

        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var worldTransforms = new Dictionary<int, Matrix4x4>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap);
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, worldTransforms, animOverrides);

        var shapeIndex = shapeIndices.First();
        if (!worldTransforms.TryGetValue(shapeIndex, out var shapeWorldTransform) ||
            IsNearlyIdentityTransform(shapeWorldTransform))
        {
            return false;
        }

        return Matrix4x4.Invert(shapeWorldTransform, out compensationTransform);
    }

    internal static bool IsNearlyIdentityTransform(Matrix4x4 transform)
    {
        return MathF.Abs(transform.M11 - 1f) < 0.0001f &&
               MathF.Abs(transform.M22 - 1f) < 0.0001f &&
               MathF.Abs(transform.M33 - 1f) < 0.0001f &&
               MathF.Abs(transform.M44 - 1f) < 0.0001f &&
               MathF.Abs(transform.M12) < 0.0001f &&
               MathF.Abs(transform.M13) < 0.0001f &&
               MathF.Abs(transform.M14) < 0.0001f &&
               MathF.Abs(transform.M21) < 0.0001f &&
               MathF.Abs(transform.M23) < 0.0001f &&
               MathF.Abs(transform.M24) < 0.0001f &&
               MathF.Abs(transform.M31) < 0.0001f &&
               MathF.Abs(transform.M32) < 0.0001f &&
               MathF.Abs(transform.M34) < 0.0001f &&
               MathF.Abs(transform.M41) < 0.0001f &&
               MathF.Abs(transform.M42) < 0.0001f &&
               MathF.Abs(transform.M43) < 0.0001f;
    }

    internal enum ModelAttachmentCompensationKind
    {
        ExplicitAttachmentNode,
        RootFallback
    }
}
