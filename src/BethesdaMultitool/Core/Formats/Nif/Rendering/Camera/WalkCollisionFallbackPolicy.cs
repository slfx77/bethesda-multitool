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
        return !IsEffectModel(modelPath, category);
    }

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
