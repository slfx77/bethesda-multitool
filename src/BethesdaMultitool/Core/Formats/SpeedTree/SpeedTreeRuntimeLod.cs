using System.Numerics;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>The independently drawable part of one generated SpeedTree runtime LOD.</summary>
internal enum SpeedTreeLodComponent : byte
{
    Branch,
    Leaf,
    Billboard,
}

/// <summary>
///     Explicit runtime-LOD identity carried with generated <c>.spt</c> geometry. Branch levels are
///     numbered <c>0..BranchLevelCount-1</c>; the far billboard is level
///     <see cref="BillboardLevel" />. Multiple component submeshes may share a level, but the renderer
///     selects exactly one level for each placement.
/// </summary>
internal readonly record struct SpeedTreeLodMetadata(
    int Level,
    int BranchLevelCount,
    float NearDistance,
    float FarDistance,
    SpeedTreeLodComponent Component)
{
    public int BillboardLevel => BranchLevelCount;

    public bool IsValid => BranchLevelCount > 0 && Level >= 0 && Level <= BillboardLevel &&
                           float.IsFinite(NearDistance) && float.IsFinite(FarDistance) &&
                           FarDistance > NearDistance;
}

/// <summary>
///     Pure policy for the temporary opt-in SpeedTree runtime-LOD path. The branch keep fractions are
///     recovered from <c>CTreeEngine::BuildBranchLods</c>. Leaf reduction and the viewer's distance
///     limits remain deliberately isolated architectural approximations until the retail binary oracle
///     for Bethesda's host-side LOD policy is recovered.
/// </summary>
internal static class SpeedTreeRuntimeLod
{
    internal const string EnvironmentVariable = "FALLOUT_VIEWER_SPT_RUNTIME_LOD";

    // Host-side screen-size limits are not stored in the .spt. These conservative model-radius
    // multiples keep full geometry through the near field and introduce the imposter in the far field.
    // Keeping them here (rather than in renderer heuristics) makes the approximation explicit/testable.
    private const float NearRadiusMultiplier = 4f;
    private const float FarRadiusMultiplier = 16f;
    private const float MinimumNearDistance = 256f;
    private const float MinimumTransitionWidth = 256f;

    public static bool Enabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return raw is not null &&
                   (raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("on", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    ///     Select a branch LOD or the far billboard. At/below <paramref name="nearDistance" /> the
    ///     result is LOD0; at/above <paramref name="farDistance" /> it is the billboard sentinel
    ///     <paramref name="branchLevelCount" />. Every finite distance maps to exactly one level.
    /// </summary>
    public static int SelectLevel(
        float distance,
        int branchLevelCount,
        float nearDistance,
        float farDistance)
    {
        branchLevelCount = Math.Max(1, branchLevelCount);
        if (float.IsNaN(distance) || distance <= nearDistance)
        {
            return 0;
        }

        if (float.IsPositiveInfinity(distance) || distance >= farDistance || farDistance <= nearDistance)
        {
            return branchLevelCount;
        }

        var t = Math.Clamp((distance - nearDistance) / (farDistance - nearDistance), 0f, 1f);
        return Math.Min(branchLevelCount - 1, (int)(t * branchLevelCount));
    }

    /// <summary>Placement-scaled selector used by the renderer's per-instance batch filter.</summary>
    public static int SelectLevelForPlacement(
        float distance,
        float uniformScale,
        in SpeedTreeLodMetadata metadata)
    {
        uniformScale = float.IsFinite(uniformScale) && uniformScale > 1e-6f ? uniformScale : 1f;
        return SelectLevel(
            distance,
            metadata.BranchLevelCount,
            metadata.NearDistance * uniformScale,
            metadata.FarDistance * uniformScale);
    }

    /// <summary>
    ///     Exact <c>BuildBranchLods</c> authored interpolation:
    ///     <c>near + (far-near) * level/(count-1)</c>. The division is performed in double then
    ///     narrowed, matching the recovered Xbox implementation.
    /// </summary>
    public static float BranchKeepFraction(SptLodInfo lod, int level)
    {
        var count = Math.Max(1, lod.NumBranchLods);
        if (count < 2)
        {
            return 1f;
        }

        level = Math.Clamp(level, 0, count - 1);
        var t = (float)((double)level / (count - 1));
        return Math.Clamp(
            lod.BranchNearFraction + (lod.BranchFarFraction - lod.BranchNearFraction) * t,
            0f,
            1f);
    }

    /// <summary>
    ///     Maps the branch transition sequence onto the authored number of leaf levels. The real SDK
    ///     pair-merges nearby cards; until that binary oracle is ported, each mapped level uses a stable
    ///     power-of-two subset and the authored size-increase scalar.
    /// </summary>
    public static int MapLeafLevel(int branchLevel, int branchLevelCount, int leafLevelCount)
    {
        branchLevelCount = Math.Max(1, branchLevelCount);
        leafLevelCount = Math.Max(1, leafLevelCount);
        if (branchLevelCount == 1 || leafLevelCount == 1)
        {
            return 0;
        }

        branchLevel = Math.Clamp(branchLevel, 0, branchLevelCount - 1);
        return (int)((long)branchLevel * (leafLevelCount - 1) / (branchLevelCount - 1));
    }

    public static bool KeepApproximateLeaf(int sourceIndex, int leafLevel)
    {
        var shift = Math.Clamp(leafLevel, 0, 20);
        var stride = 1 << shift;
        return sourceIndex >= 0 && sourceIndex % stride == 0;
    }

    public static float ApproximateLeafSizeScale(SptLodInfo lod, int leafLevel) =>
        MathF.Max(0.05f, 1f + Math.Max(0, leafLevel) * lod.LeafLodSizeIncrease);

    /// <summary>Build model-local near/far limits and the placement-scale reference radius.</summary>
    public static (float NearDistance, float FarDistance, float ReferenceRadius) ComputeDistanceRange(
        Vector3 min,
        Vector3 max)
    {
        var farthestAabbCorner = new Vector3(
            MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X)),
            MathF.Max(MathF.Abs(min.Y), MathF.Abs(max.Y)),
            MathF.Max(MathF.Abs(min.Z), MathF.Abs(max.Z)));
        var referenceRadius = farthestAabbCorner.Length();
        if (!float.IsFinite(referenceRadius) || referenceRadius < 1f)
        {
            referenceRadius = 1f;
        }

        var near = MathF.Max(MinimumNearDistance, referenceRadius * NearRadiusMultiplier);
        var far = MathF.Max(near + MinimumTransitionWidth, referenceRadius * FarRadiusMultiplier);
        return (near, far, referenceRadius);
    }

    public static string BillboardTexturePath(string modelPath)
    {
        var normalized = modelPath.Replace('/', '\\').Trim();
        var stem = Path.GetFileNameWithoutExtension(normalized);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "tree";
        }

        return $"textures\\trees\\billboards\\{stem.ToLowerInvariant()}.dds";
    }
}
