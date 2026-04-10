using System.Numerics;
using FalloutXbox360Utils.CLI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal sealed class NpcWeaponCompositionPlan
{
    public required WeaponVisual WeaponVisual { get; init; }

    public Matrix4x4? MainAttachmentTransform { get; init; }

    public string? AttachmentNodeName { get; init; }

    public string? AttachmentSourceLabel { get; init; }

    public string? AttachmentOmitReason { get; init; }

    public NpcWeaponAttachmentResolver.WeaponHolsterPose? HolsterPose { get; init; }

    public HashSet<int>? MainWeaponExcludedShapes { get; init; }

    public List<NifSceneGraphWalker.ParentBoneShapeGroup> HolsterAttachmentGroups { get; init; } = [];

    public Dictionary<string, NifAnimationParser.AnimPoseOverride>? HolsterModelPoseOverrides { get; init; }

    public bool RenderOnlyExplicitHolsterAttachmentGroups { get; init; }

    public bool UseSkinnedMainWeaponWhenPossible { get; init; }

    public IReadOnlyList<WeaponAddonVisual> AddonMeshes { get; init; } = [];
}
