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

        AttachRaceFaceParts(
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
            AttachHairMesh(
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
            AttachHeadParts(
                npc,
                model,
                headPlan.AttachmentBoneTransforms,
                usedBaseRaceMesh,
                meshArchives,
                textureResolver,
                compositionCaches.EgmFiles,
                headPlan.BonelessAttachmentTransform);
        }

        AttachEyeMeshes(
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
            AttachHeadEquipment(
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

    private static void AttachRaceFaceParts(
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
                IsMouthPart(partPath))
            {
                var morphMagnitude = EstimateFaceGenMorphMagnitude(npc.FaceGenSymmetricCoeffs);
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

    private static void AttachHairMesh(
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

        // Position hair: parent to Bip01 Head bone. In full-body mode, the skeleton's
        // head bone includes Bip01's 90Ãƒâ€šÃ‚Â° Z rotation, so boneless NIFs need the head NIF's
        // own Bip01 Head as a reference to remap correctly: inv(headNifBone) * skelBone.
        // Apply EGM morphs BEFORE boneless correction ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â EGM deltas are in NIF local space.
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
                "'Bip01 Head' bone not found ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â hair will render at origin. Available bones: {0}",
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

    /// <summary>
    ///     Loads left and right eye NIFs, restores their vertices to head-local bind-pose
    ///     space by undoing the eye NIF's rotated __root__, applies EGM in that space,
    ///     then attaches them with the direct boneless head transform. This avoids routing
    ///     eyes through the generic boned-attachment path that caused them to bulge out of
    ///     the sockets.
    /// </summary>
    private static void AttachEyeMeshes(
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

            // Eye NIFs bake a rotated __root__ into extracted vertices. Undo that first so the
            // eyeball vertices are back in head-local bind-pose space before any morphs or
            // attachment transforms are applied.
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

            // Apply EGM morphs in bind-pose eye space, after root compensation.
            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                var eyeEgmPath = Path.ChangeExtension(eyeNifPath, ".egm");
                NpcMeshHelpers.LoadAndApplyEgm(eyeEgmPath, eyeModel,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                    meshArchives, egmCache,
                    false);
            }

            // Attach eyes with the direct boneless head transform.
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
                    "'Bip01 Head' bone not found ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â eye will render at origin. Available bones: {0}",
                    availableBones);
            }

            // Override eye texture from EYES record.
            if (npc.EyeTexturePath != null)
            {
                foreach (var sub in eyeModel.Submeshes)
                    sub.DiffuseTexturePath = npc.EyeTexturePath;
            }

            // Merge eye submeshes into head model.
            foreach (var sub in eyeModel.Submeshes)
            {
                sub.RenderOrder = 2;
                model.Submeshes.Add(sub);
                model.ExpandBounds(sub.Positions);
            }
        }
    }

    private static void AttachHeadParts(
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

            // Apply EGM morphs BEFORE boneless correction ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â EGM deltas are in NIF local space.
            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                var egmPath = Path.ChangeExtension(partPath, ".egm");
                NpcMeshHelpers.LoadAndApplyEgm(egmPath, partModel,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                    meshArchives, egmCache,
                    false);
            }

            // Position head parts: parent to Bip01 Head bone (same as hair).
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

            // Merge into head model. RenderOrder=0 (before hair).
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

    private static void AttachHeadEquipment(
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
