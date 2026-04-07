using System.Numerics;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Equipment slot logic, armor/weapon attachment, holster pose resolution.
/// </summary>
internal static class NpcEquipmentAttacher
{
    private static readonly Logger Log = Logger.Instance;

    internal static void LoadEquipment(
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
            if (NpcTextureHelpers.IsHeadEquipment(item.BipedFlags))
                continue;

            if ((item.BipedFlags & suppressedEquipmentSlots) != 0)
            {
                Log.Debug("Equipment '{0}' suppressed due to active weapon-addon slot overlap 0x{1:X}",
                    item.MeshPath,
                    item.BipedFlags & suppressedEquipmentSlots);
                continue;
            }

            var equipRaw = NpcMeshHelpers.LoadNifRawFromBsa(item.MeshPath, meshArchives);
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
                if (!NpcWeaponAttachmentResolver.TryResolveEquipmentAttachmentTransform(
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

            Log.Debug("Equipment '{0}': {1} submeshes, bounds ({2:F2},{3:F2},{4:F2})\u2192({5:F2},{6:F2},{7:F2})",
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
                    NpcTextureHelpers.IsEquipmentSkinSubmesh(sub.DiffuseTexturePath))
                {
                    sub.DiffuseTexturePath =
                        sub.DiffuseTexturePath!.Contains("hand", StringComparison.OrdinalIgnoreCase)
                            ? effectiveHandTex ?? effectiveBodyTex
                            : effectiveBodyTex;
                }

                sub.RenderOrder = 5;
                sub.SourceNifPath = item.MeshPath;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }
    }

    internal static void LoadWeapon(
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

        var usePowerArmorHolster = NpcWeaponAttachmentResolver.HasPowerArmorTorso(npc.EquippedItems);
        Matrix4x4? weaponBoneTransform;
        NpcWeaponAttachmentResolver.WeaponHolsterPose? holsterPose = null;
        string attachmentNodeName;
        string attachmentSourceLabel;

        switch (npc.WeaponVisual.AttachmentMode)
        {
            case WeaponAttachmentMode.EquippedHandMounted:
            {
                if (!NpcWeaponAttachmentResolver.TryResolveEquippedWeaponAttachmentTransform(
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

                if (!NpcWeaponAttachmentResolver.TryResolveWeaponAttachmentNode(npc.WeaponVisual,
                        out attachmentNodeName, out var omitReason))
                {
                    Log.Warn("Weapon omitted for NPC 0x{0:X8}: {1}", npc.NpcFormId,
                        omitReason ?? "unsupported attachment");
                    return;
                }

                holsterPose = NpcWeaponAttachmentResolver.LoadWeaponHolsterPose(
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

                weaponBoneTransform =
                    NpcWeaponAttachmentResolver.ResolveWeaponHolsterAttachmentTransform(holsterPose,
                        attachmentNodeName);
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

        var weaponRaw = NpcMeshHelpers.LoadNifRawFromBsa(
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
                    "Weapon '{0}': holster mode \u2014 excluding {1} vis-controlled shapes",
                    npc.WeaponVisual.MeshPath,
                    visAnalysis.VisControlledShapeIndices.Count);
            }

            if (holsterAttachmentGroups.Count > 0)
            {
                Log.Debug(
                    "Weapon '{0}': holster mode \u2014 found {1} attachment groups ({2})",
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
            NpcSkinningResolver.TryResolveModelAttachmentCompensation(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                "Weapon",
                out var modelAnchorCompensation,
                out var compensationKind) &&
            NpcSkinningResolver.ShouldApplyWeaponModelAttachmentCompensation(
                npc.WeaponVisual.AttachmentMode,
                compensationKind))
        {
            NpcRenderHelpers.TransformModel(weaponModel, modelAnchorCompensation);
            modelAnchorCompensationLabel = compensationKind ==
                                           NpcSkinningResolver.ModelAttachmentCompensationKind.ExplicitAttachmentNode
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

        var allShapeIndices = NpcSkinningResolver.FindShapeBlockIndices(weaponRaw.Value.Data, weaponRaw.Value.Info);
        foreach (var group in holsterAttachmentGroups)
        {
            var groupAttachmentTransform =
                NpcWeaponAttachmentResolver.ResolveWeaponHolsterAttachmentTransform(holsterPose, group.BoneName);
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

    internal static void LoadEquipmentFromPlan(
        NpcCompositionPlan plan,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NifRenderableModel bodyModel)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(bodyModel);

        var idleBoneTransforms = plan.Skeleton?.BodySkinningBones;
        foreach (var item in plan.BodyEquipment)
        {
            var equipRaw = NpcMeshHelpers.LoadNifRawFromBsa(item.MeshPath, meshArchives);
            if (equipRaw == null)
            {
                Log.Warn("Equipment NIF failed to load: {0}", item.MeshPath);
                continue;
            }

            var useRigidAttachment = item.AttachmentMode != EquipmentAttachmentMode.None &&
                                     idleBoneTransforms != null &&
                                     IsRigidEquipmentModel(equipRaw.Value.Data, equipRaw.Value.Info);

            var equipModel = NifGeometryExtractor.Extract(
                equipRaw.Value.Data,
                equipRaw.Value.Info,
                textureResolver,
                externalBoneTransforms: useRigidAttachment ? null : idleBoneTransforms,
                useDualQuaternionSkinning: !useRigidAttachment);
            if (equipModel == null || !equipModel.HasGeometry)
            {
                Log.Warn("Equipment NIF has no geometry: {0}", item.MeshPath);
                continue;
            }

            if (useRigidAttachment)
            {
                if (!NpcWeaponAttachmentResolver.TryResolveEquipmentAttachmentTransform(
                        item,
                        idleBoneTransforms!,
                        out var attachmentNodeName,
                        out var attachmentTransform,
                        out var omitReason))
                {
                    Log.Warn("Rigid equipment omitted for NPC 0x{0:X8}: {1}",
                        plan.Appearance.NpcFormId,
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

            foreach (var sub in equipModel.Submeshes)
            {
                if (plan.EffectiveBodyTexturePath != null &&
                    NpcTextureHelpers.IsEquipmentSkinSubmesh(sub.DiffuseTexturePath))
                {
                    sub.DiffuseTexturePath =
                        sub.DiffuseTexturePath!.Contains("hand", StringComparison.OrdinalIgnoreCase)
                            ? plan.EffectiveHandTexturePath ?? plan.EffectiveBodyTexturePath
                            : plan.EffectiveBodyTexturePath;
                }

                sub.RenderOrder = 5;
                sub.SourceNifPath = item.MeshPath;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }
    }

    internal static void LoadWeaponFromPlan(
        NpcCompositionPlan plan,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NifRenderableModel bodyModel)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(bodyModel);

        var weaponPlan = plan.Weapon;
        if (weaponPlan?.WeaponVisual is not { IsVisible: true, MeshPath: not null } weaponVisual)
        {
            return;
        }

        if (weaponVisual.AttachmentMode == WeaponAttachmentMode.EquippedHandMounted &&
            weaponPlan.AddonMeshes.Count > 0)
        {
            LoadWeaponAddonMeshes(
                weaponPlan.AddonMeshes,
                meshArchives,
                textureResolver,
                plan.Skeleton?.BodySkinningBones,
                bodyModel);
        }

        if (!weaponVisual.RenderStandaloneMesh)
        {
            Log.Debug("Weapon '{0}' suppressed as standalone mesh; rendering via addon meshes only",
                weaponVisual.MeshPath ?? weaponVisual.EditorId ?? "?");
            return;
        }

        if (!weaponPlan.MainAttachmentTransform.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(weaponPlan.AttachmentOmitReason))
            {
                Log.Warn("Weapon omitted for NPC 0x{0:X8}: {1}",
                    plan.Appearance.NpcFormId,
                    weaponPlan.AttachmentOmitReason);
            }

            return;
        }

        var weaponRaw = NpcMeshHelpers.LoadNifRawFromBsa(weaponVisual.MeshPath, meshArchives);
        if (weaponRaw == null)
        {
            Log.Warn("Weapon NIF failed to load: {0}", weaponVisual.MeshPath);
            return;
        }

        if (weaponPlan.UseSkinnedMainWeaponWhenPossible)
        {
            var skinnedModel = NifGeometryExtractor.Extract(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                textureResolver,
                externalBoneTransforms: plan.Skeleton?.BodySkinningBones ?? plan.Skeleton?.WeaponAttachmentBones,
                useDualQuaternionSkinning: true);
            if (skinnedModel != null && skinnedModel.HasGeometry && skinnedModel.WasSkinned)
            {
                Log.Debug("Weapon '{0}' ({1}): skinned to body pose bones, {2} submeshes",
                    weaponVisual.MeshPath,
                    weaponVisual.WeaponType,
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

        var weaponModel = weaponPlan.RenderOnlyExplicitHolsterAttachmentGroups
            ? null
            : NifGeometryExtractor.Extract(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                textureResolver,
                excludeBlockIndices: weaponPlan.MainWeaponExcludedShapes);
        if ((weaponModel == null || !weaponModel.HasGeometry) &&
            weaponPlan.HolsterAttachmentGroups.Count == 0)
        {
            Log.Warn("Weapon NIF has no geometry: {0}", weaponVisual.MeshPath);
            return;
        }

        string? modelAnchorCompensationLabel = null;
        if (weaponModel != null &&
            weaponModel.HasGeometry &&
            NpcSkinningResolver.TryResolveModelAttachmentCompensation(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                "Weapon",
                out var modelAnchorCompensation,
                out var compensationKind) &&
            NpcSkinningResolver.ShouldApplyWeaponModelAttachmentCompensation(
                weaponVisual.AttachmentMode,
                compensationKind))
        {
            NpcRenderHelpers.TransformModel(weaponModel, modelAnchorCompensation);
            modelAnchorCompensationLabel = compensationKind ==
                                           NpcSkinningResolver.ModelAttachmentCompensationKind.ExplicitAttachmentNode
                ? " + model Weapon anchor compensation"
                : " + model root fallback compensation";
        }

        if (weaponModel != null && weaponModel.HasGeometry)
        {
            NpcRenderHelpers.TransformModel(weaponModel, weaponPlan.MainAttachmentTransform.Value);

            Log.Debug("Weapon '{0}' ({1}): node '{2}' at ({3:F1},{4:F1},{5:F1}){6}, {7} submeshes",
                weaponVisual.MeshPath,
                weaponVisual.WeaponType,
                weaponPlan.AttachmentNodeName ?? "?",
                weaponPlan.MainAttachmentTransform.Value.Translation.X,
                weaponPlan.MainAttachmentTransform.Value.Translation.Y,
                weaponPlan.MainAttachmentTransform.Value.Translation.Z,
                (weaponPlan.AttachmentSourceLabel ?? string.Empty) + (modelAnchorCompensationLabel ?? string.Empty),
                weaponModel.Submeshes.Count);

            foreach (var sub in weaponModel.Submeshes)
            {
                sub.RenderOrder = 6;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }

        if (weaponVisual.AttachmentMode != WeaponAttachmentMode.HolsterPose ||
            weaponPlan.HolsterAttachmentGroups.Count == 0 ||
            weaponPlan.HolsterPose == null)
        {
            return;
        }

        var allShapeIndices = NpcSkinningResolver.FindShapeBlockIndices(weaponRaw.Value.Data, weaponRaw.Value.Info);
        foreach (var group in weaponPlan.HolsterAttachmentGroups)
        {
            var groupAttachmentTransform =
                NpcWeaponAttachmentResolver.ResolveWeaponHolsterAttachmentTransform(
                    weaponPlan.HolsterPose,
                    group.BoneName);
            if (!groupAttachmentTransform.HasValue)
            {
                Log.Warn(
                    "Weapon attachment group omitted for NPC 0x{0:X8}: no holster attachment node '{1}' for weapon '{2}'",
                    plan.Appearance.NpcFormId,
                    group.BoneName,
                    weaponVisual.MeshPath);
                continue;
            }

            var groupExcludedShapes = new HashSet<int>(allShapeIndices);
            groupExcludedShapes.ExceptWith(group.ShapeIndices);

            var groupModel = NifGeometryExtractor.Extract(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                textureResolver,
                excludeBlockIndices: groupExcludedShapes,
                animOverrides: weaponPlan.HolsterModelPoseOverrides);
            if (groupModel == null || !groupModel.HasGeometry)
            {
                Log.Warn(
                    "Weapon attachment group '{0}' had no geometry for weapon '{1}'",
                    group.SourceNodeName,
                    weaponVisual.MeshPath);
                continue;
            }

            NpcRenderHelpers.TransformModel(groupModel, groupAttachmentTransform.Value);
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

    internal static void LoadWeaponAddonMeshes(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? idleBoneTransforms,
        NifRenderableModel bodyModel)
    {
        if (npc.WeaponVisual?.AddonMeshes is not { Count: > 0 } addonMeshes)
        {
            return;
        }

        LoadWeaponAddonMeshes(addonMeshes, meshArchives, textureResolver, idleBoneTransforms, bodyModel);
    }

    internal static void LoadWeaponAddonMeshes(
        IReadOnlyList<WeaponAddonVisual> addonMeshes,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? idleBoneTransforms,
        NifRenderableModel bodyModel)
    {
        if (idleBoneTransforms == null || addonMeshes.Count == 0)
        {
            return;
        }

        foreach (var addon in addonMeshes)
        {
            var addonRaw = NpcMeshHelpers.LoadNifRawFromBsa(addon.MeshPath, meshArchives);
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
}
