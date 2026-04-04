using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal sealed class NpcCompositionCaches
{
    public NpcCompositionCaches()
        : this(
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, CachedNpcSkeletonPlan?>(StringComparer.OrdinalIgnoreCase))
    {
    }

    public NpcCompositionCaches(
        Dictionary<string, EgmParser?> egmFiles,
        Dictionary<string, EgtParser?> egtFiles,
        Dictionary<string, CachedNpcSkeletonPlan?> skeletonPlans)
    {
        EgmFiles = egmFiles;
        EgtFiles = egtFiles;
        SkeletonPlans = skeletonPlans;
    }

    public Dictionary<string, EgmParser?> EgmFiles { get; }

    public Dictionary<string, EgtParser?> EgtFiles { get; }

    public Dictionary<string, CachedNpcSkeletonPlan?> SkeletonPlans { get; }

    internal sealed record CachedNpcSkeletonPlan(
        string SkeletonNifPath,
        Dictionary<string, Matrix4x4> BodySkinningBones,
        Dictionary<string, Matrix4x4> PoseDeltas,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? AnimationOverrides);
}
