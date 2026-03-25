using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Builds composited NPC full-body models: skeleton + body parts + equipment + head.
///     Delegates to <see cref="NpcSkeletonLoader"/>, <see cref="NpcEquipmentAttacher"/>,
///     and <see cref="NpcSkinningResolver"/> for specific subsystems.
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
        // Load skeleton bone transforms (cached across NPCs — same skeleton for all humans)
        if (skeletonBoneCache == null && npc.SkeletonNifPath != null)
        {
            NpcSkeletonLoader.LoadSkeletonBones(npc.SkeletonNifPath, meshArchives, s.BindPose,
                out skeletonBoneCache, out poseDeltaCache, s.AnimOverride);
        }

        // Skeleton-only mode: build geometric visualization from bone positions/hierarchy.
        if (s.Skeleton && npc.SkeletonNifPath != null)
        {
            var skelModel = NpcSkeletonLoader.BuildSkeletonVisualization(npc.SkeletonNifPath, meshArchives, s.BindPose);
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
        var bodyAndAddonBones = skeletonBoneCache;
        var weaponAttachmentBones = skeletonBoneCache;
        if (bodyAndAddonBones != null &&
            NpcSkeletonLoader.ShouldUseHandToHandIdleArmPose(npc, s) &&
            !string.IsNullOrWhiteSpace(npc.WeaponVisual?.EquippedPoseKfPath) &&
            npc.SkeletonNifPath != null)
        {
            weaponAttachmentBones = NpcSkeletonLoader.BuildHandToHandEquippedArmBones(
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
        NpcEquipmentAttacher.LoadEquipment(npc, meshArchives, textureResolver, bodyAndAddonBones,
            effectiveBodyTex, effectiveHandTex, bodyModel, s);

        // Load weapon mesh
        NpcEquipmentAttacher.LoadWeapon(npc, meshArchives, textureResolver, bodyAndAddonBones, weaponAttachmentBones,
            bodyModel, s, npc.SkeletonNifPath);

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
    ///     Loads a body part NIF with skeleton-driven skinning and merges into target model.
    /// </summary>
    private static void LoadAndMergeBodyPart(
        string nifPath, string? textureOverride, int renderOrder,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? idleBoneTransforms,
        NifRenderableModel targetModel)
    {
        var raw = NpcMeshHelpers.LoadNifRawFromBsa(nifPath, meshArchives);
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

        Log.Debug("Body part '{0}': {1} submeshes, bounds ({2:F2},{3:F2},{4:F2})\u2192({5:F2},{6:F2},{7:F2})",
            nifPath, partModel.Submeshes.Count,
            partModel.MinX, partModel.MinY, partModel.MinZ,
            partModel.MaxX, partModel.MaxY, partModel.MaxZ);

        foreach (var sub in partModel.Submeshes)
        {
            if (textureOverride != null &&
                NpcTextureHelpers.ShouldApplyBodyTextureOverride(sub.DiffuseTexturePath, textureOverride))
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
            var key = NpcMeshHelpers.ApplyBodyEgtMorph(npc.BodyEgtPath, npc.BodyTexturePath,
                npc.FaceGenTextureCoeffs!, npc.NpcFormId, "upperbody", npc.RenderVariantLabel,
                meshArchives, textureResolver, egtCache);
            if (key != null) effectiveBodyTex = key;
        }

        if (npc.LeftHandEgtPath != null && npc.HandTexturePath != null)
        {
            var key = NpcMeshHelpers.ApplyBodyEgtMorph(npc.LeftHandEgtPath, npc.HandTexturePath,
                npc.FaceGenTextureCoeffs!, npc.NpcFormId, "lefthand", npc.RenderVariantLabel,
                meshArchives, textureResolver, egtCache);
            if (key != null) effectiveHandTex = key;
        }

        if (npc.RightHandEgtPath != null && npc.HandTexturePath != null)
        {
            NpcMeshHelpers.ApplyBodyEgtMorph(npc.RightHandEgtPath, npc.HandTexturePath,
                npc.FaceGenTextureCoeffs!, npc.NpcFormId, "righthand", npc.RenderVariantLabel,
                meshArchives, textureResolver, egtCache);
        }
    }
}
