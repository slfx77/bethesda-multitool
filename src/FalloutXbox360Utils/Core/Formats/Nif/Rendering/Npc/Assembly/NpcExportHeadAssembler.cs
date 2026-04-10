using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Head, hair, eyes, and face part assembly methods for NPC export scene construction.
/// </summary>
internal static class NpcExportHeadAssembler
{
    internal static void AddHeadContent(
        NpcExportScene scene,
        NpcCompositionPlan plan,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcCompositionCaches compositionCaches,
        Dictionary<string, int>? nodeIndicesByBoneName)
    {
        var npc = plan.Appearance;
        var headPlan = plan.Head;
        var usedBaseRaceMesh = false;

        if (headPlan.BaseHeadNifPath != null)
        {
            var extracted = NpcExportSceneBuilder.LoadExtractedNif(
                headPlan.BaseHeadNifPath,
                meshArchives,
                preSkinMorphDeltas: headPlan.HeadPreSkinMorphDeltas);
            if (extracted != null)
            {
                foreach (var part in extracted.MeshParts)
                {
                    if (headPlan.EffectiveHeadTexturePath != null)
                    {
                        part.Submesh.DiffuseTexturePath = headPlan.EffectiveHeadTexturePath;
                    }

                    if (part.Skin != null && nodeIndicesByBoneName != null)
                    {
                        NpcExportSceneBuilder.AddSkinnedPart(scene, part, nodeIndicesByBoneName);
                    }
                    else
                    {
                        NpcExportSceneBuilder.AddExtractedRigidPart(
                            scene,
                            part,
                            part.ShapeWorldTransform,
                            headPlan.BaseHeadNifPath);
                    }
                }

                if (headPlan.HeadPreSkinMorphDeltas != null)
                {
                    foreach (var part in extracted.MeshParts)
                    {
                        FaceGenMeshMorpher.RecalculateNormals(part.Submesh);
                    }
                }

                usedBaseRaceMesh = true;
            }
        }

        AddRaceFaceParts(
            scene,
            npc,
            meshArchives,
            textureResolver,
            compositionCaches.EgmFiles,
            usedBaseRaceMesh,
            headPlan.AttachmentBoneTransforms,
            headPlan.BonelessAttachmentTransform);
        if (plan.Options.IncludeHair)
        {
            AddHair(
                scene,
                npc,
                meshArchives,
                textureResolver,
                compositionCaches.EgmFiles,
                usedBaseRaceMesh,
                headPlan.HairFilter,
                headPlan.AttachmentBoneTransforms,
                headPlan.BonelessAttachmentTransform);
            AddHeadParts(
                scene,
                npc,
                meshArchives,
                textureResolver,
                compositionCaches.EgmFiles,
                usedBaseRaceMesh,
                headPlan.AttachmentBoneTransforms,
                headPlan.BonelessAttachmentTransform);
        }

        AddEyes(
            scene,
            npc,
            meshArchives,
            textureResolver,
            compositionCaches.EgmFiles,
            usedBaseRaceMesh,
            headPlan.AttachmentBoneTransforms,
            headPlan.BonelessAttachmentTransform);
        AddHeadEquipment(
            scene,
            npc,
            meshArchives,
            textureResolver,
            compositionCaches.EgmFiles,
            usedBaseRaceMesh,
            nodeIndicesByBoneName,
            headPlan.AttachmentBoneTransforms,
            headPlan.BonelessAttachmentTransform,
            headPlan.HeadEquipment.Count > 0);
    }

    internal static void AddHeadContent(
        NpcExportScene scene,
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        NpcExportSettings settings,
        Dictionary<string, int>? nodeIndicesByBoneName,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        Matrix4x4? bonelessAttachmentTransform)
    {
        var usedBaseRaceMesh = false;
        string? fullHeadTexturePath = null;
        var headMeshStartIndex = scene.MeshParts.Count;
        var headMeshEndIndex = headMeshStartIndex;

        if (npc.BaseHeadNifPath != null)
        {
            var headPreSkinDeltas = ComputeHeadPreSkinDeltas(npc, meshArchives, egmCache, settings);
            var extracted = NpcExportSceneBuilder.LoadExtractedNif(
                npc.BaseHeadNifPath,
                meshArchives,
                preSkinMorphDeltas: headPreSkinDeltas);
            if (extracted != null)
            {
                fullHeadTexturePath = npc.HeadDiffuseOverride != null
                    ? "textures\\" + npc.HeadDiffuseOverride
                    : null;

                foreach (var part in extracted.MeshParts)
                {
                    if (fullHeadTexturePath != null)
                    {
                        part.Submesh.DiffuseTexturePath = fullHeadTexturePath;
                    }

                    if (part.Skin != null && nodeIndicesByBoneName != null)
                    {
                        NpcExportSceneBuilder.AddSkinnedPart(scene, part, nodeIndicesByBoneName);
                    }
                    else
                    {
                        NpcExportSceneBuilder.AddExtractedRigidPart(scene, part, part.ShapeWorldTransform,
                            npc.BaseHeadNifPath);
                    }
                }

                if (headPreSkinDeltas != null)
                {
                    foreach (var part in extracted.MeshParts)
                    {
                        FaceGenMeshMorpher.RecalculateNormals(part.Submesh);
                    }
                }

                usedBaseRaceMesh = true;
                headMeshEndIndex = scene.MeshParts.Count;
            }
        }

        if (!settings.NoEgt &&
            usedBaseRaceMesh &&
            npc.FaceGenTextureCoeffs != null &&
            fullHeadTexturePath != null)
        {
            var morphedTextureKey = ApplyHeadEgtMorph(
                npc,
                fullHeadTexturePath,
                meshArchives,
                textureResolver,
                egtCache);
            if (morphedTextureKey != null)
            {
                for (var index = headMeshStartIndex; index < headMeshEndIndex; index++)
                {
                    scene.MeshParts[index].Submesh.DiffuseTexturePath = morphedTextureKey;
                }
            }
        }

        string? hairFilter = null;
        if (!settings.NoEquip && NpcTextureHelpers.HasHatEquipment(npc.EquippedItems))
        {
            hairFilter = "Hat";
        }

        AddRaceFaceParts(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
            attachmentBoneTransforms, bonelessAttachmentTransform);
        if (!settings.NoHair)
        {
            AddHair(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
                hairFilter, attachmentBoneTransforms, bonelessAttachmentTransform);
            AddHeadParts(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
                attachmentBoneTransforms, bonelessAttachmentTransform);
        }

        AddEyes(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
            attachmentBoneTransforms, bonelessAttachmentTransform);
        AddHeadEquipment(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
            nodeIndicesByBoneName, attachmentBoneTransforms, bonelessAttachmentTransform, !settings.NoEquip);
    }

    private static void AddHair(
        NpcExportScene scene,
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        bool usedBaseRaceMesh,
        string? hairFilter,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        Matrix4x4? bonelessAttachmentTransform)
    {
        if (npc.HairNifPath == null)
        {
            return;
        }

        var hairRaw = NpcMeshHelpers.LoadNifRawFromBsa(npc.HairNifPath, meshArchives);
        if (hairRaw == null)
        {
            return;
        }

        var hairModel = NifGeometryExtractor.Extract(
            hairRaw.Value.Data,
            hairRaw.Value.Info,
            textureResolver,
            filterShapeName: hairFilter ?? "NoHat");
        if (hairModel == null)
        {
            return;
        }

        if (usedBaseRaceMesh &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var hairBaseName = Path.GetFileNameWithoutExtension(npc.HairNifPath);
            var hairDir = Path.GetDirectoryName(npc.HairNifPath) ?? string.Empty;
            var egmSuffix = hairFilter == "Hat" ? "hat.egm" : "nohat.egm";
            var hairEgmPath = Path.Combine(hairDir, hairBaseName + egmSuffix);
            NpcMeshHelpers.LoadAndApplyEgm(
                hairEgmPath,
                hairModel,
                npc.FaceGenSymmetricCoeffs,
                npc.FaceGenAsymmetricCoeffs,
                meshArchives,
                egmCache);
        }

        if (attachmentBoneTransforms != null &&
            attachmentBoneTransforms.TryGetValue("Bip01 Head", out var headBone))
        {
            NpcRenderHelpers.ApplyHeadBoneCorrection(
                hairModel,
                hairRaw.Value.Data,
                hairRaw.Value.Info,
                headBone,
                bonelessAttachmentTransform,
                npc.HairNifPath);
        }

        // Some hair NIFs contain both actual hair strands and scalp/skin geometry.
        // Hair strands have NiStencilProperty (IsDoubleSided=true); scalp shapes are
        // single-sided and overlap the FaceGen head mesh, causing z-fighting dark bands.
        // Only filter when the NIF has a mix — if all shapes are single-sided, keep them all.
        if (hairModel.Submeshes.Any(s => s.IsDoubleSided))
        {
            hairModel.Submeshes.RemoveAll(s => !s.IsDoubleSided);
        }

        var tint = NpcTextureHelpers.UnpackHairColor(npc.HairColor);
        foreach (var submesh in hairModel.Submeshes)
        {
            submesh.TintColor = tint;
            if (npc.HairTexturePath != null)
            {
                submesh.DiffuseTexturePath = npc.HairTexturePath;
            }

            // Hair NIFs intentionally have unshared per-face vertices and authored
            // flat normals. The engine renders them as-is. Previously we called
            // RecalculateNormals + WeldSeamNormals to "smooth" hair, but this averages
            // normals across hair cards facing very different directions, producing
            // sideways-pointing normals at silhouette edges that read as dark patches
            // in glTF PBR viewers. Trust the authored NIF normals.
        }

        NpcExportSceneBuilder.AddRigidModel(scene, npc.HairNifPath, hairModel);
    }

    private static void AddRaceFaceParts(
        NpcExportScene scene,
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        bool usedBaseRaceMesh,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        Matrix4x4? bonelessAttachmentTransform)
    {
        foreach (var facePartPath in new[]
                 {
                     npc.MouthNifPath,
                     npc.LowerTeethNifPath,
                     npc.UpperTeethNifPath,
                     npc.TongueNifPath
                 })
        {
            if (facePartPath == null)
            {
                continue;
            }

            var partRaw = NpcMeshHelpers.LoadNifRawFromBsa(facePartPath, meshArchives);
            if (partRaw == null)
            {
                continue;
            }

            var partModel = NifGeometryExtractor.Extract(partRaw.Value.Data, partRaw.Value.Info, textureResolver);
            if (partModel == null)
            {
                continue;
            }

            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                NpcMeshHelpers.LoadAndApplyEgm(
                    Path.ChangeExtension(facePartPath, ".egm"),
                    partModel,
                    npc.FaceGenSymmetricCoeffs,
                    npc.FaceGenAsymmetricCoeffs,
                    meshArchives,
                    egmCache);
            }

            // Push mouth/teeth inward when FaceGen morphs are active to reduce clipping.
            if (usedBaseRaceMesh && npc.FaceGenSymmetricCoeffs != null &&
                NpcHeadBuilder.IsMouthPart(facePartPath))
            {
                var morphMagnitude =
                    NpcHeadBuilder.EstimateFaceGenMorphMagnitude(npc.FaceGenSymmetricCoeffs);
                if (morphMagnitude > 0.01f)
                {
                    var yOffset = -morphMagnitude * 0.15f;
                    foreach (var sub in partModel.Submeshes)
                    {
                        for (var i = 1; i < sub.Positions.Length; i += 3)
                        {
                            sub.Positions[i] += yOffset;
                        }
                    }
                }
            }

            if (attachmentBoneTransforms != null &&
                attachmentBoneTransforms.TryGetValue("Bip01 Head", out var headBone))
            {
                NpcRenderHelpers.ApplyHeadBoneCorrection(
                    partModel,
                    partRaw.Value.Data,
                    partRaw.Value.Info,
                    headBone,
                    bonelessAttachmentTransform,
                    facePartPath,
                    NpcRenderHelpers.HeadAttachmentRootPolicy.CompensateRotatedRoot);
            }

            NpcExportSceneBuilder.AddRigidModel(scene, facePartPath, partModel);
        }
    }

    private static void AddHeadParts(
        NpcExportScene scene,
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        bool usedBaseRaceMesh,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        Matrix4x4? bonelessAttachmentTransform)
    {
        if (npc.HeadPartNifPaths == null)
        {
            return;
        }

        foreach (var headPartPath in npc.HeadPartNifPaths)
        {
            var partRaw = NpcMeshHelpers.LoadNifRawFromBsa(headPartPath, meshArchives);
            if (partRaw == null)
            {
                continue;
            }

            var partModel = NifGeometryExtractor.Extract(partRaw.Value.Data, partRaw.Value.Info, textureResolver);
            if (partModel == null)
            {
                continue;
            }

            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                NpcMeshHelpers.LoadAndApplyEgm(
                    Path.ChangeExtension(headPartPath, ".egm"),
                    partModel,
                    npc.FaceGenSymmetricCoeffs,
                    npc.FaceGenAsymmetricCoeffs,
                    meshArchives,
                    egmCache);
            }

            if (attachmentBoneTransforms != null &&
                attachmentBoneTransforms.TryGetValue("Bip01 Head", out var headBone))
            {
                NpcRenderHelpers.ApplyHeadBoneCorrection(
                    partModel,
                    partRaw.Value.Data,
                    partRaw.Value.Info,
                    headBone,
                    bonelessAttachmentTransform,
                    headPartPath);
            }

            var tint = NpcTextureHelpers.UnpackHairColor(npc.HairColor);
            foreach (var submesh in partModel.Submeshes)
            {
                submesh.TintColor = tint;
                submesh.IsDoubleSided = true;
            }

            NpcExportSceneBuilder.AddRigidModel(scene, headPartPath, partModel);
        }
    }

    private static void AddEyes(
        NpcExportScene scene,
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        bool usedBaseRaceMesh,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        Matrix4x4? bonelessAttachmentTransform)
    {
        var eyeAttachmentTransform = bonelessAttachmentTransform;
        if (eyeAttachmentTransform == null &&
            attachmentBoneTransforms != null &&
            attachmentBoneTransforms.TryGetValue("Bip01 Head", out var headBone))
        {
            eyeAttachmentTransform = Matrix4x4.CreateTranslation(headBone.Translation);
        }

        foreach (var eyePath in new[] { npc.LeftEyeNifPath, npc.RightEyeNifPath })
        {
            if (eyePath == null)
            {
                continue;
            }

            var eyeRaw = NpcMeshHelpers.LoadNifRawFromBsa(eyePath, meshArchives);
            if (eyeRaw == null)
            {
                continue;
            }

            var eyeModel = NifGeometryExtractor.Extract(eyeRaw.Value.Data, eyeRaw.Value.Info, textureResolver);
            if (eyeModel == null)
            {
                continue;
            }

            if (NpcRenderHelpers.TryGetRootRotationCompensation(eyeRaw.Value.Data, eyeRaw.Value.Info,
                    out var rootCompensation))
            {
                NpcRenderHelpers.TransformModel(eyeModel, rootCompensation);
            }

            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                NpcMeshHelpers.LoadAndApplyEgm(
                    Path.ChangeExtension(eyePath, ".egm"),
                    eyeModel,
                    npc.FaceGenSymmetricCoeffs,
                    npc.FaceGenAsymmetricCoeffs,
                    meshArchives,
                    egmCache);
            }

            if (eyeAttachmentTransform.HasValue)
            {
                NpcRenderHelpers.TransformModel(eyeModel, eyeAttachmentTransform.Value);
            }

            if (npc.EyeTexturePath != null)
            {
                foreach (var submesh in eyeModel.Submeshes)
                {
                    submesh.DiffuseTexturePath = npc.EyeTexturePath;
                }
            }

            NpcExportSceneBuilder.AddRigidModel(scene, eyePath, eyeModel);
        }
    }

    private static void AddHeadEquipment(
        NpcExportScene scene,
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        bool usedBaseRaceMesh,
        Dictionary<string, int>? nodeIndicesByBoneName,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        Matrix4x4? bonelessAttachmentTransform,
        bool includeEquipment)
    {
        if (!includeEquipment || npc.EquippedItems == null)
        {
            return;
        }

        foreach (var item in npc.EquippedItems.Where(item => NpcTextureHelpers.IsHeadEquipment(item.BipedFlags)))
        {
            var raw = NpcMeshHelpers.LoadNifRawFromBsa(item.MeshPath, meshArchives);
            if (raw == null)
            {
                continue;
            }

            var hasSkinning =
                raw.Value.Info.Blocks.Any(block => block.TypeName is "NiSkinInstance" or "BSDismemberSkinInstance");
            if (hasSkinning && nodeIndicesByBoneName != null)
            {
                NpcExportSceneBuilder.AddSkinnedNif(scene, item.MeshPath, meshArchives, nodeIndicesByBoneName);
                continue;
            }

            var model = NifGeometryExtractor.Extract(raw.Value.Data, raw.Value.Info, textureResolver);
            if (model == null)
            {
                continue;
            }

            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                NpcMeshHelpers.LoadAndApplyEgm(
                    Path.ChangeExtension(item.MeshPath, ".egm"),
                    model,
                    npc.FaceGenSymmetricCoeffs,
                    npc.FaceGenAsymmetricCoeffs,
                    meshArchives,
                    egmCache);
            }

            if (attachmentBoneTransforms != null &&
                attachmentBoneTransforms.TryGetValue("Bip01 Head", out var headBone))
            {
                NpcRenderHelpers.ApplyHeadBoneCorrection(
                    model,
                    raw.Value.Data,
                    raw.Value.Info,
                    headBone,
                    bonelessAttachmentTransform,
                    item.MeshPath,
                    NpcRenderHelpers.HeadAttachmentRootPolicy.CompensateRotatedRoot);
            }

            NpcExportSceneBuilder.AddRigidModel(scene, item.MeshPath, model);
        }
    }

    private static float[]? ComputeHeadPreSkinDeltas(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, EgmParser?> egmCache,
        NpcExportSettings settings)
    {
        if (settings.NoEgm ||
            npc.BaseHeadNifPath == null ||
            (npc.FaceGenSymmetricCoeffs == null && npc.FaceGenAsymmetricCoeffs == null))
        {
            return null;
        }

        var egmPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egm");
        var egm = NpcMeshHelpers.LoadAndCacheEgm(egmPath, meshArchives, egmCache);
        return egm == null
            ? null
            : FaceGenMeshMorpher.ComputeAccumulatedDeltas(
                egm,
                npc.FaceGenSymmetricCoeffs,
                npc.FaceGenAsymmetricCoeffs,
                egm.VertexCount);
    }

    private static string? ApplyHeadEgtMorph(
        NpcAppearance npc,
        string fullHeadTexturePath,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache)
    {
        if (npc.BaseHeadNifPath == null || npc.FaceGenTextureCoeffs == null)
        {
            return null;
        }

        var egtPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egt");
        if (!egtCache.TryGetValue(egtPath, out var egt))
        {
            egt = NpcMeshHelpers.LoadEgtFromBsa(egtPath, meshArchives);
            egtCache[egtPath] = egt;
        }

        var baseTexture = egt == null ? null : textureResolver.GetTexture(fullHeadTexturePath);
        if (egt == null || baseTexture == null)
        {
            return null;
        }

        var morphedTexture = FaceGenTextureMorpher.Apply(baseTexture, egt, npc.FaceGenTextureCoeffs);
        if (morphedTexture == null)
        {
            return null;
        }

        var textureKey = NpcTextureHelpers.BuildNpcFaceEgtTextureKey(npc);
        textureResolver.InjectTexture(textureKey, morphedTexture);
        return textureKey;
    }
}
