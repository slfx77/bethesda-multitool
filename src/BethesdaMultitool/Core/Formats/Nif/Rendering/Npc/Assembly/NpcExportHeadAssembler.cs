using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Head, hair, eyes, and face part assembly methods for NPC export scene construction.
/// </summary>
internal static class NpcExportHeadAssembler
{
    internal static void AddHeadContent(
        GlbScene scene,
        NpcCompositionPlan plan,
        MeshArchiveSet meshArchives,
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

                    // FaceGen morph + seam-normal weld must run BEFORE the scene-add call
                    // — NpcExportSceneBuilder.AddSkinnedPart / AddExtractedRigidPart both
                    // deep-clone the submesh (CloneSubmesh) and any later mutation of
                    // part.Submesh is lost. The previous post-loop RecalculateNormals call
                    // was operating on the orphaned original and never reached the GLB.
                    if (headPlan.HeadPreSkinMorphDeltas != null)
                    {
                        FaceGenMeshMorpher.RecalculateNormals(part.Submesh);
                    }
                    else if (part.Submesh.Normals != null)
                    {
                        // Always weld co-located seam normals (eye sockets, mouth interior,
                        // neck rim), even on the non-morphed code path. The glTF PBR pipeline
                        // amplifies per-vertex TBN inconsistency at unwelded seams into visible
                        // splotches / triangular holes that the rasterizer's per-pixel shader
                        // smooths over. WeldSeamNormals is hemisphere-split so opposing-normal
                        // seam partners (mouth interior vs face exterior) stay in distinct weld
                        // groups instead of canceling to a zero direction.
                        FaceGenMeshMorpher.WeldSeamNormals(
                            part.Submesh.Positions,
                            part.Submesh.Normals);
                    }

                    if (part.Skin != null && nodeIndicesByBoneName != null)
                    {
                        NpcExportSceneBuilder.AddSkinnedPart(
                            scene,
                            part,
                            nodeIndicesByBoneName,
                            headPlan.BaseHeadNifPath);
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
        GlbScene scene,
        NpcAppearance npc,
        MeshArchiveSet meshArchives,
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

                    // Morph + seam weld must run BEFORE scene-add since the scene-add
                    // deep-clones the submesh (see other AddHeadContent overload for full
                    // explanation). Post-add mutation never reaches the GLB.
                    if (headPreSkinDeltas != null)
                    {
                        FaceGenMeshMorpher.RecalculateNormals(part.Submesh);
                    }
                    else if (part.Submesh.Normals != null)
                    {
                        FaceGenMeshMorpher.WeldSeamNormals(
                            part.Submesh.Positions,
                            part.Submesh.Normals);
                    }

                    if (part.Skin != null && nodeIndicesByBoneName != null)
                    {
                        NpcExportSceneBuilder.AddSkinnedPart(
                            scene,
                            part,
                            nodeIndicesByBoneName,
                            npc.BaseHeadNifPath);
                    }
                    else
                    {
                        NpcExportSceneBuilder.AddExtractedRigidPart(scene, part, part.ShapeWorldTransform,
                            npc.BaseHeadNifPath);
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

    /// <summary>
    ///     Welds co-located vertex normals on every submesh of a renderable model.
    ///     Must be called before <see cref="NpcExportSceneBuilder.AddRigidModel" />
    ///     (and its peers) because those deep-clone the submesh — any normal mutation
    ///     afterwards never reaches the exported GLB. The weld is hemisphere-split, so
    ///     opposing seam partners (e.g. mouth interior vs face exterior) don't average
    ///     to zero. Idempotent: re-welding already-welded vertices is a no-op.
    /// </summary>
    private static void WeldSubmeshSeams(NifRenderableModel model)
    {
        foreach (var submesh in model.Submeshes)
        {
            if (submesh.Normals != null)
            {
                FaceGenMeshMorpher.WeldSeamNormals(submesh.Positions, submesh.Normals);
            }
        }
    }

    private static void AddHair(
        GlbScene scene,
        NpcAppearance npc,
        MeshArchiveSet meshArchives,
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
            // flat normals. The engine renders them as-is. Do not smooth hair normals
            // (RecalculateNormals + WeldSeamNormals): averaging normals across hair
            // cards facing very different directions produces sideways-pointing
            // normals at silhouette edges that read as dark patches in glTF PBR
            // viewers. Trust the authored NIF normals.
        }

        NpcExportSceneBuilder.AddRigidModel(scene, npc.HairNifPath, hairModel);
    }

    private static void AddRaceFaceParts(
        GlbScene scene,
        NpcAppearance npc,
        MeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        bool usedBaseRaceMesh,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        Matrix4x4? bonelessAttachmentTransform)
    {
        foreach (var facePartPath in new[]
                 {
                     npc.EarNifPath,
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

            if (string.Equals(facePartPath, npc.EarNifPath, StringComparison.OrdinalIgnoreCase) &&
                npc.EarTexturePath != null)
            {
                foreach (var submesh in partModel.Submeshes)
                {
                    submesh.DiffuseTexturePath = npc.EarTexturePath;
                }
            }

            WeldSubmeshSeams(partModel);
            NpcExportSceneBuilder.AddRigidModel(scene, facePartPath, partModel);
        }
    }

    private static void AddHeadParts(
        GlbScene scene,
        NpcAppearance npc,
        MeshArchiveSet meshArchives,
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

            WeldSubmeshSeams(partModel);
            NpcExportSceneBuilder.AddRigidModel(scene, headPartPath, partModel);
        }
    }

    private static void AddEyes(
        GlbScene scene,
        NpcAppearance npc,
        MeshArchiveSet meshArchives,
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

            WeldSubmeshSeams(eyeModel);
            NpcExportSceneBuilder.AddRigidModel(scene, eyePath, eyeModel);
        }
    }

    private static void AddHeadEquipment(
        GlbScene scene,
        NpcAppearance npc,
        MeshArchiveSet meshArchives,
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
        MeshArchiveSet meshArchives,
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
        MeshArchiveSet meshArchives,
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
