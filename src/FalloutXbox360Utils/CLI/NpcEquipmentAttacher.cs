using System.Numerics;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
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
        NpcWeaponLoader.LoadWeapon(npc, meshArchives, textureResolver,
            addonSkinningBoneTransforms, attachmentBoneTransforms, bodyModel, s, skeletonNifPath);
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
        NpcWeaponLoader.LoadWeaponFromPlan(plan, meshArchives, textureResolver, bodyModel);
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
