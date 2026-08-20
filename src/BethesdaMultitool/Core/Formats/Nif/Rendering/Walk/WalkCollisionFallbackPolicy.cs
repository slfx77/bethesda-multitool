using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     Decides when placed-reference collision may use speculative object bounds or visual triangle
///     soup. Presentation-only geometry is never solid merely because it lacks authored Havok.
/// </summary>
internal static class WalkCollisionFallbackPolicy
{
    public static bool AllowsObjectBoundsFallback(
        string? modelPath, PlacedObjectCategory category = PlacedObjectCategory.Unknown)
    {
        return !IsEffectModel(modelPath, category) &&
               !IsVegetation(category) &&
               !IsSpeedTreeModel(modelPath);
    }

    /// <summary>
    ///     Source-aware gate used when a cached mesh is resolved for a particular placement. Authored
    ///     Havok and authoritative no-collision results are never rejected here; only synthesized
    ///     visual soup is subject to placement/path policy.
    /// </summary>
    public static bool AllowsResolvedCollisionMesh(
        CollisionMeshSource source,
        string? modelPath,
        PlacedObjectCategory category)
    {
        return source != CollisionMeshSource.VisualFallback ||
               AllowsVisualMeshFallback(modelPath, category);
    }

    /// <summary>
    ///     True for a Gamebryo SpeedTree recipe (<c>.spt</c>). Its geometry is generated entirely from
    ///     billboard leaf cards and frond strips — presentation, never a walk surface — and the
    ///     placement's ESM category cannot be relied on to say so (a .spt may be placed as a plain
    ///     static). Path-gated so it holds for every placement of the model.
    /// </summary>
    public static bool IsSpeedTreeModel(string? modelPath)
    {
        return modelPath is not null &&
               modelPath.EndsWith(".spt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Viewer policy treats Plants and Tree placements as walk-through UNLESS they ship authored
    ///     Havok — a shrub/cactus/tree without collision in its NIF must not become an invisible wall.
    ///     Our synthesized fallbacks (render-mesh triangle soup + speculative OBND box) would otherwise
    ///     turn a plant's alpha-tested foliage cards and trunk into collision, so vegetation is excluded
    ///     from BOTH. Authored Havok is checked first by the collision cache and stays
    ///     authoritative, so a plant that genuinely ships collision is still solid.
    /// </summary>
    public static bool IsVegetation(PlacedObjectCategory category)
    {
        return category is PlacedObjectCategory.Plants or PlacedObjectCategory.Tree;
    }

    /// <summary>
    ///     Visual geometry under the effects folder is presentation, not an inferred walk surface.
    ///     Authored Havok is checked before this policy and remains authoritative.
    /// </summary>
    public static bool AllowsVisualMeshFallback(
        string? modelPath,
        PlacedObjectCategory category = PlacedObjectCategory.Unknown)
    {
        return !IsEffectModel(modelPath, category) &&
               !IsVegetation(category) &&
               !IsSpeedTreeModel(modelPath);
    }

    /// <summary>
    ///     True for an explicit effect-category placement, a missing model path, or a model stored
    ///     beneath the effects folder. Missing paths cannot provide a safe speculative fallback.
    /// </summary>
    public static bool IsEffectModel(
        string? modelPath,
        PlacedObjectCategory category = PlacedObjectCategory.Unknown)
    {
        if (category == PlacedObjectCategory.Effects || string.IsNullOrWhiteSpace(modelPath))
        {
            return true;
        }

        var normalized = modelPath.Replace('/', '\\').TrimStart('\\');
        if (normalized.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return normalized.StartsWith("effects\\", StringComparison.OrdinalIgnoreCase);
    }
}
