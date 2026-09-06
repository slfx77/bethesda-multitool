using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>
///     Builds a <see cref="CreatureCompositionPlan" /> from a scanned creature and options, resolving its body
///     meshes, skeleton, and animation.
/// </summary>
internal static class CreatureCompositionPlanner
{
    private static readonly string[] OblivionDefaultIdleCandidates = ["idle.kf"];

    private static readonly string[] FalloutStyleDefaultIdleCandidates =
    [
        "locomotion\\mtidle.kf",
        "mtidle.kf"
    ];

    private static readonly string[] NonOblivionLastResortIdleCandidates =
    [
        "idleanims\\specialidle_toungehang.kf",
        "idleanims\\specialidle_sniff.kf",
        "idleanims\\mtidle.kf",
        "locomotion\\mtforward.kf"
    ];

    internal static CreatureCompositionPlan? CreatePlan(
        CreatureScanEntry creature,
        MeshArchiveSet meshArchives,
        NpcAppearanceResolver resolver,
        CreatureCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);

        if (creature.SkeletonPath == null || creature.BodyModelPaths is not { Length: > 0 })
        {
            return null;
        }

        string? weaponMeshPath = null;
        WeapScanEntry? weaponEntry = null;
        if (options.IncludeWeapon && creature.InventoryItems != null)
        {
            var candidates = new List<WeapScanEntry>();
            foreach (var item in creature.InventoryItems)
            {
                if (item.Count <= 0)
                {
                    continue;
                }

                resolver.CollectWeaponEntries(item.ItemFormId, candidates);
            }

            var restriction = resolver.GetWeaponRestriction(creature.CombatStyleFormId);
            weaponEntry = WeaponSelectionScorer.PickBestWeapon(
                candidates,
                restriction,
                null, // Creatures don't have a DNAM skills array
                creature.CombatSkill,
                creature.Strength ?? 10);
            weaponMeshPath = weaponEntry?.ModelPath;
        }

        return CreatePlan(
            creature.SkeletonPath,
            creature.BodyModelPaths,
            meshArchives,
            options,
            creature.ResolveIdleAnimationPath(),
            weaponMeshPath,
            creature,
            weaponEntry,
            resolver.Game);
    }

    internal static CreatureCompositionPlan? CreatePlan(
        string skeletonPath,
        string[] bodyModelPaths,
        MeshArchiveSet meshArchives,
        CreatureCompositionOptions options,
        string? idleAnimationPath = null,
        string? weaponMeshPath = null,
        CreatureScanEntry? creature = null,
        WeapScanEntry? weaponEntry = null,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        ArgumentNullException.ThrowIfNull(skeletonPath);
        ArgumentNullException.ThrowIfNull(bodyModelPaths);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(options);

        if (bodyModelPaths.Length == 0)
        {
            return null;
        }

        var skeletonNifPath = NormalizeMeshPath(skeletonPath);
        var skeletonRaw = NpcMeshHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skeletonRaw == null)
        {
            return null;
        }

        var animation = ResolveCreatureAnimationOverrides(
            skeletonNifPath,
            skeletonRaw.Value,
            meshArchives,
            options,
            idleAnimationPath,
            game);
        var animationOverrides = animation?.Overrides;

        // Merge weapon holster pose over base idle so arm/hand bones adopt a weapon-holding stance
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? weaponPoseOverrides = null;
        string? holsterParentOverrideBone = null;
        if (weaponEntry != null && weaponMeshPath != null)
        {
            var holsterKf = ResolveCreatureWeaponPoseOverrides(
                skeletonNifPath, weaponEntry, meshArchives);
            if (holsterKf != null)
            {
                weaponPoseOverrides = holsterKf.Value.Overrides;
                holsterParentOverrideBone = NpcSkeletonLoader.TryParseSequenceParentBoneName(
                    holsterKf.Value.KfInfo);
                var filtered = FilterCreatureWeaponPoseOverrides(weaponPoseOverrides);
                if (filtered.Count > 0)
                {
                    animationOverrides = NpcSkeletonLoader.MergePoseOverrides(
                        animationOverrides, filtered);
                }
            }
        }

        var boneTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skeletonRaw.Value.Data,
            skeletonRaw.Value.Info,
            animationOverrides);

        var normalizedBodyPaths = bodyModelPaths
            .Select(path => ResolveCreatureBodyPath(skeletonNifPath, path))
            .ToArray();
        Matrix4x4? headAttachmentTransform = null;
        Matrix4x4? weaponAttachmentTransform = null;
        if (boneTransforms != null &&
            boneTransforms.TryGetValue("Bip01 Head", out var headBoneTransform))
        {
            headAttachmentTransform = headBoneTransform;
        }

        if (boneTransforms != null)
        {
            if (boneTransforms.TryGetValue("Weapon", out var weaponBoneTransform))
            {
                weaponAttachmentTransform = weaponBoneTransform;
            }

            // Honor the holster KF's `prn:` text key by recomputing the Weapon node's
            // world transform against the parent override bone (e.g., Bip01 Spine1 for
            // Super Mutant 2-handed). This positions the weapon on the back instead of
            // inheriting the in-hand idle position. Mirrors the NPC path in
            // NpcWeaponAttachmentResolver.LoadWeaponAttachmentPose.
            if (weaponPoseOverrides != null && holsterParentOverrideBone != null)
            {
                var holsteredWeaponTransform = NpcWeaponAttachmentResolver
                    .ResolveWeaponHolsterAttachmentTransform(
                        boneTransforms,
                        weaponPoseOverrides,
                        skeletonRaw.Value.Data,
                        skeletonRaw.Value.Info,
                        "Weapon",
                        holsterParentOverrideBone);
                if (holsteredWeaponTransform.HasValue)
                {
                    weaponAttachmentTransform = holsteredWeaponTransform.Value;
                }
            }
        }

        return new CreatureCompositionPlan
        {
            Creature = creature ?? new CreatureScanEntry(
                null,
                null,
                skeletonPath,
                bodyModelPaths,
                idleAnimationPath != null ? [idleAnimationPath] : null,
                null,
                0),
            Options = options,
            SkeletonNifPath = skeletonNifPath,
            BodyModelPaths = normalizedBodyPaths,
            BoneTransforms = boneTransforms,
            AnimationOverrides = animationOverrides,
            AnimationSourcePath = animation?.SourcePath,
            HeadAttachmentTransform = headAttachmentTransform,
            WeaponAttachmentTransform = weaponAttachmentTransform,
            WeaponMeshPath = weaponMeshPath != null ? NormalizeMeshPath(weaponMeshPath) : null
        };
    }

    private static CreatureAnimationResolution? ResolveCreatureAnimationOverrides(
        string skeletonNifPath,
        (byte[] Data, NifInfo Info) skeletonRaw,
        MeshArchiveSet meshArchives,
        CreatureCompositionOptions options,
        string? idleAnimationPath,
        BethesdaGame game)
    {
        if (options.BindPose)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.AnimOverride))
        {
            var overridePath = ResolveCreatureAnimationPath(skeletonNifPath, options.AnimOverride);
            var explicitAnimation = TryLoadCreatureAnimation(
                overridePath,
                meshArchives,
                probeQuietly: false);
            if (explicitAnimation != null)
            {
                return explicitAnimation;
            }
        }

        if (!string.IsNullOrWhiteSpace(idleAnimationPath))
        {
            var kfPath = ResolveCreatureAnimationPath(skeletonNifPath, idleAnimationPath);
            var recordAnimation = TryLoadCreatureAnimation(
                kfPath,
                meshArchives,
                probeQuietly: false);
            if (recordAnimation != null)
            {
                return recordAnimation;
            }
        }

        var defaultCandidates = game == BethesdaGame.Oblivion
            ? OblivionDefaultIdleCandidates
            : FalloutStyleDefaultIdleCandidates;
        foreach (var candidate in defaultCandidates)
        {
            var defaultAnimation = TryLoadCreatureAnimation(
                ResolveSiblingAnimationPath(skeletonNifPath, candidate),
                meshArchives,
                probeQuietly: true);
            if (defaultAnimation != null)
            {
                return defaultAnimation;
            }
        }

        var embeddedSequence = NifAnimationParser.ParseIdlePoseOverrides(
            skeletonRaw.Data,
            skeletonRaw.Info);
        if (embeddedSequence is { Count: > 0 })
        {
            return new CreatureAnimationResolution(
                embeddedSequence,
                $"{skeletonNifPath}#embedded-sequence");
        }

        var embeddedController = NifNodeControllerPoseReader.Parse(
            skeletonRaw.Data,
            skeletonRaw.Info);
        if (embeddedController is { Count: > 0 })
        {
            return new CreatureAnimationResolution(
                embeddedController,
                $"{skeletonNifPath}#embedded-controller");
        }

        // Preserve the historical FO3/FNV recovery choices, but never substitute a combat,
        // special-idle, or forward-movement clip for TES4's neutral sibling idle.
        if (game != BethesdaGame.Oblivion)
        {
            foreach (var candidate in NonOblivionLastResortIdleCandidates)
            {
                var lastResort = TryLoadCreatureAnimation(
                    ResolveSiblingAnimationPath(skeletonNifPath, candidate),
                    meshArchives,
                    probeQuietly: true);
                if (lastResort != null)
                {
                    return lastResort;
                }
            }
        }

        return null;
    }

    private static CreatureAnimationResolution? TryLoadCreatureAnimation(
        string animationPath,
        MeshArchiveSet meshArchives,
        bool probeQuietly)
    {
        if (probeQuietly &&
            !meshArchives.TryResolvePath(animationPath, out _, out _))
        {
            return null;
        }

        var raw = NpcMeshHelpers.LoadNifRawFromBsa(
            animationPath,
            meshArchives,
            skipConversion: true);
        if (raw == null)
        {
            return null;
        }

        var overrides = NifAnimationParser.ParseIdlePoseOverrides(
            raw.Value.Data,
            raw.Value.Info);
        return overrides is { Count: > 0 }
            ? new CreatureAnimationResolution(overrides, animationPath)
            : null;
    }

    private static string ResolveSiblingAnimationPath(
        string skeletonNifPath,
        string relativeAnimationPath)
    {
        var skeletonDirectory = skeletonNifPath.Replace(
            "skeleton.nif",
            "",
            StringComparison.OrdinalIgnoreCase);
        return skeletonDirectory + relativeAnimationPath;
    }

    private static (Dictionary<string, NifAnimationParser.AnimPoseOverride> Overrides, NifInfo KfInfo)?
        ResolveCreatureWeaponPoseOverrides(
            string skeletonNifPath,
            WeapScanEntry weaponEntry,
            MeshArchiveSet meshArchives)
    {
        if (!NpcWeaponResolver.TryResolveHolsterProfileKey(
                weaponEntry.WeaponType, out var profileKey))
        {
            return null;
        }

        var skeletonDir = skeletonNifPath.Replace(
            "skeleton.nif", "", StringComparison.OrdinalIgnoreCase);

        // Try creature's own directory first, then fall back to _male humanoid KFs.
        // KF files key overrides by bone name, so _male holster KFs work on creature
        // skeletons that share the Bip01 naming convention (e.g., Super Mutants).
        string[] candidateKfPaths =
        [
            skeletonDir + $"{profileKey}Holster.kf",
            skeletonDir + $"{profileKey}idle.kf",
            $"meshes\\characters\\_male\\{profileKey}Holster.kf",
            $"meshes\\characters\\_male\\{profileKey}idle.kf"
        ];

        foreach (var kfPath in candidateKfPaths)
        {
            var kfRaw = NpcMeshHelpers.LoadNifRawFromBsa(kfPath, meshArchives, true);
            if (kfRaw == null)
            {
                continue;
            }

            var overrides = NifAnimationParser.ParseIdlePoseOverrides(
                kfRaw.Value.Data, kfRaw.Value.Info);
            if (overrides is { Count: > 0 })
            {
                return (overrides, kfRaw.Value.Info);
            }
        }

        return null;
    }

    private static Dictionary<string, NifAnimationParser.AnimPoseOverride>
        FilterCreatureWeaponPoseOverrides(
            Dictionary<string, NifAnimationParser.AnimPoseOverride> holsterOverrides)
    {
        var filtered = new Dictionary<string, NifAnimationParser.AnimPoseOverride>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (bone, pose) in holsterOverrides)
        {
            if (!IsCreatureCoreBone(bone))
            {
                filtered[bone] = pose;
            }
        }

        return filtered;
    }

    private static bool IsCreatureCoreBone(string boneName)
    {
        return string.Equals(boneName, "Bip01", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 Pelvis", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 Spine", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 Spine1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 Spine2", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 Neck", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 Neck1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 Head", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Weapon", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveCreatureBodyPath(string skeletonNifPath, string bodyPath)
    {
        if (bodyPath.Contains('\\') || bodyPath.Contains('/'))
        {
            return NormalizeMeshPath(bodyPath);
        }

        var skeletonDirectory = Path.GetDirectoryName(skeletonNifPath);
        return !string.IsNullOrEmpty(skeletonDirectory)
            ? Path.Combine(skeletonDirectory, bodyPath)
            : NormalizeMeshPath(bodyPath);
    }

    private static string ResolveCreatureAnimationPath(string skeletonNifPath, string animationPath)
    {
        if (animationPath.Contains('\\') || animationPath.Contains('/'))
        {
            return NormalizeMeshPath(animationPath);
        }

        var skeletonDirectory = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        return skeletonDirectory + animationPath;
    }

    private static string NormalizeMeshPath(string path)
    {
        return path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
            ? path
            : "meshes\\" + path.TrimStart('\\');
    }

    private sealed record CreatureAnimationResolution(
        Dictionary<string, NifAnimationParser.AnimPoseOverride> Overrides,
        string SourcePath);
}
