using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>
///     The skeleton portion of an NPC composition plan: the skeleton NIF, its bone transforms, and any animation pose
///     overrides.
/// </summary>
internal sealed class NpcSkeletonComposition
{
    public required string SkeletonNifPath { get; init; }

    public Dictionary<string, Matrix4x4>? BodySkinningBones { get; init; }

    public Dictionary<string, Matrix4x4>? WeaponAttachmentBones { get; init; }

    public Dictionary<string, Matrix4x4>? PoseDeltas { get; init; }

    public Dictionary<string, NifAnimationParser.AnimPoseOverride>? AnimationOverrides { get; init; }
}
