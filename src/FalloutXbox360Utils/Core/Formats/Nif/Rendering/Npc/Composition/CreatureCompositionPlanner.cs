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
        if (options.IncludeWeapon && creature.InventoryItems != null)
        {
            foreach (var item in creature.InventoryItems)
            {
                weaponMeshPath = resolver.ResolveWeaponMeshPath(item.ItemFormId);
                if (weaponMeshPath != null)
                {
                    break;
                }
            }
        }

        return CreatePlan(
            creature.SkeletonPath,
            creature.BodyModelPaths,
            meshArchives,
            options,
            creature.ResolveIdleAnimationPath(),
            weaponMeshPath,
            creature);
    }

    internal static CreatureCompositionPlan? CreatePlan(
        string skeletonPath,
        string[] bodyModelPaths,
        NpcMeshArchiveSet meshArchives,
        CreatureCompositionOptions options,
        string? idleAnimationPath = null,
        string? weaponMeshPath = null,
        CreatureScanEntry? creature = null)
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
            headAttachmentTransform = Matrix4x4.CreateTranslation(headBoneTransform.Translation);
        }

        if (boneTransforms != null &&
            boneTransforms.TryGetValue("Weapon", out var weaponBoneTransform))
        {
            weaponAttachmentTransform = weaponBoneTransform;
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
            WeaponMeshPath = weaponMeshPath
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
