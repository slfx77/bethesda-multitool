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
        if (category == PlacedObjectCategory.Effects || string.IsNullOrWhiteSpace(modelPath))
        {
            return false;
        }

        var normalized = modelPath.Replace('/', '\\').TrimStart('\\');
        if (normalized.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return !normalized.StartsWith("effects\\", StringComparison.OrdinalIgnoreCase);
    }
}
