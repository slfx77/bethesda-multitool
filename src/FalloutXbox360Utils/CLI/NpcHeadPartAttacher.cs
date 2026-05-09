using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Head part attachment methods (race face parts, hair, eyes, head equipment).
///     Extracted from NpcHeadBuilder.
/// </summary>
internal static class NpcHeadPartAttacher
{
    private static readonly Logger Log = Logger.Instance;

    internal static void AttachRaceFaceParts(
        NpcAppearance npc,
        NifRenderableModel model,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        bool usedBaseRaceMesh,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Matrix4x4? bonelessAttachmentTransform)
    {
        foreach (var partPath in new[]
                 {
                     npc.MouthNifPath,
                     npc.LowerTeethNifPath,
                     npc.UpperTeethNifPath,
                     npc.TongueNifPath
                 })
        {
            if (partPath == null)
            {
                continue;
            }

            var partRaw = NpcMeshHelpers.LoadNifRawFromBsa(partPath, meshArchives);
            if (partRaw == null)
            {
                Log.Warn("Race face part NIF failed to load: {0}", partPath);
                continue;
            }

            var partModel = NifGeometryExtractor.Extract(partRaw.Value.Data, partRaw.Value.Info, textureResolver);
            if (partModel == null || !partModel.HasGeometry)
            {
                Log.Warn("Race face part NIF has no geometry: {0}", partPath);
                continue;
            }

            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                NpcMeshHelpers.LoadAndApplyEgm(
                    Path.ChangeExtension(partPath, ".egm"),
                    partModel,
                    npc.FaceGenSymmetricCoeffs,
                    npc.FaceGenAsymmetricCoeffs,
                    meshArchives,
                    egmCache,
                    false);
            }

            // Apply a small inward offset to mouth/teeth parts when FaceGen morphs
            // are active. Morphs can push the face inward (thin face, etc.) while
            // mouth parts stay in their original position, causing clipping.
            if (usedBaseRaceMesh && npc.FaceGenSymmetricCoeffs != null &&
                NpcHeadBuilder.IsMouthPart(partPath))
            {
                var morphMagnitude = NpcHeadBuilder.EstimateFaceGenMorphMagnitude(npc.FaceGenSymmetricCoeffs);
                if (morphMagnitude > 0.01f)
                {
                    // Push mouth slightly inward (negative Y in NIF head-local space).
                    // Scale is empirical: -0.15 per unit of average morph coefficient.
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
                    partPath,
                    NpcRenderHelpers.HeadAttachmentRootPolicy.CompensateRotatedRoot);
            }

            foreach (var sub in partModel.Submeshes)
            {
                sub.RenderOrder = 0;
                model.Submeshes.Add(sub);
                model.ExpandBounds(sub.Positions);
            }
        }
    }

    internal static void AttachHairMesh(
        NpcAppearance npc, NifRenderableModel model,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        bool usedBaseRaceMesh, string? hairFilterOverride,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Matrix4x4? bonelessAttachmentTransform)
    {
        var hairNifPath = npc.HairNifPath!;
        var hairBaseName = Path.GetFileNameWithoutExtension(hairNifPath);
        var hairDir = Path.GetDirectoryName(hairNifPath) ?? "";

        var hairRaw = NpcMeshHelpers.LoadNifRawFromBsa(
            hairNifPath,
            meshArchives);
        if (hairRaw == null)
        {
            Log.Warn("Hair NIF failed to load: {0}", hairNifPath);
            return;
        }

        var hairModel = NifGeometryExtractor.Extract(hairRaw.Value.Data, hairRaw.Value.Info, textureResolver,
            filterShapeName: hairFilterOverride ?? "NoHat");
        if (hairModel == null || !hairModel.HasGeometry)
        {
            Log.Warn("Hair NIF has no geometry: {0}", hairNifPath);
            return;
        }

        if (usedBaseRaceMesh &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var egmSuffix = hairFilterOverride == "Hat" ? "hat.egm" : "nohat.egm";
            var hairEgmPath = Path.Combine(hairDir, hairBaseName + egmSuffix);
            NpcMeshHelpers.LoadAndApplyEgm(hairEgmPath, hairModel,
                npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                meshArchives, egmCache,
                false);
        }

        if (attachmentBoneTransforms != null &&
            attachmentBoneTransforms.TryGetValue("Bip01 Head", out var headBoneMatrix))
        {
            NpcRenderHelpers.ApplyHeadBoneCorrection(
                hairModel,
                hairRaw.Value.Data,
                hairRaw.Value.Info,
                headBoneMatrix,
                bonelessAttachmentTransform,
                hairNifPath);
        }
        else
        {
            var availableBones = attachmentBoneTransforms == null
                ? "(null)"
                : string.Join(", ", attachmentBoneTransforms.Keys);
            Log.Warn(
                "'Bip01 Head' bone not found \u2014 hair will render at origin. Available bones: {0}",
                availableBones);
        }

        // Merge hair submeshes into head model
        var hairTint = NpcTextureHelpers.UnpackHairColor(npc.HairColor);
        foreach (var sub in hairModel.Submeshes)
        {
            sub.TintColor = hairTint;
            if (npc.HairTexturePath != null)
                sub.DiffuseTexturePath = npc.HairTexturePath;

            sub.RenderOrder = 1;
            model.Submeshes.Add(sub);
            model.ExpandBounds(sub.Positions);
        }
    }

    internal static void AttachEyeMeshes(
        NpcAppearance npc, NifRenderableModel model,
        Dictionary<string, Matrix4x4>? headBoneTransforms,
        bool usedBaseRaceMesh,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Matrix4x4? bonelessAttachmentTransform)
    {
        var eyeAttachmentTransform = bonelessAttachmentTransform;
        if (eyeAttachmentTransform == null &&
            headBoneTransforms != null &&
            headBoneTransforms.TryGetValue("Bip01 Head", out var headBoneMatrix))
        {
            eyeAttachmentTransform = Matrix4x4.CreateTranslation(headBoneMatrix.Translation);
        }

        foreach (var eyeNifPath in new[] { npc.LeftEyeNifPath, npc.RightEyeNifPath })
        {
            if (eyeNifPath == null)
                continue;

            var eyeRaw = NpcMeshHelpers.LoadNifRawFromBsa(eyeNifPath, meshArchives);
            if (eyeRaw == null)
            {
                Log.Warn("Eye NIF failed to load: {0}", eyeNifPath);
                continue;
            }

            var eyeModel = NifGeometryExtractor.Extract(eyeRaw.Value.Data, eyeRaw.Value.Info, textureResolver);
            if (eyeModel == null || !eyeModel.HasGeometry)
            {
                Log.Warn("Eye NIF has no geometry: {0}", eyeNifPath);
                continue;
            }

            if (NpcRenderHelpers.TryGetRootRotationCompensation(
                    eyeRaw.Value.Data, eyeRaw.Value.Info, out var rootCompensation))
            {
                NpcRenderHelpers.TransformModel(eyeModel, rootCompensation);
            }
            else
            {
                Log.Warn(
                    "Eye NIF '{0}' has no usable root rotation compensation; attaching without bind-pose correction",
                    eyeNifPath);
            }

            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                var eyeEgmPath = Path.ChangeExtension(eyeNifPath, ".egm");
                NpcMeshHelpers.LoadAndApplyEgm(eyeEgmPath, eyeModel,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                    meshArchives, egmCache,
                    false);
            }

            if (eyeAttachmentTransform != null)
            {
                NpcRenderHelpers.TransformModel(eyeModel, eyeAttachmentTransform.Value);
            }
            else
            {
                var availableBones = headBoneTransforms == null
                    ? "(null)"
                    : string.Join(", ", headBoneTransforms.Keys);
                Log.Warn(
                    "'Bip01 Head' bone not found \u2014 eye will render at origin. Available bones: {0}",
                    availableBones);
            }

            if (npc.EyeTexturePath != null)
            {
                foreach (var sub in eyeModel.Submeshes)
                    sub.DiffuseTexturePath = npc.EyeTexturePath;
            }

            foreach (var sub in eyeModel.Submeshes)
            {
                sub.RenderOrder = 2;
                model.Submeshes.Add(sub);
                model.ExpandBounds(sub.Positions);
            }
        }
    }

    internal static void AttachHeadParts(
        NpcAppearance npc, NifRenderableModel model,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        bool usedBaseRaceMesh,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Matrix4x4? bonelessAttachmentTransform)
    {
        foreach (var partPath in npc.HeadPartNifPaths!)
        {
            var partRaw = NpcMeshHelpers.LoadNifRawFromBsa(partPath, meshArchives);
            if (partRaw == null)
            {
                Log.Warn("Head part NIF failed to load: {0}", partPath);
                continue;
            }

            var partModel = NifGeometryExtractor.Extract(partRaw.Value.Data, partRaw.Value.Info, textureResolver);
            if (partModel == null || !partModel.HasGeometry)
            {
                Log.Warn("Head part NIF has no geometry: {0}", partPath);
                continue;
            }

            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                var egmPath = Path.ChangeExtension(partPath, ".egm");
                NpcMeshHelpers.LoadAndApplyEgm(egmPath, partModel,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                    meshArchives, egmCache,
                    false);
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
                    partPath);
            }

            var partTint = NpcTextureHelpers.UnpackHairColor(npc.HairColor);
            foreach (var sub in partModel.Submeshes)
            {
                Log.Info("HeadPart '{0}' sub: tex={1}, alphaTest={2} func={3} thresh={4}, " +
                         "alphaBlend={5}, matAlpha={6:F2}, vcol={7}, doubleSided={8}, verts={9}",
                    partPath, sub.DiffuseTexturePath ?? "(none)",
                    sub.HasAlphaTest, sub.AlphaTestFunction, sub.AlphaTestThreshold,
                    sub.HasAlphaBlend, sub.MaterialAlpha, sub.UseVertexColors,
                    sub.IsDoubleSided, sub.VertexCount);
                sub.TintColor = partTint;
                sub.RenderOrder = 0;
                model.Submeshes.Add(sub);
                model.ExpandBounds(sub.Positions);
            }
        }
    }

    internal static void AttachHeadEquipment(
        NpcAppearance npc,
        NifRenderableModel model,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        bool usedBaseRaceMesh,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Matrix4x4? bonelessAttachmentTransform)
    {
        foreach (var item in npc.EquippedItems!)
        {
            if (!NpcTextureHelpers.IsHeadEquipment(item.BipedFlags))
                continue;

            var equipRaw = NpcMeshHelpers.LoadNifRawFromBsa(item.MeshPath, meshArchives);
            if (equipRaw == null)
            {
                Log.Warn("Head equipment NIF failed to load: {0}", item.MeshPath);
                continue;
            }

            var equipModel = NifGeometryExtractor.Extract(
                equipRaw.Value.Data,
                equipRaw.Value.Info,
                textureResolver,
                externalBoneTransforms: attachmentBoneTransforms,
                useDualQuaternionSkinning: true);
            if (equipModel == null || !equipModel.HasGeometry)
            {
                Log.Warn("Head equipment NIF has no geometry: {0}", item.MeshPath);
                continue;
            }

            var hasSkinning =
                equipRaw.Value.Info.Blocks.Any(block =>
                    block.TypeName is "NiSkinInstance" or "BSDismemberSkinInstance");

            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                var egmPath = Path.ChangeExtension(item.MeshPath, ".egm");
                NpcMeshHelpers.LoadAndApplyEgm(
                    egmPath,
                    equipModel,
                    npc.FaceGenSymmetricCoeffs,
                    npc.FaceGenAsymmetricCoeffs,
                    meshArchives,
                    egmCache,
                    false);
            }

            if (!hasSkinning)
            {
                if (attachmentBoneTransforms != null &&
                    attachmentBoneTransforms.TryGetValue("Bip01 Head", out var headBone))
                {
                    NpcRenderHelpers.ApplyHeadBoneCorrection(
                        equipModel,
                        equipRaw.Value.Data,
                        equipRaw.Value.Info,
                        headBone,
                        bonelessAttachmentTransform,
                        item.MeshPath,
                        NpcRenderHelpers.HeadAttachmentRootPolicy.CompensateRotatedRoot);
                }
                else
                {
                    Log.Warn("Head equipment '{0}' missing Bip01 Head target transform", item.MeshPath);
                }
            }

            foreach (var sub in equipModel.Submeshes)
            {
                sub.RenderOrder = 3;
                model.Submeshes.Add(sub);
                model.ExpandBounds(sub.Positions);
            }
        }
    }
}
