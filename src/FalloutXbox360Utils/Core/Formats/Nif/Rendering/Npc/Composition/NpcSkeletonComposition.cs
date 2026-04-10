using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal sealed class NpcSkeletonComposition
{
    public required string SkeletonNifPath { get; init; }

    public Dictionary<string, Matrix4x4>? BodySkinningBones { get; init; }

    public Dictionary<string, Matrix4x4>? WeaponAttachmentBones { get; init; }

    public Dictionary<string, Matrix4x4>? PoseDeltas { get; init; }

    public Dictionary<string, NifAnimationParser.AnimPoseOverride>? AnimationOverrides { get; init; }
}
