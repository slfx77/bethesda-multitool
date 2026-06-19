using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>Reusable caches shared across NPC composition passes: parsed EGM/EGT morph files and resolved skeleton plans, keyed by path.</summary>
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

    /// <summary>A cached resolved skeleton for an NPC: its skeleton NIF path, body skinning bones, pose deltas, and animation overrides.</summary>
    internal sealed record CachedNpcSkeletonPlan(
        string SkeletonNifPath,
        Dictionary<string, Matrix4x4> BodySkinningBones,
        Dictionary<string, Matrix4x4> PoseDeltas,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? AnimationOverrides);
}
