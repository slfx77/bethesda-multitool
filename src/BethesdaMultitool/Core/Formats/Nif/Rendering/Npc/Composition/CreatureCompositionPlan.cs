using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>
///     A resolved plan for composing a creature: its scanned record, options, and the meshes/transforms/animation to
///     assemble.
/// </summary>
internal sealed class CreatureCompositionPlan
{
    internal BethesdaMultitool.Core.Assets.AssetUseGraph AssetUses { get; set; } = BethesdaMultitool.Core.Assets.AssetUseGraph.Empty;
    public required CreatureScanEntry Creature { get; init; }

    public required CreatureCompositionOptions Options { get; init; }

    public required string SkeletonNifPath { get; init; }

    public required string[] BodyModelPaths { get; init; }

    public Dictionary<string, Matrix4x4>? BoneTransforms { get; init; }

    public Dictionary<string, NifAnimationParser.AnimPoseOverride>? AnimationOverrides { get; init; }

    /// <summary>
    ///     Exact virtual path of the KF that supplied <see cref="AnimationOverrides" />, or a
    ///     skeleton-fragment label when the pose came from embedded controller data.
    /// </summary>
    public string? AnimationSourcePath { get; init; }

    public Matrix4x4? HeadAttachmentTransform { get; init; }

    public Matrix4x4? WeaponAttachmentTransform { get; init; }

    public string? WeaponMeshPath { get; init; }
}
