using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal static class CreatureCompositionPlanner
{
    internal static CreatureCompositionPlan? CreatePlan(
        CreatureScanEntry creature,
        NpcMeshArchiveSet meshArchives,
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
                skills: null, // Creatures don't have a DNAM skills array
                combatSkillAggregate: creature.CombatSkill,
                strength: creature.Strength ?? 10);
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
            weaponEntry);
    }

    internal static CreatureCompositionPlan? CreatePlan(
        string skeletonPath,
        string[] bodyModelPaths,
        NpcMeshArchiveSet meshArchives,
        CreatureCompositionOptions options,
        string? idleAnimationPath = null,
        string? weaponMeshPath = null,
        CreatureScanEntry? creature = null,
        WeapScanEntry? weaponEntry = null)
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

        var animationOverrides = ResolveCreatureAnimationOverrides(
            skeletonNifPath,
            skeletonRaw.Value,
            meshArchives,
            options,
            idleAnimationPath);

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
            HeadAttachmentTransform = headAttachmentTransform,
            WeaponAttachmentTransform = weaponAttachmentTransform,
            WeaponMeshPath = weaponMeshPath != null ? NormalizeMeshPath(weaponMeshPath) : null
        };
    }

    private static Dictionary<string, NifAnimationParser.AnimPoseOverride>? ResolveCreatureAnimationOverrides(
        string skeletonNifPath,
        (byte[] Data, NifInfo Info) skeletonRaw,
        NpcMeshArchiveSet meshArchives,
        CreatureCompositionOptions options,
        string? idleAnimationPath)
    {
        if (options.BindPose)
        {
            return null;
        }

        (byte[] Data, NifInfo Info)? idleRaw = null;
        if (!string.IsNullOrWhiteSpace(options.AnimOverride))
        {
            var overridePath = ResolveCreatureAnimationPath(skeletonNifPath, options.AnimOverride);
            idleRaw = NpcMeshHelpers.LoadNifRawFromBsa(overridePath, meshArchives, true);
        }

        if (idleRaw == null && !string.IsNullOrWhiteSpace(idleAnimationPath))
        {
            var kfPath = ResolveCreatureAnimationPath(skeletonNifPath, idleAnimationPath);
            idleRaw = NpcMeshHelpers.LoadNifRawFromBsa(kfPath, meshArchives, true);
        }

        if (idleRaw == null)
        {
            var skeletonDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
            idleRaw = NpcMeshHelpers.LoadNifRawFromBsa(
                skeletonDir + "locomotion\\mtidle.kf",
                meshArchives,
                true);

            // Some creatures (e.g., Super Mutants) store mtidle.kf directly in the creature
            // root directory rather than in a locomotion subdirectory
            idleRaw ??= NpcMeshHelpers.LoadNifRawFromBsa(
                skeletonDir + "mtidle.kf",
                meshArchives,
                true);
        }

        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animationOverrides = null;
        if (idleRaw != null)
        {
            animationOverrides = NifAnimationParser.ParseIdlePoseOverrides(idleRaw.Value.Data, idleRaw.Value.Info);
        }

        if (animationOverrides == null || animationOverrides.Count == 0)
        {
            animationOverrides = NifAnimationParser.ParseIdlePoseOverrides(
                skeletonRaw.Data,
                skeletonRaw.Info);
        }

        if (animationOverrides == null || animationOverrides.Count == 0)
        {
            animationOverrides = NifNodeControllerPoseReader.Parse(
                skeletonRaw.Data,
                skeletonRaw.Info);
        }

        if (animationOverrides == null || animationOverrides.Count == 0)
        {
            var skeletonDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
            string[] candidateIdleKfs =
            [
                "mtidle.kf",
                "idleanims\\specialidle_toungehang.kf",
                "idleanims\\specialidle_sniff.kf",
                "idleanims\\mtidle.kf",
                "locomotion\\mtforward.kf"
            ];
            foreach (var candidateKf in candidateIdleKfs)
            {
                var candidateRaw = NpcMeshHelpers.LoadNifRawFromBsa(
                    skeletonDir + candidateKf,
                    meshArchives,
                    true);
                if (candidateRaw == null)
                {
                    continue;
                }

                animationOverrides = NifAnimationParser.ParseIdlePoseOverrides(
                    candidateRaw.Value.Data,
                    candidateRaw.Value.Info);
                if (animationOverrides is { Count: > 0 })
                {
                    break;
                }
            }
        }

        return animationOverrides;
    }

    private static (Dictionary<string, NifAnimationParser.AnimPoseOverride> Overrides, NifInfo KfInfo)?
        ResolveCreatureWeaponPoseOverrides(
            string skeletonNifPath,
            WeapScanEntry weaponEntry,
            NpcMeshArchiveSet meshArchives)
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
            $"meshes\\characters\\_male\\{profileKey}idle.kf",
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
}
