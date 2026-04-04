using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal static class NpcCompositionPlanner
{
    private static readonly Logger Log = Logger.Instance;

    internal static NpcCompositionPlan CreatePlan(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcCompositionCaches caches,
        NpcCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(caches);
        ArgumentNullException.ThrowIfNull(options);

        var skeleton = options.HeadOnly
            ? null
            : BuildSkeletonComposition(npc, meshArchives, caches, options);

        var coveredSlots = ResolveCoveredSlots(npc, options);

        var effectiveBodyTex = npc.BodyTexturePath;
        var effectiveHandTex = npc.HandTexturePath;
        if (!options.HeadOnly &&
            options.ApplyEgt &&
            npc.FaceGenTextureCoeffs != null)
        {
            FaceGenTextureMorpher.DebugLabel = NpcTextureHelpers.BuildNpcRenderName(npc);
            ApplyBodyEgtMorphs(
                npc,
                meshArchives,
                textureResolver,
                caches.EgtFiles,
                ref effectiveBodyTex,
                ref effectiveHandTex);
        }

        Dictionary<string, Matrix4x4>? attachmentBoneTransforms = skeleton?.BodySkinningBones;
        Matrix4x4? bonelessAttachmentTransform = skeleton != null
            ? NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(
                skeleton.BodySkinningBones,
                skeleton.PoseDeltas)
            : null;

        if (options.HeadOnly &&
            npc.BaseHeadNifPath != null)
        {
            var headRaw = NpcMeshHelpers.LoadNifRawFromBsa(npc.BaseHeadNifPath, meshArchives);
            if (headRaw != null)
            {
                attachmentBoneTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
                    headRaw.Value.Data,
                    headRaw.Value.Info);
            }

            bonelessAttachmentTransform ??=
                NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(attachmentBoneTransforms, null);
        }

        var bodyEquipment = ResolveBodyEquipment(npc, options);
        var headPlan = BuildHeadPlan(
            npc,
            meshArchives,
            textureResolver,
            caches,
            options,
            attachmentBoneTransforms,
            bonelessAttachmentTransform);

        return new NpcCompositionPlan
        {
            Appearance = npc,
            Options = options,
            Skeleton = skeleton,
            Head = headPlan,
            BodyParts = BuildBodyParts(npc, options, coveredSlots, effectiveBodyTex, effectiveHandTex),
            BodyEquipment = bodyEquipment,
            CoveredSlots = coveredSlots,
            EffectiveBodyTexturePath = effectiveBodyTex,
            EffectiveHandTexturePath = effectiveHandTex,
            Weapon = BuildWeaponPlan(npc, meshArchives, skeleton, options)
        };
    }

    internal static NpcSkeletonComposition? BuildSkeletonComposition(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NpcCompositionCaches caches,
        NpcCompositionOptions options)
    {
        if (npc.SkeletonNifPath == null)
        {
            return null;
        }

        var cacheKey = BuildSkeletonCacheKey(npc.SkeletonNifPath, options);
        if (!caches.SkeletonPlans.TryGetValue(cacheKey, out var cached))
        {
            cached = LoadSkeletonPlan(npc.SkeletonNifPath, meshArchives, options);
            caches.SkeletonPlans[cacheKey] = cached;
        }

        if (cached == null)
        {
            return null;
        }

        var bodyBones = cached.BodySkinningBones;
        var weaponBones = bodyBones;
        if (ShouldUseHandToHandEquippedArmPose(npc, options))
        {
            var h2hBones = NpcSkeletonLoader.BuildHandToHandEquippedArmBones(
                cached.SkeletonNifPath,
                meshArchives,
                bodyBones,
                npc.WeaponVisual);
            // Use h2h arm bones for ALL consumers (body, outfit, addon, weapon)
            // so the sleeve, arm, and weapon share the same equipped arm pose.
            bodyBones = h2hBones;
            weaponBones = h2hBones;
        }

        return new NpcSkeletonComposition
        {
            SkeletonNifPath = cached.SkeletonNifPath,
            BodySkinningBones = bodyBones,
            WeaponAttachmentBones = weaponBones,
            PoseDeltas = cached.PoseDeltas,
            AnimationOverrides = cached.AnimationOverrides
        };
    }

    private static NpcCompositionCaches.CachedNpcSkeletonPlan? LoadSkeletonPlan(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        NpcCompositionOptions options)
    {
        var skelRaw = NpcMeshHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
        {
            Log.Warn("Failed to load skeleton for composition planning: {0}", skeletonNifPath);
            return null;
        }

        var animationOverrides = options.BindPose
            ? null
            : NpcSkeletonLoader.LoadIdleAnimationOverrides(
                skeletonNifPath,
                meshArchives,
                skelRaw,
                options.AnimOverride);

        var bodyBones = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skelRaw.Value.Data,
            skelRaw.Value.Info,
            animationOverrides);
        var bindPoseBones = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skelRaw.Value.Data,
            skelRaw.Value.Info);
        var poseDeltas = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, posedWorld) in bodyBones)
        {
            if (!bindPoseBones.TryGetValue(name, out var bindWorld) ||
                !Matrix4x4.Invert(bindWorld, out var inverseBind))
            {
                continue;
            }

            poseDeltas[name] = inverseBind * posedWorld;
        }

        return new NpcCompositionCaches.CachedNpcSkeletonPlan(
            skeletonNifPath,
            bodyBones,
            poseDeltas,
            animationOverrides);
    }

    private static NpcHeadCompositionPlan BuildHeadPlan(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcCompositionCaches caches,
        NpcCompositionOptions options,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        Matrix4x4? bonelessAttachmentTransform)
    {
        float[]? headPreSkinMorphDeltas = null;
        if (options.ApplyEgm &&
            npc.BaseHeadNifPath != null &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var egmPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egm");
            var egm = NpcMeshHelpers.LoadAndCacheEgm(egmPath, meshArchives, caches.EgmFiles);
            if (egm != null && egm.VertexCount > 0)
            {
                headPreSkinMorphDeltas = FaceGenMeshMorpher.ComputeAccumulatedDeltas(
                    egm,
                    npc.FaceGenSymmetricCoeffs,
                    npc.FaceGenAsymmetricCoeffs,
                    egm.VertexCount);
            }
        }

        var effectiveHeadTexturePath = npc.HeadDiffuseOverride != null
            ? "textures\\" + npc.HeadDiffuseOverride
            : null;
        var effectiveHeadTextureUsesEgtMorph = false;
        if (options.ApplyEgt &&
            npc.BaseHeadNifPath != null &&
            npc.FaceGenTextureCoeffs != null &&
            effectiveHeadTexturePath != null)
        {
            FaceGenTextureMorpher.DebugLabel = NpcTextureHelpers.BuildNpcRenderName(npc);
            var egtPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egt");
            if (!caches.EgtFiles.TryGetValue(egtPath, out var egt))
            {
                egt = NpcMeshHelpers.LoadEgtFromBsa(egtPath, meshArchives);
                caches.EgtFiles[egtPath] = egt;
            }

            var baseTexture = egt == null ? null : textureResolver.GetTexture(effectiveHeadTexturePath);
            if (egt != null && baseTexture != null)
            {
                var morphed = FaceGenTextureMorpher.Apply(baseTexture, egt, npc.FaceGenTextureCoeffs);
                if (morphed != null)
                {
                    var morphedKey = NpcTextureHelpers.BuildNpcFaceEgtTextureKey(npc);
                    textureResolver.InjectTexture(morphedKey, morphed);
                    effectiveHeadTexturePath = morphedKey;
                    effectiveHeadTextureUsesEgtMorph = true;
                }
            }
        }

        var raceFacePartPaths = new[]
            {
                npc.MouthNifPath,
                npc.LowerTeethNifPath,
                npc.UpperTeethNifPath,
                npc.TongueNifPath
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
        var eyeNifPaths = new[] { npc.LeftEyeNifPath, npc.RightEyeNifPath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
        var headEquipment = options.IncludeEquipment && npc.EquippedItems != null
            ? npc.EquippedItems
                .Where(item => NpcTextureHelpers.IsHeadEquipment(item.BipedFlags))
                .ToArray()
            : Array.Empty<EquippedItem>();

        return new NpcHeadCompositionPlan
        {
            BaseHeadNifPath = npc.BaseHeadNifPath,
            FaceGenNifPath = npc.FaceGenNifPath,
            HeadPreSkinMorphDeltas = headPreSkinMorphDeltas,
            EffectiveHeadTexturePath = effectiveHeadTexturePath,
            EffectiveHeadTextureUsesEgtMorph = effectiveHeadTextureUsesEgtMorph,
            HairFilter = options.IncludeEquipment && NpcTextureHelpers.HasHatEquipment(npc.EquippedItems)
                ? "Hat"
                : null,
            AttachmentBoneTransforms = attachmentBoneTransforms,
            BonelessAttachmentTransform = bonelessAttachmentTransform,
            RaceFacePartPaths = raceFacePartPaths,
            HairNifPath = npc.HairNifPath,
            HeadPartNifPaths = npc.HeadPartNifPaths ?? [],
            EyeNifPaths = eyeNifPaths,
            HeadEquipment = headEquipment
        };
    }

    private static IReadOnlyList<NpcBodyMeshPlan> BuildBodyParts(
        NpcAppearance npc,
        NpcCompositionOptions options,
        uint coveredSlots,
        string? effectiveBodyTex,
        string? effectiveHandTex)
    {
        if (options.HeadOnly)
        {
            return Array.Empty<NpcBodyMeshPlan>();
        }

        var parts = new List<NpcBodyMeshPlan>(3);
        if ((coveredSlots & 0x04) == 0 &&
            npc.UpperBodyNifPath != null)
        {
            parts.Add(new NpcBodyMeshPlan
            {
                MeshPath = npc.UpperBodyNifPath,
                TextureOverride = effectiveBodyTex,
                RenderOrder = 0
            });
        }

        if ((coveredSlots & 0x08) == 0 &&
            npc.LeftHandNifPath != null)
        {
            parts.Add(new NpcBodyMeshPlan
            {
                MeshPath = npc.LeftHandNifPath,
                TextureOverride = effectiveHandTex,
                RenderOrder = 0
            });
        }

        if ((coveredSlots & 0x10) == 0 &&
            npc.RightHandNifPath != null)
        {
            parts.Add(new NpcBodyMeshPlan
            {
                MeshPath = npc.RightHandNifPath,
                TextureOverride = effectiveHandTex,
                RenderOrder = 0
            });
        }

        return parts;
    }

    private static EquippedItem[] ResolveBodyEquipment(
        NpcAppearance npc,
        NpcCompositionOptions options)
    {
        if (!options.IncludeEquipment || npc.EquippedItems == null)
        {
            return Array.Empty<EquippedItem>();
        }

        var suppressedEquipmentSlots = 0u;
        if (options.IncludeWeapon &&
            npc.WeaponVisual is
            {
                IsVisible: true,
                AttachmentMode: WeaponAttachmentMode.EquippedHandMounted,
                AddonMeshes: { Count: > 0 }
            })
        {
            foreach (var addon in npc.WeaponVisual.AddonMeshes)
            {
                suppressedEquipmentSlots |= addon.BipedFlags;
            }
        }

        return npc.EquippedItems
            .Where(item =>
                !NpcTextureHelpers.IsHeadEquipment(item.BipedFlags) &&
                (item.BipedFlags & suppressedEquipmentSlots) == 0)
            .ToArray();
    }

    private static uint ResolveCoveredSlots(
        NpcAppearance npc,
        NpcCompositionOptions options)
    {
        var coveredSlots = 0u;
        if (options.IncludeEquipment && npc.EquippedItems != null)
        {
            foreach (var item in npc.EquippedItems)
            {
                coveredSlots |= item.BipedFlags;
            }
        }

        if (options.IncludeWeapon &&
            npc.WeaponVisual?.AddonMeshes is { Count: > 0 })
        {
            foreach (var addon in npc.WeaponVisual.AddonMeshes)
            {
                coveredSlots |= addon.BipedFlags;
            }
        }

        return coveredSlots;
    }

    private static void ApplyBodyEgtMorphs(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache,
        ref string? effectiveBodyTex,
        ref string? effectiveHandTex)
    {
        if (npc.BodyEgtPath != null && npc.BodyTexturePath != null)
        {
            var key = NpcMeshHelpers.ApplyBodyEgtMorph(
                npc.BodyEgtPath,
                npc.BodyTexturePath,
                npc.FaceGenTextureCoeffs!,
                npc.NpcFormId,
                "upperbody",
                npc.RenderVariantLabel,
                meshArchives,
                textureResolver,
                egtCache);
            if (key != null)
            {
                effectiveBodyTex = key;
            }
        }

        if (npc.LeftHandEgtPath != null && npc.HandTexturePath != null)
        {
            var key = NpcMeshHelpers.ApplyBodyEgtMorph(
                npc.LeftHandEgtPath,
                npc.HandTexturePath,
                npc.FaceGenTextureCoeffs!,
                npc.NpcFormId,
                "lefthand",
                npc.RenderVariantLabel,
                meshArchives,
                textureResolver,
                egtCache);
            if (key != null)
            {
                effectiveHandTex = key;
            }
        }

        if (npc.RightHandEgtPath != null && npc.HandTexturePath != null)
        {
            _ = NpcMeshHelpers.ApplyBodyEgtMorph(
                npc.RightHandEgtPath,
                npc.HandTexturePath,
                npc.FaceGenTextureCoeffs!,
                npc.NpcFormId,
                "righthand",
                npc.RenderVariantLabel,
                meshArchives,
                textureResolver,
                egtCache);
        }
    }

    private static NpcWeaponCompositionPlan? BuildWeaponPlan(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NpcSkeletonComposition? skeleton,
        NpcCompositionOptions options)
    {
        if (!options.IncludeWeapon ||
            npc.WeaponVisual?.IsVisible != true)
        {
            return null;
        }

        var weaponVisual = npc.WeaponVisual;
        var plan = new NpcWeaponCompositionPlan
        {
            WeaponVisual = weaponVisual,
            UseSkinnedMainWeaponWhenPossible =
                weaponVisual.AttachmentMode == WeaponAttachmentMode.EquippedHandMounted,
            AddonMeshes = weaponVisual.AddonMeshes ?? []
        };

        if (skeleton?.WeaponAttachmentBones == null ||
            weaponVisual.MeshPath == null)
        {
            return plan;
        }

        Matrix4x4? mainAttachmentTransform;
        string? attachmentNodeName;
        string? attachmentSourceLabel;
        string? omitReason = null;
        NpcWeaponAttachmentResolver.WeaponHolsterPose? holsterPose = null;

        switch (weaponVisual.AttachmentMode)
        {
            case WeaponAttachmentMode.EquippedHandMounted:
                if (!NpcWeaponAttachmentResolver.TryResolveEquippedWeaponAttachmentTransform(
                        weaponVisual,
                        skeleton.WeaponAttachmentBones,
                        skeleton.SkeletonNifPath,
                        meshArchives,
                        out var equippedNodeName,
                        out var equippedTransform,
                        out omitReason))
                {
                    return new NpcWeaponCompositionPlan
                    {
                        WeaponVisual = weaponVisual,
                        UseSkinnedMainWeaponWhenPossible = plan.UseSkinnedMainWeaponWhenPossible,
                        AddonMeshes = plan.AddonMeshes,
                        AttachmentOmitReason = omitReason
                    };
                }

                mainAttachmentTransform = equippedTransform;
                attachmentNodeName = equippedNodeName;
                attachmentSourceLabel = " (equipped hand mount)";
                break;

            case WeaponAttachmentMode.HolsterPose:
                if (string.IsNullOrWhiteSpace(weaponVisual.HolsterProfileKey))
                {
                    return new NpcWeaponCompositionPlan
                    {
                        WeaponVisual = weaponVisual,
                        UseSkinnedMainWeaponWhenPossible = plan.UseSkinnedMainWeaponWhenPossible,
                        AddonMeshes = plan.AddonMeshes,
                        AttachmentOmitReason = "missing holster profile"
                    };
                }

                if (!NpcWeaponAttachmentResolver.TryResolveWeaponAttachmentNode(
                        weaponVisual,
                        out var resolvedAttachmentNodeName,
                        out omitReason))
                {
                    return new NpcWeaponCompositionPlan
                    {
                        WeaponVisual = weaponVisual,
                        UseSkinnedMainWeaponWhenPossible = plan.UseSkinnedMainWeaponWhenPossible,
                        AddonMeshes = plan.AddonMeshes,
                        AttachmentOmitReason = omitReason
                    };
                }

                var usePowerArmorHolster = NpcWeaponAttachmentResolver.HasPowerArmorTorso(npc.EquippedItems);
                holsterPose = NpcWeaponAttachmentResolver.LoadWeaponHolsterPose(
                    skeleton.SkeletonNifPath,
                    meshArchives,
                    weaponVisual.HolsterProfileKey!,
                    usePowerArmorHolster);
                mainAttachmentTransform = holsterPose != null
                    ? NpcWeaponAttachmentResolver.ResolveWeaponHolsterAttachmentTransform(
                        holsterPose,
                        resolvedAttachmentNodeName)
                    : null;
                attachmentNodeName = resolvedAttachmentNodeName;
                attachmentSourceLabel = usePowerArmorHolster ? " (power armor holster KF)" : " (holster KF)";
                if (!mainAttachmentTransform.HasValue)
                {
                    return new NpcWeaponCompositionPlan
                    {
                        WeaponVisual = weaponVisual,
                        AttachmentNodeName = attachmentNodeName,
                        AttachmentSourceLabel = attachmentSourceLabel,
                        HolsterPose = holsterPose,
                        UseSkinnedMainWeaponWhenPossible = plan.UseSkinnedMainWeaponWhenPossible,
                        AddonMeshes = plan.AddonMeshes,
                        AttachmentOmitReason = holsterPose == null
                            ? "missing holster pose"
                            : $"no attachment node '{attachmentNodeName}' in holster pose"
                    };
                }

                break;

            default:
                return new NpcWeaponCompositionPlan
                {
                    WeaponVisual = weaponVisual,
                    UseSkinnedMainWeaponWhenPossible = plan.UseSkinnedMainWeaponWhenPossible,
                    AddonMeshes = plan.AddonMeshes,
                    AttachmentOmitReason = $"unsupported attachment mode {weaponVisual.AttachmentMode}"
                };
        }

        var weaponRaw = NpcMeshHelpers.LoadNifRawFromBsa(weaponVisual.MeshPath, meshArchives);
        if (weaponRaw == null)
        {
            return new NpcWeaponCompositionPlan
            {
                WeaponVisual = weaponVisual,
                MainAttachmentTransform = mainAttachmentTransform,
                AttachmentNodeName = attachmentNodeName,
                AttachmentSourceLabel = attachmentSourceLabel,
                HolsterPose = holsterPose,
                UseSkinnedMainWeaponWhenPossible = plan.UseSkinnedMainWeaponWhenPossible,
                AddonMeshes = plan.AddonMeshes,
                AttachmentOmitReason = $"failed to load weapon '{weaponVisual.MeshPath}'"
            };
        }

        HashSet<int>? mainWeaponExcludedShapes = null;
        List<NifSceneGraphWalker.ParentBoneShapeGroup> holsterAttachmentGroups = [];
        if (weaponVisual.AttachmentMode == WeaponAttachmentMode.HolsterPose)
        {
            var visAnalysis = NifSceneGraphWalker.AnalyzeVisControllers(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info);
            holsterAttachmentGroups = visAnalysis.ParentBoneGroups;
            if (visAnalysis.VisControlledShapeIndices.Count > 0 || holsterAttachmentGroups.Count > 0)
            {
                mainWeaponExcludedShapes = new HashSet<int>(visAnalysis.VisControlledShapeIndices);
                foreach (var group in holsterAttachmentGroups)
                {
                    mainWeaponExcludedShapes.UnionWith(group.ShapeIndices);
                }
            }
        }

        Dictionary<string, NifAnimationParser.AnimPoseOverride>? holsterModelPoseOverrides = null;
        var renderOnlyExplicitHolsterAttachmentGroups =
            weaponVisual.AttachmentMode == WeaponAttachmentMode.HolsterPose &&
            holsterAttachmentGroups.Count > 0;
        if (renderOnlyExplicitHolsterAttachmentGroups)
        {
            holsterModelPoseOverrides = NifNodeControllerPoseReader.Parse(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                true);
        }

        return new NpcWeaponCompositionPlan
        {
            WeaponVisual = weaponVisual,
            MainAttachmentTransform = mainAttachmentTransform,
            AttachmentNodeName = attachmentNodeName,
            AttachmentSourceLabel = attachmentSourceLabel,
            HolsterPose = holsterPose,
            MainWeaponExcludedShapes = mainWeaponExcludedShapes,
            HolsterAttachmentGroups = holsterAttachmentGroups,
            HolsterModelPoseOverrides = holsterModelPoseOverrides,
            RenderOnlyExplicitHolsterAttachmentGroups = renderOnlyExplicitHolsterAttachmentGroups,
            UseSkinnedMainWeaponWhenPossible = plan.UseSkinnedMainWeaponWhenPossible,
            AddonMeshes = plan.AddonMeshes
        };
    }

    private static bool ShouldUseHandToHandEquippedArmPose(
        NpcAppearance npc,
        NpcCompositionOptions options)
    {
        return !options.BindPose &&
               options.AnimOverride == null &&
               npc.WeaponVisual?.IsVisible == true &&
               npc.WeaponVisual.AttachmentMode == WeaponAttachmentMode.EquippedHandMounted &&
               npc.WeaponVisual.WeaponType == WeaponType.HandToHandMelee;
    }

    private static string BuildSkeletonCacheKey(
        string skeletonNifPath,
        NpcCompositionOptions options)
    {
        return $"{skeletonNifPath}|bind:{options.BindPose}|anim:{options.AnimOverride ?? string.Empty}";
    }
}
