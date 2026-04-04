using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal sealed class CreatureCompositionPlan
{
    public required CreatureScanEntry Creature { get; init; }

    public required CreatureCompositionOptions Options { get; init; }

    public required string SkeletonNifPath { get; init; }

    public required string[] BodyModelPaths { get; init; }

    public Dictionary<string, Matrix4x4>? BoneTransforms { get; init; }

    public Dictionary<string, NifAnimationParser.AnimPoseOverride>? AnimationOverrides { get; init; }

    public Matrix4x4? HeadAttachmentTransform { get; init; }

    public Matrix4x4? WeaponAttachmentTransform { get; init; }

    public string? WeaponMeshPath { get; init; }
}
