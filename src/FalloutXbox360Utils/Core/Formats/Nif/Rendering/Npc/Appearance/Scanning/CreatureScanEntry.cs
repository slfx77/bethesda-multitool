using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal sealed record CreatureScanEntry(
    string? EditorId,
    string? FullName,
    string? SkeletonPath,
    string[]? BodyModelPaths,
    string[]? AnimationPaths,
    List<InventoryItem>? InventoryItems,
    byte CreatureType)
{
    public uint? CombatStyleFormId { get; init; }
    public byte? CombatSkill { get; init; }
    public byte? Strength { get; init; }

    internal string CreatureTypeName => CreatureType switch
    {
        0 => "Animal",
        1 => "Mutated Animal",
        2 => "Mutated Insect",
        3 => "Abomination",
        4 => "Super Mutant",
        5 => "Feral Ghoul",
        6 => "Robot",
        7 => "Giant",
        _ => $"Unknown ({CreatureType})"
    };

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

        var skeletonDir = Path.GetDirectoryName(SkeletonPath);
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
                    : Path.Combine(skeletonDir, path);
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
            var skeletonDir = Path.GetDirectoryName(SkeletonPath);
            if (!string.IsNullOrEmpty(skeletonDir))
            {
                return Path.Combine(skeletonDir, bodyFileName);
            }
        }

        return bodyFileName;
    }
}
