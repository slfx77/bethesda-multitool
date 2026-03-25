using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Body and equipment assembly methods for NPC export scene construction.
/// </summary>
internal static class NpcExportBodyAssembler
{
    private static readonly Logger Log = Logger.Instance;

    internal static void AddBodyEquipment(
        NpcExportScene scene,
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, int> nodeIndicesByBoneName,
        Dictionary<string, Matrix4x4> boneTransforms,
        string? effectiveBodyTex,
        string? effectiveHandTex,
        NpcExportSettings settings)
    {
        if (settings.NoEquip || npc.EquippedItems == null)
        {
            return;
        }

        foreach (var item in npc.EquippedItems)
        {
            if (NpcTextureHelpers.IsHeadEquipment(item.BipedFlags))
            {
                continue;
            }

            if (item.AttachmentMode != EquipmentAttachmentMode.None)
            {
                var raw = NpcMeshHelpers.LoadNifRawFromBsa(item.MeshPath, meshArchives);
                if (raw != null && NpcEquipmentAttacher.IsRigidEquipmentModel(raw.Value.Data, raw.Value.Info) &&
                    NpcWeaponAttachmentResolver.TryResolveEquipmentAttachmentTransform(
                        item, boneTransforms, out _, out var attachmentTransform, out _))
                {
                    var extracted = NpcExportSceneBuilder.LoadExtractedNif(item.MeshPath, meshArchives);
                    if (extracted != null && extracted.MeshParts.Count > 0)
                    {
                        foreach (var part in extracted.MeshParts)
                        {
                            if (NifBlockParsers.IsPipBoyScreenShape(part.Name))
                            {
                                continue;
                            }

                            var submesh = NpcExportSceneBuilder.CloneSubmesh(part.Submesh);
                            var composedTransform = part.ShapeWorldTransform * attachmentTransform;
                            NpcRenderHelpers.TransformSubmesh(submesh, composedTransform);
                            ApplyEquipmentTextureOverride(submesh, effectiveBodyTex, effectiveHandTex);
                            NpcExportSceneBuilder.AddRigidSubmesh(scene, item.MeshPath, submesh);
                        }

                        continue;
                    }
                }
            }

            NpcExportSceneBuilder.AddSkinnedNif(
                scene,
                item.MeshPath,
                meshArchives,
                nodeIndicesByBoneName,
                submesh => ApplyEquipmentTextureOverride(submesh, effectiveBodyTex, effectiveHandTex));
        }
    }

    private static void ApplyEquipmentTextureOverride(
        RenderableSubmesh submesh,
        string? effectiveBodyTex,
        string? effectiveHandTex)
    {
        if (effectiveBodyTex != null && NpcTextureHelpers.IsEquipmentSkinSubmesh(submesh.DiffuseTexturePath))
        {
            submesh.DiffuseTexturePath =
                submesh.DiffuseTexturePath?.Contains("hand", StringComparison.OrdinalIgnoreCase) == true
                    ? effectiveHandTex ?? effectiveBodyTex
                    : effectiveBodyTex;
        }
    }

    internal static void AddWeapon(
        NpcExportScene scene,
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcExportSceneBuilder.SkeletonContext skeletonContext,
        NpcExportSettings settings)
    {
        if (!settings.IncludeWeapon ||
            settings.NoEquip ||
            npc.WeaponVisual?.IsVisible != true ||
            npc.WeaponVisual.MeshPath == null)
        {
            return;
        }

        if (!NpcWeaponAttachmentResolver.TryResolveWeaponAttachmentNode(npc.WeaponVisual, out var attachmentNodeName, out _))
        {
            return;
        }

        Matrix4x4? attachmentTransform = null;
        NpcWeaponAttachmentResolver.WeaponHolsterPose? holsterPose = null;
        if (settings.BindPose || string.IsNullOrWhiteSpace(npc.WeaponVisual.HolsterProfileKey))
        {
            attachmentTransform = skeletonContext.BoneTransforms.TryGetValue(attachmentNodeName, out var bindAttachment)
                ? bindAttachment
                : null;
        }
        else
        {
            holsterPose = LoadHolsterPose(npc, meshArchives);
            attachmentTransform = holsterPose != null
                ? NpcWeaponAttachmentResolver.ResolveWeaponHolsterAttachmentTransform(holsterPose, attachmentNodeName)
                : null;
        }

        if (!attachmentTransform.HasValue)
        {
            return;
        }

        var weaponRaw = NpcMeshHelpers.LoadNifRawFromBsa(npc.WeaponVisual.MeshPath, meshArchives);
        if (weaponRaw == null)
        {
            return;
        }

        HashSet<int>? mainWeaponExcludedShapes = null;
        List<NifSceneGraphWalker.ParentBoneShapeGroup> holsterAttachmentGroups = [];
        if (npc.WeaponVisual.AttachmentMode == WeaponAttachmentMode.HolsterPose)
        {
            var visAnalysis = NifSceneGraphWalker.AnalyzeVisControllers(weaponRaw.Value.Data, weaponRaw.Value.Info);
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
            npc.WeaponVisual.AttachmentMode == WeaponAttachmentMode.HolsterPose &&
            holsterAttachmentGroups.Count > 0;
        if (renderOnlyExplicitHolsterAttachmentGroups)
        {
            holsterModelPoseOverrides = NifNodeControllerPoseReader.Parse(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                true);
            Log.Debug(
                "Export weapon '{0}': holster mode - suppressing remaining root-attached geometry; rendering explicit attachment groups only",
                npc.WeaponVisual.MeshPath);
        }

        var weaponModel = renderOnlyExplicitHolsterAttachmentGroups
            ? null
            : NifGeometryExtractor.Extract(
                weaponRaw.Value.Data,
                weaponRaw.Value.Info,
                textureResolver,
                excludeBlockIndices: mainWeaponExcludedShapes);
        if (weaponModel != null && weaponModel.HasGeometry)
        {
            if (NpcSkinningResolver.TryResolveModelAttachmentCompensation(
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
            }

            NpcRenderHelpers.TransformModel(weaponModel, attachmentTransform.Value);
            NpcExportSceneBuilder.AddRigidModel(scene, npc.WeaponVisual.MeshPath, weaponModel);
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
            var groupAttachmentTransform = NpcWeaponAttachmentResolver.ResolveWeaponHolsterAttachmentTransform(
                holsterPose,
                group.BoneName);
            if (!groupAttachmentTransform.HasValue)
            {
                Log.Warn(
                    "Export weapon attachment group omitted for NPC 0x{0:X8}: no holster attachment node '{1}' for weapon '{2}'",
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
                continue;
            }

            NpcRenderHelpers.TransformModel(groupModel, groupAttachmentTransform.Value);
            NpcExportSceneBuilder.AddRigidModel(scene, $"{npc.WeaponVisual.MeshPath}:{group.SourceNodeName}", groupModel);
        }
    }

    internal static void ApplyBodyEgtMorphs(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache,
        ref string? effectiveBodyTex,
        ref string? effectiveHandTex)
    {
        if (npc.BodyEgtPath != null && npc.BodyTexturePath != null && npc.FaceGenTextureCoeffs != null)
        {
            effectiveBodyTex = NpcMeshHelpers.ApplyBodyEgtMorph(
                npc.BodyEgtPath,
                npc.BodyTexturePath,
                npc.FaceGenTextureCoeffs,
                npc.NpcFormId,
                "upperbody",
                npc.RenderVariantLabel,
                meshArchives,
                textureResolver,
                egtCache) ?? effectiveBodyTex;
        }

        if (npc.LeftHandEgtPath != null && npc.HandTexturePath != null && npc.FaceGenTextureCoeffs != null)
        {
            effectiveHandTex = NpcMeshHelpers.ApplyBodyEgtMorph(
                npc.LeftHandEgtPath,
                npc.HandTexturePath,
                npc.FaceGenTextureCoeffs,
                npc.NpcFormId,
                "lefthand",
                npc.RenderVariantLabel,
                meshArchives,
                textureResolver,
                egtCache) ?? effectiveHandTex;
        }

        if (npc.RightHandEgtPath != null && npc.HandTexturePath != null && npc.FaceGenTextureCoeffs != null)
        {
            _ = NpcMeshHelpers.ApplyBodyEgtMorph(
                npc.RightHandEgtPath,
                npc.HandTexturePath,
                npc.FaceGenTextureCoeffs,
                npc.NpcFormId,
                "righthand",
                npc.RenderVariantLabel,
                meshArchives,
                textureResolver,
                egtCache);
        }
    }

    internal static Dictionary<string, NifAnimationParser.AnimPoseOverride>? LoadAnimationOverrides(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        (byte[] Data, NifInfo Info) skeletonRaw,
        string? animOverride)
    {
        var skeletonDirectory = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(animOverride))
        {
            var customRaw = NpcMeshHelpers.LoadNifRawFromBsa(skeletonDirectory + animOverride, meshArchives, true);
            if (customRaw != null)
            {
                return NifAnimationParser.ParseIdlePoseOverrides(customRaw.Value.Data, customRaw.Value.Info);
            }
        }

        var idleRaw = NpcMeshHelpers.LoadNifRawFromBsa(
            skeletonDirectory + "locomotion\\mtidle.kf",
            meshArchives,
            true);
        if (idleRaw == null &&
            skeletonDirectory.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            idleRaw = NpcMeshHelpers.LoadNifRawFromBsa(
                skeletonDirectory.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase) +
                "locomotion\\mtidle.kf",
                meshArchives,
                true);
        }

        return idleRaw != null
            ? NifAnimationParser.ParseIdlePoseOverrides(idleRaw.Value.Data, idleRaw.Value.Info)
            : NifAnimationParser.ParseIdlePoseOverrides(skeletonRaw.Data, skeletonRaw.Info);
    }

    private static NpcWeaponAttachmentResolver.WeaponHolsterPose? LoadHolsterPose(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives)
    {
        if (npc.SkeletonNifPath == null || npc.WeaponVisual?.HolsterProfileKey == null)
        {
            return null;
        }

        var kfRelPath = HasPowerArmorTorso(npc.EquippedItems)
            ? $"PA{npc.WeaponVisual.HolsterProfileKey}Holster.kf"
            : $"{npc.WeaponVisual.HolsterProfileKey}Holster.kf";
        var skeletonDirectory = npc.SkeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        var holsterRaw = NpcMeshHelpers.LoadNifRawFromBsa(skeletonDirectory + kfRelPath, meshArchives, true);
        if (holsterRaw == null &&
            skeletonDirectory.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            holsterRaw = NpcMeshHelpers.LoadNifRawFromBsa(
                skeletonDirectory.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase) + kfRelPath,
                meshArchives,
                true);
        }

        if (holsterRaw == null)
        {
            return null;
        }

        var holsterOverrides = NifAnimationParser.ParseIdlePoseOverrides(holsterRaw.Value.Data, holsterRaw.Value.Info);
        if (holsterOverrides == null || holsterOverrides.Count == 0)
        {
            return null;
        }

        var skeletonRaw = NpcMeshHelpers.LoadNifRawFromBsa(npc.SkeletonNifPath, meshArchives);
        if (skeletonRaw == null)
        {
            return null;
        }

        var baseIdle = LoadAnimationOverrides(npc.SkeletonNifPath, meshArchives, skeletonRaw.Value, null);
        var merged = new Dictionary<string, NifAnimationParser.AnimPoseOverride>(StringComparer.OrdinalIgnoreCase);
        if (baseIdle != null)
        {
            foreach (var (boneName, pose) in baseIdle)
            {
                merged[boneName] = pose;
            }
        }

        foreach (var (boneName, pose) in holsterOverrides)
        {
            merged[boneName] = pose;
        }

        var worldTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skeletonRaw.Value.Data,
            skeletonRaw.Value.Info,
            merged);
        var parentOverrideBone = NpcSkeletonLoader.TryParseSequenceParentBoneName(holsterRaw.Value.Info);
        return new NpcWeaponAttachmentResolver.WeaponHolsterPose(
            worldTransforms,
            holsterOverrides,
            skeletonRaw.Value.Data,
            skeletonRaw.Value.Info,
            parentOverrideBone);
    }

    private static bool HasPowerArmorTorso(IEnumerable<EquippedItem>? equippedItems)
    {
        return equippedItems != null &&
               equippedItems.Any(item => (item.BipedFlags & 0x04) != 0 && item.IsPowerArmor);
    }
}
