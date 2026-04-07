using System.Numerics;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Builds composited NPC full-body models: skeleton + body parts + equipment + head.
///     Delegates to <see cref="NpcSkeletonLoader" />, <see cref="NpcEquipmentAttacher" />,
///     and <see cref="NpcSkinningResolver" /> for specific subsystems.
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
        if (s.Skeleton && npc.SkeletonNifPath != null)
        {
            var skelModel = NpcSkeletonLoader.BuildSkeletonVisualization(npc.SkeletonNifPath, meshArchives, s.BindPose);
            if (skelModel != null)
            {
                return skelModel;
            }
        }

        var skeletonPlans = new Dictionary<string, NpcCompositionCaches.CachedNpcSkeletonPlan?>(
            StringComparer.OrdinalIgnoreCase);
        if (npc.SkeletonNifPath != null && skeletonBoneCache != null)
        {
            skeletonPlans[BuildSkeletonCacheKey(npc.SkeletonNifPath, s)] =
                new NpcCompositionCaches.CachedNpcSkeletonPlan(
                    npc.SkeletonNifPath,
                    skeletonBoneCache,
                    poseDeltaCache ?? new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase),
                    null);
        }

        var compositionCaches = new NpcCompositionCaches(egmCache, egtCache, skeletonPlans);
        var renderModelCache = new NpcRenderModelCache(headMeshCache);
        var plan = NpcCompositionPlanner.CreatePlan(
            npc,
            meshArchives,
            textureResolver,
            compositionCaches,
            NpcCompositionOptions.From(s));

        skeletonBoneCache = plan.Skeleton?.BodySkinningBones;
        poseDeltaCache = plan.Skeleton?.PoseDeltas;
        return BuildFromPlan(plan, meshArchives, textureResolver, compositionCaches, renderModelCache);
    }

    internal static NifRenderableModel? BuildFromPlan(
        NpcCompositionPlan plan,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcCompositionCaches compositionCaches,
        NpcRenderModelCache renderModelCache)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(compositionCaches);
        ArgumentNullException.ThrowIfNull(renderModelCache);

        var npc = plan.Appearance;
        Log.Debug("NPC 0x{0:X8} ({1}): coveredSlots=0x{2:X}, equipment={3}, bodyTex={4}, upperBody={5}",
            npc.NpcFormId, npc.EditorId ?? "?", plan.CoveredSlots,
            plan.BodyEquipment.Count > 0 ? string.Join(", ", plan.BodyEquipment.Select(e => e.MeshPath)) : "(none)",
            plan.EffectiveBodyTexturePath ?? "(null)", npc.UpperBodyNifPath ?? "(null)");

        var bodyModel = new NifRenderableModel();
        var bodySkinningBones = plan.Skeleton?.BodySkinningBones;

        foreach (var bodyPart in plan.BodyParts)
        {
            LoadAndMergeBodyPart(
                bodyPart.MeshPath,
                bodyPart.TextureOverride,
                bodyPart.RenderOrder,
                meshArchives,
                textureResolver,
                bodySkinningBones,
                bodyModel);
        }

        NpcEquipmentAttacher.LoadEquipmentFromPlan(
            plan,
            meshArchives,
            textureResolver,
            bodyModel);
        NpcEquipmentAttacher.LoadWeaponFromPlan(
            plan,
            meshArchives,
            textureResolver,
            bodyModel);

        // Stitch boundary vertices between body parts and equipment to close seams
        // at mesh boundaries (e.g., outfit sleeve ↔ hand mesh at the wrist).
        NpcBoundaryVertexStitcher.StitchBoundaryVertices(bodyModel.Submeshes);

        var headModel = NpcHeadBuilder.BuildFromPlan(
            plan,
            meshArchives,
            textureResolver,
            compositionCaches,
            renderModelCache);
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
            sub.SourceNifPath = nifPath;
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

    private static string BuildSkeletonCacheKey(string skeletonNifPath, NpcRenderSettings settings)
    {
        return $"{skeletonNifPath}|bind:{settings.BindPose}|anim:{settings.AnimOverride ?? string.Empty}";
    }
}
