using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     Decides when walk mode may temporarily approximate a cold mesh with its object-bounds box.
///     Visual effects are never solid merely because their render mesh has not produced collision yet.
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
    ///     Gate for an already-RESOLVED warm collision mesh, applied at the placement site. The shared
    ///     mesh cache builds one ordinary entry per model path under
    ///     <see cref="PlacedObjectCategory.Unknown" />, so the vegetation rule inside
    ///     <see cref="CollisionMeshBuilder.Build" /> never sees the placement's real category and a
    ///     tree's synthesized canopy soup reached walk mode as solid ground — "walk mode can stand on
    ///     SPT leaves". Leaf cards re-face the camera every frame, so a surface built from one is a
    ///     floor that is not where it is drawn. Authored Havok is untouched and stays authoritative.
    /// </summary>
    public static bool AllowsResolvedCollisionMesh(
        CollisionMeshSource source,
        string? modelPath,
        PlacedObjectCategory category)
    {
        if (source != CollisionMeshSource.VisualFallback) return true;
        return !IsVegetation(category) && !IsSpeedTreeModel(modelPath);
    }

    /// <summary>
    ///     True for a Gamebryo SpeedTree recipe (<c>.spt</c>). Its geometry is generated entirely from
    ///     billboard leaf cards and frond strips — presentation, never a walk surface — and the
    ///     placement's ESM category cannot be relied on to say so (a .spt may be placed as a plain
    ///     static). Path-gated so it holds for every placement of the model.
    /// </summary>
    public static bool IsSpeedTreeModel(string? modelPath)
        => modelPath is not null &&
           modelPath.EndsWith(".spt", StringComparison.OrdinalIgnoreCase);

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
