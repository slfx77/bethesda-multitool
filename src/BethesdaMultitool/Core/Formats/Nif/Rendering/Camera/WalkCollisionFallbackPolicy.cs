using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Decides when walk mode may temporarily approximate a cold mesh with its object-bounds box.
///     Visual effects are never solid merely because their render mesh has not produced collision yet.
/// </summary>
internal static class WalkCollisionFallbackPolicy
{
    public static bool AllowsObjectBoundsFallback(
        string? modelPath, PlacedObjectCategory category = PlacedObjectCategory.Unknown)
    {
        return !IsEffectModel(modelPath, category) && !IsVegetation(category);
    }

    /// <summary>
    ///     Plants and trees are walk-through in the retail engine UNLESS they ship authored Havok — a
    ///     shrub/cactus/tree without collision in its NIF must not become an invisible wall. Our
    ///     synthesized fallbacks (render-mesh triangle soup + speculative OBND box) would otherwise turn
    ///     a plant's alpha-tested foliage cards and trunk into collision, so vegetation is excluded from
    ///     BOTH. Authored Havok is checked first (see <see cref="CollisionMeshBuilder.Build" />) and stays
    ///     authoritative, so a plant that genuinely ships collision is still solid.
    /// </summary>
    public static bool IsVegetation(PlacedObjectCategory category)
        => category is PlacedObjectCategory.Plants or PlacedObjectCategory.Tree;

    /// <summary>
    ///     Visual geometry under the effects folder is presentation, not an inferred walk surface.
    ///     Authored Havok is checked before this policy and remains authoritative.
    /// </summary>
    public static bool AllowsVisualMeshFallback(string? modelPath)
        => !IsEffectModel(modelPath);

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
