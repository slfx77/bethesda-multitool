using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Builds composited NPC full-body models: skeleton + body parts + equipment + head.
///     Also provides skeleton-only debug visualization.
/// </summary>
internal static class NpcBodyBuilder
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Builds the composited full-body model (body + equipment + head) without rendering.
    /// </summary>
    internal static NifRenderableModel? Build(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        ref Dictionary<string, Matrix4x4>? skeletonBoneCache,
        ref Dictionary<string, Matrix4x4>? poseDeltaCache,
        NpcRenderSettings s)
    {
        // Load skeleton bone transforms (cached across NPCs â€” same skeleton for all humans)
        if (skeletonBoneCache == null && npc.SkeletonNifPath != null)
        {
            LoadSkeletonBones(npc.SkeletonNifPath, meshArchives, s.BindPose,
                out skeletonBoneCache, out poseDeltaCache, s.AnimOverride);
        }

        // Skeleton-only mode: build geometric visualization from bone positions/hierarchy.
        if (s.Skeleton && npc.SkeletonNifPath != null)
        {
            var skelModel = BuildSkeletonVisualization(npc.SkeletonNifPath, meshArchives, s.BindPose);
            if (skelModel != null)
                return skelModel;
        }

        // Determine which body slots are covered by equipment
        var coveredSlots = 0u;
        if (!s.NoEquip && npc.EquippedItems != null)
        {
            foreach (var item in npc.EquippedItems)
                coveredSlots |= item.BipedFlags;
        }

        if (!s.NoEquip && npc.WeaponVisual?.AddonMeshes is { Count: > 0 })
        {
            foreach (var addon in npc.WeaponVisual.AddonMeshes)
            {
                coveredSlots |= addon.BipedFlags;
            }
        }

        Log.Debug("NPC 0x{0:X8} ({1}): coveredSlots=0x{2:X}, equipment={3}, bodyTex={4}, upperBody={5}",
            npc.NpcFormId, npc.EditorId ?? "?", coveredSlots,
            npc.EquippedItems != null ? string.Join(", ", npc.EquippedItems.Select(e => e.MeshPath)) : "(none)",
            npc.BodyTexturePath ?? "(null)", npc.UpperBodyNifPath ?? "(null)");

        var bodyModel = new NifRenderableModel();

        // Pre-compute EGT-morphed body/hand textures
        var effectiveBodyTex = npc.BodyTexturePath;
        var effectiveHandTex = npc.HandTexturePath;
        if (!s.NoEgt && npc.FaceGenTextureCoeffs != null)
        {
            ApplyBodyEgtMorphs(npc, meshArchives, textureResolver, egtCache,
                ref effectiveBodyTex, ref effectiveHandTex);
        }

        // Load body parts using skeleton idle-pose bone transforms directly.
        // skinMatrix = IBP * skelIdleWorld correctly transforms from each equipment NIF's
        // own bind-pose space to idle-pose world space, regardless of whether the equipment
        // NIF's bone transforms match the skeleton's bind pose (they may differ after
        // Xbox BEâ†’LE conversion).
        var bodyAndAddonBones = skeletonBoneCache;
        var weaponAttachmentBones = skeletonBoneCache;
        if (bodyAndAddonBones != null &&
            ShouldUseHandToHandIdleArmPose(npc, s) &&
            !string.IsNullOrWhiteSpace(npc.WeaponVisual?.EquippedPoseKfPath) &&
            npc.SkeletonNifPath != null)
        {
            weaponAttachmentBones = BuildHandToHandEquippedArmBones(
                npc.SkeletonNifPath,
                meshArchives,
                bodyAndAddonBones,
                npc.WeaponVisual);
        }

        if ((coveredSlots & 0x04) == 0 && npc.UpperBodyNifPath != null)
        {
            LoadAndMergeBodyPart(npc.UpperBodyNifPath, effectiveBodyTex, 0,
                meshArchives, textureResolver, bodyAndAddonBones, bodyModel);
        }

        if ((coveredSlots & 0x08) == 0 && npc.LeftHandNifPath != null)
        {
            LoadAndMergeBodyPart(npc.LeftHandNifPath, effectiveHandTex, 0,
                meshArchives, textureResolver, bodyAndAddonBones, bodyModel);
        }

        if ((coveredSlots & 0x10) == 0 && npc.RightHandNifPath != null)
        {
            LoadAndMergeBodyPart(npc.RightHandNifPath, effectiveHandTex, 0,
                meshArchives, textureResolver, bodyAndAddonBones, bodyModel);
        }

        var bonelessHeadAttachmentTransform = NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(
            skeletonBoneCache,
            poseDeltaCache);

        // Load equipment meshes
        LoadEquipment(npc, meshArchives, textureResolver, bodyAndAddonBones,
            effectiveBodyTex, effectiveHandTex, bodyModel, s);

        // Load weapon mesh (holster animation transforms or equipped hand attachment; no generic fallback)
        LoadWeapon(npc, meshArchives, textureResolver, bodyAndAddonBones, weaponAttachmentBones, bodyModel, s,
            npc.SkeletonNifPath);

        var headModel = NpcHeadBuilder.Build(npc, meshArchives, textureResolver,
            headMeshCache, egmCache, egtCache, s,
            idlePoseBones: skeletonBoneCache,
            headEquipmentTransformOverride: bonelessHeadAttachmentTransform);

        if (headModel != null && headModel.HasGeometry)
        {
            foreach (var sub in headModel.Submeshes)
            {
                sub.RenderOrder += 1;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }

        return bodyModel.HasGeometry ? bodyModel : null;
    }

    /// <summary>
    ///     Loads skeleton NIF, applies idle animation overrides, and computes pose deltas.
    /// </summary>
    private static void LoadSkeletonBones(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        bool bindPose,
        out Dictionary<string, Matrix4x4>? boneCache,
        out Dictionary<string, Matrix4x4>? poseDeltaCache,
        string? animOverride = null)
    {
        boneCache = null;
        poseDeltaCache = null;

        var skelRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
        {
            Log.Warn("Failed to load skeleton: {0}", skeletonNifPath);
            return;
        }

        var idleOverrides = bindPose
            ? null
            : LoadIdleAnimationOverrides(skeletonNifPath, meshArchives, skelRaw, animOverride);

        boneCache = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skelRaw.Value.Data, skelRaw.Value.Info, idleOverrides);

        // Compute pose deltas: inverse(skelBind) * skelIdle for each bone
        var skelBindPose = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skelRaw.Value.Data, skelRaw.Value.Info);
        poseDeltaCache = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, idleWorld) in boneCache)
        {
            if (skelBindPose.TryGetValue(name, out var bindWorld))
            {
                Matrix4x4.Invert(bindWorld, out var invBind);
                poseDeltaCache[name] = invBind * idleWorld;
            }
        }

        Log.Debug("Skeleton loaded: {0} bones, {1} pose deltas from {2}{3}",
            boneCache.Count, poseDeltaCache.Count, skeletonNifPath,
            idleOverrides != null ? $" (idle pose: {idleOverrides.Count} overrides)" : " (bind pose)");
    }

    /// <summary>
    ///     Loads idle animation overrides from KF file adjacent to the skeleton NIF.
    ///     Handles femaleâ†’male KF fallback and embedded sequence fallback.
    /// </summary>
    private static Dictionary<string, NifAnimationParser.AnimPoseOverride>? LoadIdleAnimationOverrides(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        (byte[] Data, NifInfo Info)? skelRaw,
        string? animOverride = null)
    {
        var skelDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);

        // Custom animation override: resolve relative to skeleton directory
        if (animOverride != null)
        {
            var customPath = skelDir + animOverride;
            var customRaw = NpcRenderHelpers.LoadNifRawFromBsa(customPath, meshArchives, true);
            if (customRaw != null)
            {
                Log.Debug("Using custom animation: {0}", customPath);
                return NifAnimationParser.ParseIdlePoseOverrides(customRaw.Value.Data, customRaw.Value.Info);
            }

            Log.Warn("Custom animation not found: {0}", customPath);
        }

        var idleKfPath = skelDir + "locomotion\\mtidle.kf";
        var idleRaw = NpcRenderHelpers.LoadNifRawFromBsa(idleKfPath, meshArchives, true);

        // Female skeletons share male locomotion animations
        if (idleRaw == null && skelDir.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            var maleKfPath = skelDir.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase)
                             + "locomotion\\mtidle.kf";
            idleRaw = NpcRenderHelpers.LoadNifRawFromBsa(maleKfPath, meshArchives, true);
        }

        Dictionary<string, NifAnimationParser.AnimPoseOverride>? overrides = null;
        if (idleRaw != null)
            overrides = NifAnimationParser.ParseIdlePoseOverrides(idleRaw.Value.Data, idleRaw.Value.Info);

        // Fallback: check skeleton NIF itself (creature skeletons may embed sequences)
        if (overrides == null && skelRaw != null)
            overrides = NifAnimationParser.ParseIdlePoseOverrides(skelRaw.Value.Data, skelRaw.Value.Info);

        return overrides;
    }

    private static bool ShouldUseHandToHandIdleArmPose(
        NpcAppearance npc,
        NpcRenderSettings settings)
    {
        return !settings.BindPose &&
               settings.AnimOverride == null &&
               npc.WeaponVisual?.IsVisible == true &&
               npc.WeaponVisual.AttachmentMode == WeaponAttachmentMode.EquippedHandMounted &&
               npc.WeaponVisual.WeaponType == WeaponType.HandToHandMelee;
    }

    private static Dictionary<string, Matrix4x4> BuildHandToHandEquippedArmBones(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, Matrix4x4> fallbackIdleBones,
        WeaponVisual? weaponVisual)
    {
        var skelRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
        {
            return fallbackIdleBones;
        }

        var baseIdleOverrides = LoadIdleAnimationOverrides(
            skeletonNifPath,
            meshArchives,
            skelRaw);
        var equippedPoseKfPath = weaponVisual?.EquippedPoseKfPath;
        if (string.IsNullOrWhiteSpace(equippedPoseKfPath))
        {
            return fallbackIdleBones;
        }

        var relativeEquippedPoseKfPath =
            equippedPoseKfPath?.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) == true
                ? equippedPoseKfPath["meshes\\".Length..]
                : equippedPoseKfPath;
        var armPoseOverrides = LoadNamedAnimationOverrides(
            skeletonNifPath,
            meshArchives,
            relativeEquippedPoseKfPath!);
        if (armPoseOverrides == null || armPoseOverrides.Count == 0)
        {
            return fallbackIdleBones;
        }

        var mergedOverrides = baseIdleOverrides != null
            ? new Dictionary<string, NifAnimationParser.AnimPoseOverride>(
                baseIdleOverrides,
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, NifAnimationParser.AnimPoseOverride>(StringComparer.OrdinalIgnoreCase);

        foreach (var (boneName, pose) in armPoseOverrides)
        {
            // h2hequip.kf is the equip TRANSITION animation — its Weapon bone override
            // (translation 29.3, 0, -6.5) is only active during the draw animation.
            // During idle, h2hidle.kf has NO Weapon bone override, so the Weapon bone
            // stays at its skeleton bind-pose position. Only apply arm bone overrides.
            if (ShouldUseHandToHandEquippedBone(boneName, false))
            {
                mergedOverrides[boneName] = pose;
            }
        }

        if (mergedOverrides.Count == 0)
        {
            return fallbackIdleBones;
        }

        Log.Debug(
            "Layering {0} right-arm overrides onto base idle for hand-to-hand weapon",
            equippedPoseKfPath ?? "h2hequip.kf");
        return NifGeometryExtractor.ExtractNamedBoneTransforms(
            skelRaw.Value.Data,
            skelRaw.Value.Info,
            mergedOverrides);
    }

    private static bool ShouldUseHandToHandEquippedBone(string boneName, bool includeWeaponOverride)
    {
        return (includeWeaponOverride &&
                string.Equals(boneName, "Weapon", StringComparison.OrdinalIgnoreCase)) ||
               string.Equals(boneName, "Bip01 R Clavicle", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 R UpperArm", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 R Forearm", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 R ForeTwist", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 R Hand", StringComparison.OrdinalIgnoreCase) ||
               boneName.StartsWith("Bip01 R Finger", StringComparison.OrdinalIgnoreCase) ||
               boneName.StartsWith("Bip01 R Thumb", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, NifAnimationParser.AnimPoseOverride>? LoadNamedAnimationOverrides(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        string kfRelPath,
        bool sampleLastKeyframe = false)
    {
        var kfPath = ResolveAnimationAssetPath(skeletonNifPath, kfRelPath);
        var raw = NpcRenderHelpers.LoadNifRawFromBsa(kfPath, meshArchives, true);

        if (raw == null &&
            kfPath.Contains(@"characters\_female\", StringComparison.OrdinalIgnoreCase))
        {
            var malePath = kfPath.Replace(
                @"characters\_female\",
                @"characters\_male\",
                StringComparison.OrdinalIgnoreCase);
            raw = NpcRenderHelpers.LoadNifRawFromBsa(malePath, meshArchives, true);
        }

        return raw != null
            ? NifAnimationParser.ParseIdlePoseOverrides(raw.Value.Data, raw.Value.Info, sampleLastKeyframe)
            : null;
    }

    internal static string ResolveAnimationAssetPath(string skeletonNifPath, string kfRelPath)
    {
        var normalizedPath = kfRelPath.Replace('/', '\\').TrimStart('\\');
        if (normalizedPath.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath;
        }

        if (normalizedPath.StartsWith(@"characters\", StringComparison.OrdinalIgnoreCase))
        {
            return @"meshes\" + normalizedPath;
        }

        var skelDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        return skelDir + normalizedPath;
    }

    /// <summary>
    ///     Builds a skeleton-only visualization from the skeleton NIF.
    /// </summary>
    private static NifRenderableModel? BuildSkeletonVisualization(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        bool bindPose)
    {
        var skelRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
            return null;

        var overrides = bindPose
            ? null
            : LoadIdleAnimationOverrides(skeletonNifPath, meshArchives, skelRaw);

        var hierarchy = NifGeometryExtractor.ExtractSkeletonHierarchy(
            skelRaw.Value.Data, skelRaw.Value.Info, overrides);
        return hierarchy != null ? BuildSkeletonModel(hierarchy) : null;
    }

    /// <summary>
    ///     Loads a body part NIF with skeleton-driven skinning and merges into target model.
    /// </summary>
    private static void LoadAndMergeBodyPart(
        string nifPath, string? textureOverride, int renderOrder,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? idleBoneTransforms,
        NifRenderableModel targetModel)
    {
        var raw = NpcRenderHelpers.LoadNifRawFromBsa(nifPath, meshArchives);
        if (raw == null)
        {
            Log.Warn("Body part NIF failed to load: {0}", nifPath);
            return;
        }

        var partModel = NifGeometryExtractor.Extract(raw.Value.Data, raw.Value.Info, textureResolver,
            externalBoneTransforms: idleBoneTransforms,
            useDualQuaternionSkinning: true);
        if (partModel == null || !partModel.HasGeometry)
        {
            Log.Warn("Body part NIF has no geometry: {0}", nifPath);
            return;
        }

        Log.Debug("Body part '{0}': {1} submeshes, bounds ({2:F2},{3:F2},{4:F2})â†’({5:F2},{6:F2},{7:F2})",
            nifPath, partModel.Submeshes.Count,
            partModel.MinX, partModel.MinY, partModel.MinZ,
            partModel.MaxX, partModel.MaxY, partModel.MaxZ);

        foreach (var sub in partModel.Submeshes)
        {
            if (textureOverride != null &&
                NpcRenderHelpers.ShouldApplyBodyTextureOverride(sub.DiffuseTexturePath, textureOverride))
                sub.DiffuseTexturePath = textureOverride;
            sub.RenderOrder = renderOrder;
            targetModel.Submeshes.Add(sub);
            targetModel.ExpandBounds(sub.Positions);
        }
    }

    private static void ApplyBodyEgtMorphs(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache,
        ref string? effectiveBodyTex, ref string? effectiveHandTex)
    {
        if (npc.BodyEgtPath != null && npc.BodyTexturePath != null)
        {
            var key = NpcRenderHelpers.ApplyBodyEgtMorph(npc.BodyEgtPath, npc.BodyTexturePath,
                npc.FaceGenTextureCoeffs!, npc.NpcFormId, "upperbody", npc.RenderVariantLabel,
                meshArchives, textureResolver, egtCache);
            if (key != null) effectiveBodyTex = key;
        }

        if (npc.LeftHandEgtPath != null && npc.HandTexturePath != null)
        {
            var key = NpcRenderHelpers.ApplyBodyEgtMorph(npc.LeftHandEgtPath, npc.HandTexturePath,
                npc.FaceGenTextureCoeffs!, npc.NpcFormId, "lefthand", npc.RenderVariantLabel,
                meshArchives, textureResolver, egtCache);
            if (key != null) effectiveHandTex = key;
        }

        if (npc.RightHandEgtPath != null && npc.HandTexturePath != null)
        {
            NpcRenderHelpers.ApplyBodyEgtMorph(npc.RightHandEgtPath, npc.HandTexturePath,
                npc.FaceGenTextureCoeffs!, npc.NpcFormId, "righthand", npc.RenderVariantLabel,
                meshArchives, textureResolver, egtCache);
        }
    }

    private static void LoadEquipment(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? idleBoneTransforms,
        string? effectiveBodyTex, string? effectiveHandTex,
        NifRenderableModel bodyModel, NpcRenderSettings s)
    {
        if (s.NoEquip || npc.EquippedItems == null)
            return;

        var suppressedEquipmentSlots = 0u;
        if (npc.WeaponVisual is
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

        foreach (var item in npc.EquippedItems)
        {
            if (NpcRenderHelpers.IsHeadEquipment(item.BipedFlags))
                continue;

            if ((item.BipedFlags & suppressedEquipmentSlots) != 0)
            {
                Log.Debug("Equipment '{0}' suppressed due to active weapon-addon slot overlap 0x{1:X}",
                    item.MeshPath,
                    item.BipedFlags & suppressedEquipmentSlots);
                continue;
            }

            var equipRaw = NpcRenderHelpers.LoadNifRawFromBsa(item.MeshPath, meshArchives);
            if (equipRaw == null)
            {
                Log.Warn("Equipment NIF failed to load: {0}", item.MeshPath);
                continue;
            }

            var useRigidAttachment = item.AttachmentMode != EquipmentAttachmentMode.None &&
                                     idleBoneTransforms != null &&
                                     IsRigidEquipmentModel(equipRaw.Value.Data, equipRaw.Value.Info);

            var equipModel = NifGeometryExtractor.Extract(
                equipRaw.Value.Data, equipRaw.Value.Info, textureResolver,
                externalBoneTransforms: useRigidAttachment ? null : idleBoneTransforms,
                useDualQuaternionSkinning: !useRigidAttachment);
            if (equipModel == null || !equipModel.HasGeometry)
            {
                Log.Warn("Equipment NIF has no geometry: {0}", item.MeshPath);
                continue;
            }

            if (useRigidAttachment)
            {
                if (!TryResolveEquipmentAttachmentTransform(
                        item,
                        idleBoneTransforms!,
                        out var attachmentNodeName,
                        out var attachmentTransform,
                        out var omitReason))
                {
                    Log.Warn("Rigid equipment omitted for NPC 0x{0:X8}: {1}", npc.NpcFormId,
                        omitReason ?? item.MeshPath);
                    continue;
                }

                NpcRenderHelpers.TransformModel(equipModel, attachmentTransform);
                Log.Debug("Rigid equipment '{0}': node '{1}' at ({2:F1},{3:F1},{4:F1})",
                    item.MeshPath,
                    attachmentNodeName,
                    attachmentTransform.Translation.X,
                    attachmentTransform.Translation.Y,
                    attachmentTransform.Translation.Z);
            }

            Log.Debug("Equipment '{0}': {1} submeshes, bounds ({2:F2},{3:F2},{4:F2})â†’({5:F2},{6:F2},{7:F2})",
                item.MeshPath, equipModel.Submeshes.Count,
                equipModel.MinX, equipModel.MinY, equipModel.MinZ,
                equipModel.MaxX, equipModel.MaxY, equipModel.MaxZ);

            foreach (var sub in equipModel.Submeshes)
            {
                Log.Debug(
                    "  Equip sub: tex={0}, alphaBlend={1}, alphaTest={2} func={3} thresh={4}, matAlpha={5:F2}, doubleSided={6}, verts={7}",
                    sub.DiffuseTexturePath ?? "(null)",
                    sub.HasAlphaBlend, sub.HasAlphaTest,
                    sub.AlphaTestFunction, sub.AlphaTestThreshold,
                    sub.MaterialAlpha, sub.IsDoubleSided, sub.Positions.Length / 3);

                if (effectiveBodyTex != null &&
                    NpcRenderHelpers.IsEquipmentSkinSubmesh(sub.DiffuseTexturePath))
                {
                    sub.DiffuseTexturePath =
                        sub.DiffuseTexturePath!.Contains("hand", StringComparison.OrdinalIgnoreCase)
                            ? effectiveHandTex ?? effectiveBodyTex
                            : effectiveBodyTex;
                }

                sub.RenderOrder = 5;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }
    }

    private static void LoadWeapon(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? addonSkinningBoneTransforms,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        NifRenderableModel bodyModel, NpcRenderSettings s,
        string? skeletonNifPath)
    {
        if (s.NoEquip || s.NoWeapon || npc.WeaponVisual?.IsVisible != true || attachmentBoneTransforms == null)
        {
            return;
        }

        if (npc.WeaponVisual.MeshPath == null)
        {
            Log.Warn("Weapon omitted for NPC 0x{0:X8}: missing weapon mesh", npc.NpcFormId);
            return;
        }

        if (npc.WeaponVisual.AttachmentMode == WeaponAttachmentMode.EquippedHandMounted &&
            npc.WeaponVisual.AddonMeshes is { Count: > 0 })
        {
            // Hand-to-hand weapons can be split across a skinned glove/forearm addon
            // plus a rigid weapon housing mounted from equipped hand/forearm nodes.
            // Keep the skinned addon on the same body pose as the actor; only the rigid
            // housing should use the separate attachment transform.
            LoadWeaponAddonMeshes(
                npc,
                meshArchives,
                textureResolver,
                addonSkinningBoneTransforms,
                bodyModel);
        }

        if (!npc.WeaponVisual.RenderStandaloneMesh)
        {
            Log.Debug("Weapon '{0}' suppressed as standalone mesh; rendering via addon meshes only",
                npc.WeaponVisual.MeshPath ?? npc.WeaponVisual.EditorId ?? "?");
            return;
        }

        var usePowerArmorHolster = HasPowerArmorTorso(npc.EquippedItems);
        Matrix4x4? weaponBoneTransform;
        WeaponHolsterPose? holsterPose = null;
        string attachmentNodeName;
        string attachmentSourceLabel;

        switch (npc.WeaponVisual.AttachmentMode)
        {
            case WeaponAttachmentMode.EquippedHandMounted:
            {
                if (!TryResolveEquippedWeaponAttachmentTransform(
                        npc.WeaponVisual,
                        attachmentBoneTransforms,
                        skeletonNifPath,
                        meshArchives,
                        out attachmentNodeName,
                        out var equippedAttachmentTransform,
                        out var omitReason))
                {
                    Log.Warn("Weapon omitted for NPC 0x{0:X8}: {1}", npc.NpcFormId,
                        omitReason ?? "unsupported attachment");
                    return;
                }

                weaponBoneTransform = equippedAttachmentTransform;
                attachmentSourceLabel = " (equipped hand mount)";
                break;
            }
            case WeaponAttachmentMode.HolsterPose:
            {
                if (npc.WeaponVisual.HolsterProfileKey == null)
                {
                    Log.Warn("Weapon omitted for NPC 0x{0:X8}: missing holster profile", npc.NpcFormId);
                    return;
                }

                if (!TryResolveWeaponAttachmentNode(npc.WeaponVisual, out attachmentNodeName, out var omitReason))
                {
                    Log.Warn("Weapon omitted for NPC 0x{0:X8}: {1}", npc.NpcFormId,
                        omitReason ?? "unsupported attachment");
                    return;
                }

                holsterPose = LoadWeaponHolsterPose(
                    skeletonNifPath,
                    meshArchives,
                    npc.WeaponVisual.HolsterProfileKey,
                    usePowerArmorHolster);
                if (holsterPose == null)
                {
                    Log.Warn("Weapon omitted for NPC 0x{0:X8}: holster KF missing for profile {1}{2}",
                        npc.NpcFormId,
                        npc.WeaponVisual.HolsterProfileKey,
                        usePowerArmorHolster ? " (power armor)" : "");
                    return;
                }

                weaponBoneTransform = ResolveWeaponHolsterAttachmentTransform(holsterPose, attachmentNodeName);
                if (!weaponBoneTransform.HasValue)
                {
                    Log.Warn("Weapon omitted for NPC 0x{0:X8}: no attachment node '{1}' in holster pose for {2}",
                        npc.NpcFormId,
                        attachmentNodeName,
                        npc.WeaponVisual.MeshPath);
                    return;
                }

                attachmentSourceLabel = usePowerArmorHolster ? " (power armor holster KF)" : " (holster KF)";
                break;
            }
            default:
                Log.Warn("Weapon omitted for NPC 0x{0:X8}: unsupported attachment mode {1}",
                    npc.NpcFormId,
                    npc.WeaponVisual.AttachmentMode);
                return;
        }

        var weaponRaw = NpcRenderHelpers.LoadNifRawFromBsa(
            npc.WeaponVisual.MeshPath, meshArchives);
        if (weaponRaw == null)
        {
            Log.Warn("Weapon NIF failed to load: {0}", npc.WeaponVisual.MeshPath);
            return;
        }

        // Some held fist weapons carry skinning data, but driving them from the special
        // equipped-arm pose can detach them from the actor when the body is still using
        // the base idle. Keep any skinned weapon mesh on the body pose and reserve the
        // hand-mounted transform path for rigid placement only.
        if (npc.WeaponVisual.AttachmentMode == WeaponAttachmentMode.EquippedHandMounted)
        {
            var skinnedModel = NifGeometryExtractor.Extract(
                weaponRaw.Value.Data, weaponRaw.Value.Info, textureResolver,
                externalBoneTransforms: addonSkinningBoneTransforms ?? attachmentBoneTransforms,
                useDualQuaternionSkinning: true);
            if (skinnedModel != null && skinnedModel.HasGeometry && skinnedModel.WasSkinned)
            {
                Log.Debug("Weapon '{0}' ({1}): skinned to body pose bones, {2} submeshes",
                    npc.WeaponVisual.MeshPath, npc.WeaponVisual.WeaponType,
                    skinnedModel.Submeshes.Count);

                foreach (var sub in skinnedModel.Submeshes)
                {
                    sub.RenderOrder = 6;
                    bodyModel.Submeshes.Add(sub);
                    bodyModel.ExpandBounds(sub.Positions);
                }

                return;
            }
        }

        // In holster mode, skip shapes under NiVisController-targeted nodes.
        // Heavy weapons (flamer, minigun) use NiVisController to hide the gun body
        // when holstered — only the backpack/tank sub-tree should be visible.
        // The entire weapon NIF attaches to the "Weapon" bone (via Prn on scene root),
        // so the remaining shapes still use the normal weapon attachment transform path.
        HashSet<int>? mainWeaponExcludedShapes = null;
        List<NifSceneGraphWalker.ParentBoneShapeGroup> holsterAttachmentGroups = [];
        if (npc.WeaponVisual.AttachmentMode == WeaponAttachmentMode.HolsterPose)
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

            if (visAnalysis.VisControlledShapeIndices.Count > 0)
            {
                Log.Debug(
                    "Weapon '{0}': holster mode — excluding {1} vis-controlled shapes",
                    npc.WeaponVisual.MeshPath,
                    visAnalysis.VisControlledShapeIndices.Count);
            }

            if (holsterAttachmentGroups.Count > 0)
            {
                Log.Debug(
                    "Weapon '{0}': holster mode — found {1} attachment groups ({2})",
                    npc.WeaponVisual.MeshPath,
                    holsterAttachmentGroups.Count,
                    string.Join(
                        ", ",
                        holsterAttachmentGroups.Select(group => $"{group.SourceNodeName}->{group.BoneName}")));
            }
        }

        Dictionary<string, NifAnimationParser.AnimPoseOverride>? holsterModelPoseOverrides = null;
        var renderOnlyExplicitHolsterAttachmentGroups =
            npc.WeaponVisual.AttachmentMode == WeaponAttachmentMode.HolsterPose &&
            holsterAttachmentGroups.Count > 0;
        if (renderOnlyExplicitHolsterAttachmentGroups)
        {
            holsterModelPoseOverrides = NifNodeControllerPoseReader.Parse(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                true);
            if (holsterModelPoseOverrides != null)
            {
                Log.Debug(
                    "Weapon '{0}': sampled {1} embedded transform-controller poses [{2}]",
                    npc.WeaponVisual.MeshPath,
                    holsterModelPoseOverrides.Count,
                    string.Join(", ", holsterModelPoseOverrides.Keys.OrderBy(name => name)));
            }

            Log.Debug(
                "Weapon '{0}': holster mode - suppressing remaining root-attached geometry; rendering explicit attachment groups only",
                npc.WeaponVisual.MeshPath);
        }

        var weaponModel = renderOnlyExplicitHolsterAttachmentGroups
            ? null
            : NifGeometryExtractor.Extract(
                weaponRaw.Value.Data, weaponRaw.Value.Info, textureResolver,
                excludeBlockIndices: mainWeaponExcludedShapes);
        if ((weaponModel == null || !weaponModel.HasGeometry) && holsterAttachmentGroups.Count == 0)
        {
            Log.Warn("Weapon NIF has no geometry: {0}", npc.WeaponVisual.MeshPath);
            return;
        }

        string? modelAnchorCompensationLabel = null;
        if (weaponModel != null &&
            weaponModel.HasGeometry &&
            TryResolveModelAttachmentCompensation(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                "Weapon",
                out var modelAnchorCompensation,
                out var compensationKind) &&
            ShouldApplyWeaponModelAttachmentCompensation(
                npc.WeaponVisual.AttachmentMode,
                compensationKind))
        {
            NpcRenderHelpers.TransformModel(weaponModel, modelAnchorCompensation);
            modelAnchorCompensationLabel = compensationKind == ModelAttachmentCompensationKind.ExplicitAttachmentNode
                ? " + model Weapon anchor compensation"
                : " + model root fallback compensation";
        }

        if (weaponModel != null && weaponModel.HasGeometry)
        {
            NpcRenderHelpers.TransformModel(weaponModel, weaponBoneTransform.Value);

            Log.Debug("Weapon '{0}' ({1}): node '{2}' at ({3:F1},{4:F1},{5:F1}){6}, {7} submeshes",
                npc.WeaponVisual.MeshPath, npc.WeaponVisual.WeaponType,
                attachmentNodeName,
                weaponBoneTransform.Value.Translation.X, weaponBoneTransform.Value.Translation.Y,
                weaponBoneTransform.Value.Translation.Z,
                attachmentSourceLabel + (modelAnchorCompensationLabel ?? string.Empty),
                weaponModel.Submeshes.Count);

            foreach (var sub in weaponModel.Submeshes)
            {
                sub.RenderOrder = 6;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }

        if (npc.WeaponVisual.AttachmentMode != WeaponAttachmentMode.HolsterPose ||
            holsterAttachmentGroups.Count == 0 ||
            holsterPose == null)
        {
            return;
        }

        var allShapeIndices = FindShapeBlockIndices(weaponRaw.Value.Data, weaponRaw.Value.Info);
        foreach (var group in holsterAttachmentGroups)
        {
            var groupAttachmentTransform = ResolveWeaponHolsterAttachmentTransform(holsterPose, group.BoneName);
            if (!groupAttachmentTransform.HasValue)
            {
                Log.Warn(
                    "Weapon attachment group omitted for NPC 0x{0:X8}: no holster attachment node '{1}' for weapon '{2}'",
                    npc.NpcFormId,
                    group.BoneName,
                    npc.WeaponVisual.MeshPath);
                continue;
            }

            var groupExcludedShapes = new HashSet<int>(allShapeIndices);
            groupExcludedShapes.ExceptWith(group.ShapeIndices);

            var groupModel = NifGeometryExtractor.Extract(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                textureResolver,
                excludeBlockIndices: groupExcludedShapes,
                animOverrides: holsterModelPoseOverrides);
            if (groupModel == null || !groupModel.HasGeometry)
            {
                Log.Warn(
                    "Weapon attachment group '{0}' had no geometry for weapon '{1}'",
                    group.SourceNodeName,
                    npc.WeaponVisual.MeshPath);
                continue;
            }

            NpcRenderHelpers.TransformModel(groupModel, groupAttachmentTransform.Value);

            Log.Debug(
                "Weapon '{0}' group '{1}' -> '{2}' at ({3:F1},{4:F1},{5:F1}), {6} submeshes",
                npc.WeaponVisual.MeshPath,
                group.SourceNodeName,
                group.BoneName,
                groupAttachmentTransform.Value.Translation.X,
                groupAttachmentTransform.Value.Translation.Y,
                groupAttachmentTransform.Value.Translation.Z,
                groupModel.Submeshes.Count);

            foreach (var sub in groupModel.Submeshes)
            {
                sub.RenderOrder = 6;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }
    }

    internal static bool IsRigidEquipmentModel(byte[] data, NifInfo nif)
    {
        var extracted = NifExportExtractor.Extract(data, nif);
        return extracted.MeshParts.Count > 0 &&
               extracted.MeshParts.All(part => part.Skin == null);
    }

    private static void LoadWeaponAddonMeshes(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? idleBoneTransforms,
        NifRenderableModel bodyModel)
    {
        if (idleBoneTransforms == null || npc.WeaponVisual?.AddonMeshes is not { Count: > 0 })
        {
            return;
        }

        foreach (var addon in npc.WeaponVisual.AddonMeshes)
        {
            var addonRaw = NpcRenderHelpers.LoadNifRawFromBsa(addon.MeshPath, meshArchives);
            if (addonRaw == null)
            {
                Log.Warn("Weapon addon NIF failed to load: {0}", addon.MeshPath);
                continue;
            }

            var addonModel = NifGeometryExtractor.Extract(
                addonRaw.Value.Data,
                addonRaw.Value.Info,
                textureResolver,
                externalBoneTransforms: idleBoneTransforms,
                useDualQuaternionSkinning: true);
            if (addonModel == null || !addonModel.HasGeometry)
            {
                Log.Warn("Weapon addon NIF has no geometry: {0}", addon.MeshPath);
                continue;
            }

            Log.Debug("Weapon addon '{0}': {1} submeshes, slots=0x{2:X}",
                addon.MeshPath,
                addonModel.Submeshes.Count,
                addon.BipedFlags);

            foreach (var sub in addonModel.Submeshes)
            {
                sub.RenderOrder = 6;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }
    }

    internal static bool TryResolveWeaponAttachmentNode(
        WeaponVisual weaponVisual,
        out string attachmentNodeName,
        out string? omitReason)
    {
        if (!string.IsNullOrWhiteSpace(weaponVisual.EmbeddedWeaponNode))
        {
            attachmentNodeName = weaponVisual.EmbeddedWeaponNode.Trim();
            omitReason = null;
            return true;
        }

        if (weaponVisual.IsEmbeddedWeapon)
        {
            attachmentNodeName = "";
            omitReason =
                $"embedded weapon '{weaponVisual.EditorId ?? weaponVisual.MeshPath ?? "?"}' has no attachment node";
            return false;
        }

        attachmentNodeName = "Weapon";
        omitReason = null;
        return true;
    }

    internal static bool TryResolveEquippedWeaponAttachmentTransform(
        WeaponVisual weaponVisual,
        Dictionary<string, Matrix4x4> idleBoneTransforms,
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform,
        out string? omitReason)
    {
        if (weaponVisual.WeaponType == WeaponType.HandToHandMelee &&
            TryResolveHandToHandProcessWeaponAttachmentTransform(
                idleBoneTransforms,
                skeletonNifPath,
                meshArchives,
                !string.IsNullOrWhiteSpace(weaponVisual.EquippedPoseKfPath),
                weaponVisual.PreferEquippedForearmMount,
                out attachmentNodeName,
                out attachmentTransform,
                out omitReason))
        {
            return true;
        }

        return TryResolveEquippedWeaponAttachmentTransform(
            weaponVisual,
            idleBoneTransforms,
            out attachmentNodeName,
            out attachmentTransform,
            out omitReason);
    }

    internal static bool TryResolveEquippedWeaponAttachmentTransform(
        WeaponVisual weaponVisual,
        Dictionary<string, Matrix4x4> idleBoneTransforms,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform,
        out string? omitReason)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        if (weaponVisual.IsEmbeddedWeapon && string.IsNullOrWhiteSpace(weaponVisual.EmbeddedWeaponNode))
        {
            omitReason =
                $"embedded weapon '{weaponVisual.EditorId ?? weaponVisual.MeshPath ?? "?"}' has no attachment node";
            return false;
        }

        var candidates = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var trimmed = candidate.Trim();
            if (seen.Add(trimmed))
            {
                candidates.Add(trimmed);
            }
        }

        AddCandidate(weaponVisual.EmbeddedWeaponNode);
        AddCandidate("Weapon");
        AddCandidate("Bip01 R Hand");
        AddCandidate("Bip01 R ForeTwist");

        foreach (var candidate in candidates)
        {
            if (idleBoneTransforms.TryGetValue(candidate, out attachmentTransform))
            {
                attachmentNodeName = candidate;
                omitReason = null;
                return true;
            }
        }

        omitReason =
            $"no equipped attachment node in base pose for {weaponVisual.MeshPath ?? weaponVisual.EditorId ?? "?"}";
        return false;
    }

    internal static bool TryResolveEquipmentAttachmentTransform(
        EquippedItem item,
        IReadOnlyDictionary<string, Matrix4x4> idleBoneTransforms,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform,
        out string? omitReason)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        string[] candidates = item.AttachmentMode switch
        {
            EquipmentAttachmentMode.LeftWristRigid => ["Bip01 L ForeTwist", "Bip01 L Forearm", "Bip01 L Hand"],
            EquipmentAttachmentMode.RightWristRigid => ["Bip01 R ForeTwist", "Bip01 R Forearm", "Bip01 R Hand"],
            _ => []
        };

        foreach (var candidate in candidates)
        {
            if (!idleBoneTransforms.TryGetValue(candidate, out attachmentTransform))
            {
                continue;
            }

            attachmentNodeName = candidate;
            omitReason = null;
            return true;
        }

        omitReason = $"no wrist attachment node in base pose for {item.MeshPath}";
        return false;
    }

    internal static bool TryResolveHandToHandProcessWeaponAttachmentTransform(
        IReadOnlyDictionary<string, Matrix4x4> idleBoneTransforms,
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        bool preferProcessStyleRebuild,
        bool preferEquippedForearmMount,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform,
        out string? omitReason)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        if (skeletonNifPath == null)
        {
            omitReason = "missing skeleton for hand-to-hand attachment";
            return false;
        }

        var skelRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
        {
            omitReason = $"failed to load skeleton '{skeletonNifPath}' for hand-to-hand attachment";
            return false;
        }

        // StoreBonePointers/GetWeaponBone resolves the named Weapon node from the
        // currently posed scene graph. Only trust that direct Weapon node when we
        // have a known equipped-pose source; otherwise reconstruct from the held
        // hand/foretwist parents to avoid using a chest-space bind/default node.
        if (preferProcessStyleRebuild &&
            idleBoneTransforms.TryGetValue("Weapon", out attachmentTransform))
        {
            Log.Debug(
                "Weapon bone matrix:\n  [{0:F3},{1:F3},{2:F3},{3:F3}]\n  [{4:F3},{5:F3},{6:F3},{7:F3}]\n  [{8:F3},{9:F3},{10:F3},{11:F3}]\n  [{12:F3},{13:F3},{14:F3},{15:F3}]",
                attachmentTransform.M11, attachmentTransform.M12, attachmentTransform.M13, attachmentTransform.M14,
                attachmentTransform.M21, attachmentTransform.M22, attachmentTransform.M23, attachmentTransform.M24,
                attachmentTransform.M31, attachmentTransform.M32, attachmentTransform.M33, attachmentTransform.M34,
                attachmentTransform.M41, attachmentTransform.M42, attachmentTransform.M43, attachmentTransform.M44);
            if (idleBoneTransforms.TryGetValue("Bip01 R Hand", out var handTransform))
            {
                Log.Debug(
                    "Bip01 R Hand matrix:\n  [{0:F3},{1:F3},{2:F3},{3:F3}]\n  [{4:F3},{5:F3},{6:F3},{7:F3}]\n  [{8:F3},{9:F3},{10:F3},{11:F3}]\n  [{12:F3},{13:F3},{14:F3},{15:F3}]",
                    handTransform.M11, handTransform.M12, handTransform.M13, handTransform.M14,
                    handTransform.M21, handTransform.M22, handTransform.M23, handTransform.M24,
                    handTransform.M31, handTransform.M32, handTransform.M33, handTransform.M34,
                    handTransform.M41, handTransform.M42, handTransform.M43, handTransform.M44);
            }

            attachmentNodeName = "Weapon (posed scene graph)";
            omitReason = null;
            return true;
        }

        if (!TryReadNodeLocalTransform(skelRaw.Value.Data, skelRaw.Value.Info, "Weapon",
                out var bindLocalWeaponTransform))
        {
            omitReason = "missing Weapon node in skeleton for hand-to-hand attachment";
            return false;
        }

        var equipOverrides = LoadNamedAnimationOverrides(
            skeletonNifPath,
            meshArchives,
            "h2hequip.kf",
            true);
        var equipParentOverrideBone = TryLoadSequenceParentBoneName(
            skeletonNifPath,
            meshArchives,
            "h2hequip.kf");
        var parentBoneCandidates = ResolveHandToHandWeaponParentBoneCandidates(
            skeletonNifPath,
            meshArchives,
            preferEquippedForearmMount);

        if (TryResolveHandToHandProcessStyleWeaponAttachmentTransform(
                bindLocalWeaponTransform,
                idleBoneTransforms,
                parentBoneCandidates,
                equipOverrides,
                equipParentOverrideBone,
                skelRaw.Value.Data,
                skelRaw.Value.Info,
                out attachmentNodeName,
                out attachmentTransform))
        {
            omitReason = null;
            return true;
        }

        if (idleBoneTransforms.TryGetValue("Weapon", out attachmentTransform))
        {
            attachmentNodeName = "Weapon";
            omitReason = null;
            return true;
        }

        omitReason = "no process-style hand-to-hand attachment parent in equipped arm pose";
        return false;
    }

    internal static bool TryResolveHandToHandProcessStyleWeaponAttachmentTransform(
        Matrix4x4 bindLocalWeaponTransform,
        IReadOnlyDictionary<string, Matrix4x4> idleBoneTransforms,
        IReadOnlyList<string> parentBoneCandidates,
        IReadOnlyDictionary<string, NifAnimationParser.AnimPoseOverride>? equipOverrides,
        string? equipParentOverrideBone,
        byte[] skeletonData,
        NifInfo skeletonInfo,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        foreach (var candidate in parentBoneCandidates)
        {
            if (equipOverrides is { Count: > 0 } &&
                !string.IsNullOrWhiteSpace(equipParentOverrideBone) &&
                string.Equals(candidate, equipParentOverrideBone, StringComparison.OrdinalIgnoreCase))
            {
                var animatedAttachmentTransform = ResolveWeaponHolsterAttachmentTransform(
                    idleBoneTransforms,
                    equipOverrides,
                    skeletonData,
                    skeletonInfo,
                    "Weapon",
                    candidate,
                    idleBoneTransforms);
                if (animatedAttachmentTransform.HasValue)
                {
                    attachmentNodeName = $"Weapon via {candidate} (equip local)";
                    attachmentTransform = animatedAttachmentTransform.Value;
                    return true;
                }
            }

            if (!idleBoneTransforms.TryGetValue(candidate, out var parentWorldTransform))
            {
                continue;
            }

            attachmentNodeName = $"Weapon via {candidate}";
            attachmentTransform = bindLocalWeaponTransform * parentWorldTransform;
            return true;
        }

        return false;
    }

    internal static bool TryResolveProcessStyleWeaponAttachmentTransform(
        Matrix4x4 bindLocalWeaponTransform,
        IReadOnlyDictionary<string, Matrix4x4> idleBoneTransforms,
        IReadOnlyList<string> parentBoneCandidates,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        foreach (var candidate in parentBoneCandidates)
        {
            if (!idleBoneTransforms.TryGetValue(candidate, out var parentWorldTransform))
            {
                continue;
            }

            attachmentNodeName = $"Weapon via {candidate}";
            attachmentTransform = bindLocalWeaponTransform * parentWorldTransform;
            return true;
        }

        return false;
    }

    internal static IReadOnlyList<string> ResolveHandToHandWeaponParentBoneCandidates(
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        bool preferEquippedForearmMount = false)
    {
        var candidates = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                candidates.Add(trimmed);
            }
        }

        var idleParentBone = TryLoadSequenceParentBoneName(
            skeletonNifPath,
            meshArchives,
            "h2hidle.kf");
        var equipParentBone = TryLoadSequenceParentBoneName(
            skeletonNifPath,
            meshArchives,
            "h2hequip.kf");

        if (preferEquippedForearmMount)
        {
            AddCandidate(equipParentBone);
            AddCandidate(idleParentBone);
            AddCandidate("Bip01 R ForeTwist");
            AddCandidate("Bip01 R Hand");
        }
        else
        {
            AddCandidate(idleParentBone);
            AddCandidate(equipParentBone);
            AddCandidate("Bip01 R Hand");
            AddCandidate("Bip01 R ForeTwist");
        }

        return candidates;
    }

    private static string? TryLoadSequenceParentBoneName(
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        string kfRelPath)
    {
        if (skeletonNifPath == null)
        {
            return null;
        }

        var skelDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        var kfPath = skelDir + kfRelPath;
        var raw = NpcRenderHelpers.LoadNifRawFromBsa(kfPath, meshArchives, true);

        if (raw == null && skelDir.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            var malePath = skelDir.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase) + kfRelPath;
            raw = NpcRenderHelpers.LoadNifRawFromBsa(malePath, meshArchives, true);
        }

        return raw != null
            ? TryParseSequenceParentBoneName(raw.Value.Info)
            : null;
    }

    /// <summary>
    ///     Loads the weapon holster animation KF and computes skeleton bone transforms with it
    ///     layered over the base idle animation. Also records the KF's parent override bone
    ///     (from "prn:" text keys) so attachment nodes can be reparented correctly.
    /// </summary>
    private static WeaponHolsterPose? LoadWeaponHolsterPose(
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        string holsterProfileKey,
        bool usePowerArmorHolster)
    {
        return LoadWeaponAttachmentPose(
            skeletonNifPath,
            meshArchives,
            BuildHolsterKfRelPath(holsterProfileKey, usePowerArmorHolster),
            false,
            "holster");
    }

    private static WeaponHolsterPose? LoadWeaponAttachmentPose(
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        string kfRelPath,
        bool sampleLastKeyframe,
        string poseLabel)
    {
        if (skeletonNifPath == null)
            return null;

        var skelDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        var kfPath = skelDir + kfRelPath;

        var kfRaw = NpcRenderHelpers.LoadNifRawFromBsa(kfPath, meshArchives, true);

        // Femaleâ†’male fallback (same pattern as base idle loading)
        if (kfRaw == null && skelDir.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            var malePath = skelDir.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase) + kfRelPath;
            kfRaw = NpcRenderHelpers.LoadNifRawFromBsa(malePath, meshArchives, true);
        }

        if (kfRaw == null)
            return null;

        var attachmentOverrides = NifAnimationParser.ParseIdlePoseOverrides(
            kfRaw.Value.Data,
            kfRaw.Value.Info,
            sampleLastKeyframe);
        if (attachmentOverrides == null || attachmentOverrides.Count == 0)
            return null;

        // Load skeleton and base idle overrides, then layer holster overrides on top.
        // Priority-based: holster KF wins for bones it defines, base idle fills the rest.
        var skelRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
            return null;

        var baseIdle = LoadIdleAnimationOverrides(skeletonNifPath, meshArchives, skelRaw);
        var merged = MergePoseOverrides(baseIdle, attachmentOverrides);

        Log.Debug("Weapon {0} KF '{1}': {2} overrides ({3} base + {4} anim)",
            poseLabel, kfRelPath, merged.Count, baseIdle?.Count ?? 0, attachmentOverrides.Count);
        var parentOverrideBone = TryParseSequenceParentBoneName(kfRaw.Value.Info);
        if (parentOverrideBone != null)
        {
            Log.Debug("Weapon {0} KF '{1}': parent override '{2}'", poseLabel, kfRelPath, parentOverrideBone);
        }

        return new WeaponHolsterPose(
            NifGeometryExtractor.ExtractNamedBoneTransforms(
                skelRaw.Value.Data,
                skelRaw.Value.Info,
                merged),
            attachmentOverrides,
            skelRaw.Value.Data,
            skelRaw.Value.Info,
            parentOverrideBone);
    }

    internal static Matrix4x4? ResolveWeaponHolsterAttachmentTransform(
        IReadOnlyDictionary<string, Matrix4x4> worldTransforms,
        IReadOnlyDictionary<string, NifAnimationParser.AnimPoseOverride> holsterOverrides,
        byte[] skeletonData,
        NifInfo skeletonInfo,
        string attachmentNodeName,
        string? parentOverrideBone,
        IReadOnlyDictionary<string, Matrix4x4>? parentWorldTransforms = null)
    {
        worldTransforms.TryGetValue(attachmentNodeName, out var defaultWorldTransform);

        if (string.IsNullOrWhiteSpace(parentOverrideBone) ||
            !holsterOverrides.TryGetValue(attachmentNodeName, out var attachmentOverride) ||
            !TryReadNodeLocalTransform(skeletonData, skeletonInfo, attachmentNodeName, out var bindLocalTransform))
        {
            return worldTransforms.ContainsKey(attachmentNodeName)
                ? defaultWorldTransform
                : null;
        }

        var parentTransforms = parentWorldTransforms ?? worldTransforms;
        if (!parentTransforms.TryGetValue(parentOverrideBone, out var parentWorldTransform))
        {
            return worldTransforms.ContainsKey(attachmentNodeName)
                ? defaultWorldTransform
                : null;
        }

        return ApplyPoseOverride(bindLocalTransform, attachmentOverride) * parentWorldTransform;
    }

    internal static Matrix4x4? ResolveWeaponHolsterAttachmentTransform(
        WeaponHolsterPose holsterPose,
        string attachmentNodeName)
    {
        return ResolveWeaponHolsterAttachmentTransform(
            holsterPose.WorldTransforms,
            holsterPose.HolsterOverrides,
            holsterPose.SkeletonData,
            holsterPose.SkeletonInfo,
            attachmentNodeName,
            holsterPose.ParentOverrideBone);
    }

    internal static string? TryParseSequenceParentBoneName(NifInfo nif)
    {
        foreach (var value in nif.Strings)
        {
            if (!value.StartsWith("prn:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parentName = value[4..].Trim();
            if (parentName.Length > 0)
            {
                return parentName;
            }
        }

        return null;
    }

    private static Dictionary<string, NifAnimationParser.AnimPoseOverride> MergePoseOverrides(
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? baseIdle,
        Dictionary<string, NifAnimationParser.AnimPoseOverride> holsterOverrides)
    {
        var merged = new Dictionary<string, NifAnimationParser.AnimPoseOverride>(StringComparer.OrdinalIgnoreCase);
        if (baseIdle != null)
        {
            foreach (var (bone, pose) in baseIdle)
            {
                merged[bone] = pose;
            }
        }

        foreach (var (bone, pose) in holsterOverrides)
        {
            merged[bone] = pose;
        }

        return merged;
    }

    private static bool TryReadNodeLocalTransform(
        byte[] data,
        NifInfo nif,
        string nodeName,
        out Matrix4x4 localTransform)
    {
        localTransform = Matrix4x4.Identity;
        foreach (var block in nif.Blocks)
        {
            if (!NifSceneGraphWalker.NodeTypes.Contains(block.TypeName))
            {
                continue;
            }

            var name = NifBlockParsers.ReadBlockName(data, block, nif);
            if (!string.Equals(name, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            localTransform = NifBlockParsers.ParseNiAVObjectTransform(data, block, nif.BsVersion, nif.IsBigEndian);
            return true;
        }

        return false;
    }

    internal static bool TryResolveModelAttachmentCompensation(
        byte[] data,
        NifInfo nif,
        string nodeName,
        out Matrix4x4 compensationTransform,
        out ModelAttachmentCompensationKind compensationKind,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        compensationTransform = Matrix4x4.Identity;
        compensationKind = ModelAttachmentCompensationKind.ExplicitAttachmentNode;

        var namedTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(data, nif, animOverrides);
        if (!namedTransforms.TryGetValue(nodeName, out var attachmentWorldTransform))
        {
            if (!TryReadNodeLocalTransform(data, nif, nodeName, out attachmentWorldTransform))
            {
                if (!namedTransforms.TryGetValue(NifGeometryExtractor.RootTransformKey, out attachmentWorldTransform) ||
                    IsNearlyIdentityTransform(attachmentWorldTransform))
                {
                    return false;
                }

                compensationKind = ModelAttachmentCompensationKind.RootFallback;
            }
        }

        return Matrix4x4.Invert(attachmentWorldTransform, out compensationTransform);
    }

    internal static bool TryResolveModelAttachmentCompensation(
        byte[] data,
        NifInfo nif,
        string nodeName,
        out Matrix4x4 compensationTransform,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        return TryResolveModelAttachmentCompensation(
            data,
            nif,
            nodeName,
            out compensationTransform,
            out _,
            animOverrides);
    }

    internal static bool ShouldApplyWeaponModelAttachmentCompensation(
        WeaponAttachmentMode attachmentMode,
        ModelAttachmentCompensationKind compensationKind)
    {
        return compensationKind == ModelAttachmentCompensationKind.ExplicitAttachmentNode ||
               attachmentMode != WeaponAttachmentMode.HolsterPose;
    }

    internal static HashSet<int> FindShapeBlockIndices(byte[] data, NifInfo nif)
    {
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap);
        return shapeDataMap.Keys.ToHashSet();
    }

    internal static bool TryResolveShapeGroupAttachmentCompensation(
        byte[] data,
        NifInfo nif,
        IReadOnlyCollection<int> shapeIndices,
        out Matrix4x4 compensationTransform,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        compensationTransform = Matrix4x4.Identity;
        if (shapeIndices.Count != 1)
        {
            return false;
        }

        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var worldTransforms = new Dictionary<int, Matrix4x4>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap);
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, worldTransforms, animOverrides);

        var shapeIndex = shapeIndices.First();
        if (!worldTransforms.TryGetValue(shapeIndex, out var shapeWorldTransform) ||
            IsNearlyIdentityTransform(shapeWorldTransform))
        {
            return false;
        }

        return Matrix4x4.Invert(shapeWorldTransform, out compensationTransform);
    }

    internal static bool IsNearlyIdentityTransform(Matrix4x4 transform)
    {
        return MathF.Abs(transform.M11 - 1f) < 0.0001f &&
               MathF.Abs(transform.M22 - 1f) < 0.0001f &&
               MathF.Abs(transform.M33 - 1f) < 0.0001f &&
               MathF.Abs(transform.M44 - 1f) < 0.0001f &&
               MathF.Abs(transform.M12) < 0.0001f &&
               MathF.Abs(transform.M13) < 0.0001f &&
               MathF.Abs(transform.M14) < 0.0001f &&
               MathF.Abs(transform.M21) < 0.0001f &&
               MathF.Abs(transform.M23) < 0.0001f &&
               MathF.Abs(transform.M24) < 0.0001f &&
               MathF.Abs(transform.M31) < 0.0001f &&
               MathF.Abs(transform.M32) < 0.0001f &&
               MathF.Abs(transform.M34) < 0.0001f &&
               MathF.Abs(transform.M41) < 0.0001f &&
               MathF.Abs(transform.M42) < 0.0001f &&
               MathF.Abs(transform.M43) < 0.0001f;
    }

    private static Matrix4x4 ApplyPoseOverride(
        Matrix4x4 bindLocalTransform,
        NifAnimationParser.AnimPoseOverride anim)
    {
        var tx = anim.HasTranslation ? anim.Tx : bindLocalTransform.M41;
        var ty = anim.HasTranslation ? anim.Ty : bindLocalTransform.M42;
        var tz = anim.HasTranslation ? anim.Tz : bindLocalTransform.M43;

        var bindScale = anim.HasScale
            ? anim.Scale
            : MathF.Sqrt(bindLocalTransform.M11 * bindLocalTransform.M11 +
                         bindLocalTransform.M21 * bindLocalTransform.M21 +
                         bindLocalTransform.M31 * bindLocalTransform.M31);

        var rot = Matrix4x4.CreateFromQuaternion(anim.Rotation);
        return new Matrix4x4(
            rot.M11 * bindScale, rot.M12 * bindScale, rot.M13 * bindScale, 0,
            rot.M21 * bindScale, rot.M22 * bindScale, rot.M23 * bindScale, 0,
            rot.M31 * bindScale, rot.M32 * bindScale, rot.M33 * bindScale, 0,
            tx, ty, tz, 1);
    }

    private static string BuildHolsterKfRelPath(string holsterProfileKey, bool usePowerArmorHolster)
    {
        return usePowerArmorHolster
            ? $"PA{holsterProfileKey}Holster.kf"
            : $"{holsterProfileKey}Holster.kf";
    }

    private static bool HasPowerArmorTorso(IEnumerable<EquippedItem>? equippedItems)
    {
        if (equippedItems == null)
        {
            return false;
        }

        foreach (var item in equippedItems)
        {
            if ((item.BipedFlags & 0x04) != 0 && item.IsPowerArmor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Builds a geometric visualization of the skeleton: small octahedra at joints,
    ///     tapered bone sticks between parent-child pairs, color-coded by body region.
    /// </summary>
    private static NifRenderableModel BuildSkeletonModel(NifGeometryExtractor.SkeletonHierarchy skeleton)
    {
        var model = new NifRenderableModel();
        var positions = new List<float>();
        var indices = new List<ushort>();
        var colors = new List<byte>();

        static (byte R, byte G, byte B) GetBoneColor(string name)
        {
            var n = name.ToUpperInvariant();
            if (n.Contains("HEAD") || n.Contains("HAIR")) return (255, 255, 0);
            if (n.Contains("L CLAVICLE") || n.Contains("L UPPERARM") ||
                n.Contains("L FOREARM") || n.Contains("L HAND") ||
                n.Contains("L FINGER") || n.Contains("L FORE")) return (0, 200, 0);
            if (n.Contains("R CLAVICLE") || n.Contains("R UPPERARM") ||
                n.Contains("R FOREARM") || n.Contains("R HAND") ||
                n.Contains("R FINGER") || n.Contains("R FORE")) return (200, 50, 50);
            if (n.Contains("L THIGH") || n.Contains("L CALF") ||
                n.Contains("L FOOT") || n.Contains("L TOE")) return (0, 200, 200);
            if (n.Contains("R THIGH") || n.Contains("R CALF") ||
                n.Contains("R FOOT") || n.Contains("R TOE")) return (200, 0, 200);
            if (n.Contains("SPINE") || n.Contains("NECK")) return (80, 130, 255);
            if (n.Contains("WEAPON") || n.Contains("CAMERA")) return (255, 160, 0);
            return (220, 220, 220);
        }

        void AddJoint(Vector3 pos, float radius, (byte R, byte G, byte B) color)
        {
            var baseIdx = (ushort)(positions.Count / 3);
            float[] offsets =
            [
                radius, 0, 0, -radius, 0, 0, 0, radius, 0,
                0, -radius, 0, 0, 0, radius, 0, 0, -radius
            ];
            for (var i = 0; i < 6; i++)
            {
                positions.Add(pos.X + offsets[i * 3]);
                positions.Add(pos.Y + offsets[i * 3 + 1]);
                positions.Add(pos.Z + offsets[i * 3 + 2]);
                colors.AddRange([color.R, color.G, color.B, 255]);
            }

            ushort[] faces =
            [
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 0), (ushort)(baseIdx + 2),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 2), (ushort)(baseIdx + 1),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 1), (ushort)(baseIdx + 3),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 3), (ushort)(baseIdx + 0),
                (ushort)(baseIdx + 5), (ushort)(baseIdx + 2), (ushort)(baseIdx + 0),
                (ushort)(baseIdx + 5), (ushort)(baseIdx + 1), (ushort)(baseIdx + 2),
                (ushort)(baseIdx + 5), (ushort)(baseIdx + 3), (ushort)(baseIdx + 1),
                (ushort)(baseIdx + 5), (ushort)(baseIdx + 0), (ushort)(baseIdx + 3)
            ];
            indices.AddRange(faces);
        }

        void AddBoneStick(Vector3 from, Vector3 to,
            float widthFrom, float widthTo, (byte R, byte G, byte B) color)
        {
            var baseIdx = (ushort)(positions.Count / 3);
            var dir = to - from;
            var len = dir.Length();
            if (len < 0.001f) return;

            dir = Vector3.Normalize(dir);
            var up = MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
            var perpA = Vector3.Normalize(Vector3.Cross(dir, up));
            var perpB = Vector3.Cross(dir, perpA);

            Vector3[] offsets =
            [
                perpA * widthFrom + perpB * widthFrom,
                perpA * widthFrom - perpB * widthFrom,
                -perpA * widthFrom - perpB * widthFrom,
                -perpA * widthFrom + perpB * widthFrom,
                perpA * widthTo + perpB * widthTo,
                perpA * widthTo - perpB * widthTo,
                -perpA * widthTo - perpB * widthTo,
                -perpA * widthTo + perpB * widthTo
            ];
            for (var i = 0; i < 4; i++)
            {
                var v = from + offsets[i];
                positions.Add(v.X);
                positions.Add(v.Y);
                positions.Add(v.Z);
                colors.AddRange([color.R, color.G, color.B, 255]);
            }

            for (var i = 4; i < 8; i++)
            {
                var v = to + offsets[i];
                positions.Add(v.X);
                positions.Add(v.Y);
                positions.Add(v.Z);
                colors.AddRange([color.R, color.G, color.B, 255]);
            }

            ushort[] tris =
            [
                baseIdx, (ushort)(baseIdx + 1), (ushort)(baseIdx + 5),
                baseIdx, (ushort)(baseIdx + 5), (ushort)(baseIdx + 4),
                (ushort)(baseIdx + 1), (ushort)(baseIdx + 2), (ushort)(baseIdx + 6),
                (ushort)(baseIdx + 1), (ushort)(baseIdx + 6), (ushort)(baseIdx + 5),
                (ushort)(baseIdx + 2), (ushort)(baseIdx + 3), (ushort)(baseIdx + 7),
                (ushort)(baseIdx + 2), (ushort)(baseIdx + 7), (ushort)(baseIdx + 6),
                (ushort)(baseIdx + 3), baseIdx, (ushort)(baseIdx + 4),
                (ushort)(baseIdx + 3), (ushort)(baseIdx + 4), (ushort)(baseIdx + 7),
                baseIdx, (ushort)(baseIdx + 2), (ushort)(baseIdx + 1),
                baseIdx, (ushort)(baseIdx + 3), (ushort)(baseIdx + 2),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 5), (ushort)(baseIdx + 6),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 6), (ushort)(baseIdx + 7)
            ];
            indices.AddRange(tris);
        }

        foreach (var (name, transform) in skeleton.BoneTransforms)
        {
            var pos = new Vector3(transform.M41, transform.M42, transform.M43);
            AddJoint(pos, 0.8f, GetBoneColor(name));
        }

        foreach (var (parentName, childName) in skeleton.BoneLinks)
        {
            if (!skeleton.BoneTransforms.TryGetValue(parentName, out var parentXform)) continue;
            if (!skeleton.BoneTransforms.TryGetValue(childName, out var childXform)) continue;

            var from = new Vector3(parentXform.M41, parentXform.M42, parentXform.M43);
            var to = new Vector3(childXform.M41, childXform.M42, childXform.M43);
            AddBoneStick(from, to, 0.5f, 0.3f, GetBoneColor(childName));
        }

        if (positions.Count == 0) return model;

        var sub = new RenderableSubmesh
        {
            Positions = positions.ToArray(),
            Triangles = indices.ToArray(),
            VertexColors = colors.ToArray(),
            UseVertexColors = true,
            IsDoubleSided = true,
            RenderOrder = 0
        };
        model.Submeshes.Add(sub);
        model.ExpandBounds(sub.Positions);
        return model;
    }

    internal sealed record WeaponHolsterPose(
        Dictionary<string, Matrix4x4> WorldTransforms,
        Dictionary<string, NifAnimationParser.AnimPoseOverride> HolsterOverrides,
        byte[] SkeletonData,
        NifInfo SkeletonInfo,
        string? ParentOverrideBone);

    internal enum ModelAttachmentCompensationKind
    {
        ExplicitAttachmentNode,
        RootFallback
    }
}
