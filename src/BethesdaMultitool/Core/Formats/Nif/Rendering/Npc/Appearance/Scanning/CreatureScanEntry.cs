using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>
///     A scanned creature (CREA) record: its skeleton, body models, animations, inventory, type, and combat stats,
///     with helpers to resolve mesh/idle paths.
/// </summary>
internal sealed record CreatureScanEntry(
    string? EditorId,
    string? FullName,
    string? SkeletonPath,
    string[]? BodyModelPaths,
    string[]? AnimationPaths,
    List<InventoryItem>? InventoryItems,
    byte CreatureType)
{
    internal BethesdaMultitool.Core.Assets.AssetRecordOwner? AssetOwner { get; init; }
    public uint? CombatStyleFormId { get; init; }
    public byte? CombatSkill { get; init; }
    public byte? Strength { get; init; }

    internal string GetCreatureTypeName(BethesdaGame game)
    {
        return CreatureTypeNamePolicy.Resolve(game, CreatureType);
    }

    /// <summary>
    ///     Finds the idle animation KF path from KFFZ, looking for "mtidle" pattern.
    ///     Paths are relative filenames resolved against the skeleton directory.
    /// </summary>
    internal string? ResolveIdleAnimationPath()
    {
        if (AnimationPaths is not { Length: > 0 } || SkeletonPath == null)
        {
            return null;
        }

        var skeletonDir = CreatureAssetPath.GetDirectoryName(SkeletonPath);
        if (string.IsNullOrEmpty(skeletonDir))
        {
            return null;
        }

        // Look for idle animation pattern in KFFZ paths
        foreach (var path in AnimationPaths)
        {
            if (path.Contains("idle", StringComparison.OrdinalIgnoreCase))
            {
                return path.Contains('\\') || path.Contains('/')
                    ? path
                    : CreatureAssetPath.Combine(skeletonDir, path);
            }
        }

        return null;
    }

    /// <summary>
    ///     Resolves the first body model path from NIFZ, using the skeleton directory
    ///     as the base path (NIFZ paths are relative filenames).
    /// </summary>
    internal string? ResolveBodyModelPath()
    {
        if (BodyModelPaths is not { Length: > 0 })
        {
            return null;
        }

        var bodyFileName = BodyModelPaths[0];

        // If NIFZ path already contains a directory separator, use it as-is
        if (bodyFileName.Contains('\\') || bodyFileName.Contains('/'))
        {
            return bodyFileName;
        }

        // Otherwise, combine with skeleton directory
        if (SkeletonPath != null)
        {
            var skeletonDir = CreatureAssetPath.GetDirectoryName(SkeletonPath);
            if (!string.IsNullOrEmpty(skeletonDir))
            {
                return CreatureAssetPath.Combine(skeletonDir, bodyFileName);
            }
        }

        return bodyFileName;
    }
}
