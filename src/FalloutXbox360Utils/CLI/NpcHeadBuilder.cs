using System.Numerics;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Builds composited NPC head models: base head mesh + EGM/EGT morphs + hair + eyes + head parts.
///     Used by both head-only and full-body render paths.
/// </summary>
internal static class NpcHeadBuilder
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Builds the composited head model (head + hair + eyes + head parts) without rendering.
    /// </summary>
    internal static NifRenderableModel? Build(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        NpcRenderSettings s,
        string? hairFilterOverride = null,
        Dictionary<string, Matrix4x4>? skeletonBones = null,
        Dictionary<string, Matrix4x4>? idlePoseBones = null,
        Matrix4x4? headEquipmentTransformOverride = null)
    {
        var compositionCaches = new NpcCompositionCaches(
            egmCache,
            egtCache,
            new Dictionary<string, NpcCompositionCaches.CachedNpcSkeletonPlan?>(
                StringComparer.OrdinalIgnoreCase));
        var renderModelCache = new NpcRenderModelCache(headMeshCache);
        var plan = NpcCompositionPlanner.CreatePlan(
            npc,
            meshArchives,
            textureResolver,
            compositionCaches,
            NpcCompositionOptions.From(s));
        plan = ApplyHeadRenderOverrides(
            plan,
            hairFilterOverride,
            skeletonBones,
            idlePoseBones,
            headEquipmentTransformOverride);
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
        var headPlan = plan.Head;
        NifRenderableModel? model = null;
        var usedBaseRaceMesh = false;

        if (headPlan.BaseHeadNifPath != null)
        {
            if (!plan.Options.HeadOnly)
            {
                model = LoadHeadWithPreSkinMorphs(
                    headPlan.BaseHeadNifPath,
                    meshArchives,
                    textureResolver,
                    headPlan.AttachmentBoneTransforms,
                    headPlan.HeadPreSkinMorphDeltas);
            }
            else if (headPlan.HeadPreSkinMorphDeltas != null)
            {
                model = LoadHeadWithPreSkinMorphs(
                    headPlan.BaseHeadNifPath,
                    meshArchives,
                    textureResolver,
                    headPlan.AttachmentBoneTransforms,
                    headPlan.HeadPreSkinMorphDeltas);
            }
            else
            {
                if (!renderModelCache.HeadMeshes.TryGetValue(headPlan.BaseHeadNifPath, out var cached))
                {
                    cached = NpcMeshHelpers.LoadNifFromBsa(
                        headPlan.BaseHeadNifPath,
                        meshArchives,
                        textureResolver,
                        headPlan.AttachmentBoneTransforms,
                        useDualQuaternionSkinning: true);
                    renderModelCache.HeadMeshes[headPlan.BaseHeadNifPath] = cached;
                }

                if (cached != null)
                {
                    model = NpcMeshHelpers.DeepCloneModel(cached);
                }
            }

            if (model != null)
            {
                usedBaseRaceMesh = true;
            }
        }

        if (model == null && headPlan.FaceGenNifPath != null)
        {
            model = NpcMeshHelpers.LoadNifFromBsa(headPlan.FaceGenNifPath, meshArchives, textureResolver);
        }

        if (model == null || !model.HasGeometry)
        {
            return null;
        }

        var headMeshEndIndex = model.Submeshes.Count;
        if (headPlan.EffectiveHeadTexturePath != null)
        {
            for (var index = 0; index < headMeshEndIndex; index++)
            {
                model.Submeshes[index].DiffuseTexturePath = headPlan.EffectiveHeadTexturePath;
                if (!headPlan.EffectiveHeadTextureUsesEgtMorph)
                {
                    continue;
                }

                model.Submeshes[index].IsFaceGen = true;
                model.Submeshes[index].SubsurfaceColor = (24f / 255f, 8f / 255f, 8f / 255f);
            }
        }

        Log.Debug("Head bounds: ({0:F2}, {1:F2}, {2:F2}) ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ ({3:F2}, {4:F2}, {5:F2})",
            model.MinX, model.MinY, model.MinZ, model.MaxX, model.MaxY, model.MaxZ);

        NpcHeadPartAttacher.AttachRaceFaceParts(
            npc,
            model,
            headPlan.AttachmentBoneTransforms,
            usedBaseRaceMesh,
            meshArchives,
            textureResolver,
            compositionCaches.EgmFiles,
            headPlan.BonelessAttachmentTransform);

        if (headPlan.HairNifPath != null)
        {
            NpcHeadPartAttacher.AttachHairMesh(
                npc,
                model,
                headPlan.AttachmentBoneTransforms,
                usedBaseRaceMesh,
                headPlan.HairFilter,
                meshArchives,
                textureResolver,
                compositionCaches.EgmFiles,
                headPlan.BonelessAttachmentTransform);
        }

        if (headPlan.HeadPartNifPaths.Count > 0)
        {
            NpcHeadPartAttacher.AttachHeadParts(
                npc,
                model,
                headPlan.AttachmentBoneTransforms,
                usedBaseRaceMesh,
                meshArchives,
                textureResolver,
                compositionCaches.EgmFiles,
                headPlan.BonelessAttachmentTransform);
        }

        NpcHeadPartAttacher.AttachEyeMeshes(
            npc,
            model,
            headPlan.AttachmentBoneTransforms,
            usedBaseRaceMesh,
            meshArchives,
            textureResolver,
            compositionCaches.EgmFiles,
            headPlan.BonelessAttachmentTransform);

        if (headPlan.HeadEquipment.Count > 0)
        {
            NpcHeadPartAttacher.AttachHeadEquipment(
                npc,
                model,
                headPlan.AttachmentBoneTransforms,
                usedBaseRaceMesh,
                meshArchives,
                textureResolver,
                compositionCaches.EgmFiles,
                headPlan.BonelessAttachmentTransform);
        }

        return model;
    }

    private static NpcCompositionPlan ApplyHeadRenderOverrides(
        NpcCompositionPlan plan,
        string? hairFilterOverride,
        Dictionary<string, Matrix4x4>? skeletonBones,
        Dictionary<string, Matrix4x4>? idlePoseBones,
        Matrix4x4? headEquipmentTransformOverride)
    {
        if (hairFilterOverride == null &&
            skeletonBones == null &&
            idlePoseBones == null &&
            !headEquipmentTransformOverride.HasValue)
        {
            return plan;
        }

        var headPlan = new NpcHeadCompositionPlan
        {
            BaseHeadNifPath = plan.Head.BaseHeadNifPath,
            FaceGenNifPath = plan.Head.FaceGenNifPath,
            HeadPreSkinMorphDeltas = plan.Head.HeadPreSkinMorphDeltas,
            EffectiveHeadTexturePath = plan.Head.EffectiveHeadTexturePath,
            EffectiveHeadTextureUsesEgtMorph = plan.Head.EffectiveHeadTextureUsesEgtMorph,
            HairFilter = hairFilterOverride ?? plan.Head.HairFilter,
            AttachmentBoneTransforms = skeletonBones ?? idlePoseBones ?? plan.Head.AttachmentBoneTransforms,
            BonelessAttachmentTransform = headEquipmentTransformOverride ?? plan.Head.BonelessAttachmentTransform,
            RaceFacePartPaths = plan.Head.RaceFacePartPaths,
            HairNifPath = plan.Head.HairNifPath,
            HeadPartNifPaths = plan.Head.HeadPartNifPaths,
            EyeNifPaths = plan.Head.EyeNifPaths,
            HeadEquipment = plan.Head.HeadEquipment
        };

        return new NpcCompositionPlan
        {
            Appearance = plan.Appearance,
            Options = plan.Options,
            Skeleton = plan.Skeleton,
            Head = headPlan,
            BodyParts = plan.BodyParts,
            BodyEquipment = plan.BodyEquipment,
            CoveredSlots = plan.CoveredSlots,
            EffectiveBodyTexturePath = plan.EffectiveBodyTexturePath,
            EffectiveHandTexturePath = plan.EffectiveHandTexturePath,
            Weapon = plan.Weapon
        };
    }

    private static NifRenderableModel? LoadHeadWithPreSkinMorphs(
        string headNifPath,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? boneTransforms,
        float[]? preSkinMorphDeltas)
    {
        var result = NpcMeshHelpers.LoadNifRawFromBsa(headNifPath, meshArchives);
        if (result == null)
            return null;

        var model = NifGeometryExtractor.Extract(result.Value.Data, result.Value.Info, textureResolver,
            externalBoneTransforms: boneTransforms,
            useDualQuaternionSkinning: true,
            preSkinMorphDeltas: preSkinMorphDeltas);

        return model;
    }

    internal static bool IsMouthPart(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name != null &&
               (name.Contains("mouth", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("teeth", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("tongue", StringComparison.OrdinalIgnoreCase));
    }

    internal static float EstimateFaceGenMorphMagnitude(float[] coefficients)
    {
        if (coefficients.Length == 0)
        {
            return 0f;
        }

        var sum = 0f;
        foreach (var c in coefficients)
        {
            sum += MathF.Abs(c);
        }

        return sum / coefficients.Length;
    }
}
