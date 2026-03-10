using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.CLI.Rendering.Npc;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

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
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
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
        NifRenderableModel? model = null;
        var headTexturePath = npc.HeadDiffuseOverride;
        var usedBaseRaceMesh = false;
        var isFullBody = skeletonBones != null || idlePoseBones != null;
        var effectiveHairFilter = hairFilterOverride ?? ResolveHairFilter(npc, s);

        // Bone transforms for positioning unskinned attachments (hair, eyes, head parts).
        // Full-body mode: skeleton's idle-pose bones (target space for attachments).
        // Head-only mode: head NIF's own bones (no Bip01 chain rotation).
        var attachmentBoneTransforms = skeletonBones ?? idlePoseBones;
        var effectiveBonelessAttachmentTransform = headEquipmentTransformOverride;

        // Compute pre-skinning EGM morph deltas for the head mesh.
        // EGM deltas are in NIF bind-pose space and must be applied BEFORE bone skinning
        // transforms the vertices. This avoids the coordinate frame mismatch that caused
        // face distortion when applying EGM post-skinning with a uniform rotation.
        float[]? headPreSkinDeltas = null;
        if (npc.BaseHeadNifPath != null && !s.NoEgm &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var egmPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egm");
            var egm = NpcRenderHelpers.LoadAndCacheEgm(egmPath, meshesArchive, meshExtractor, egmCache);
            if (egm != null && egm.VertexCount > 0)
            {
                // Use EGM vertex count as upper bound; the delta application in
                // NifSubmeshExtractor clamps to min(positions.Length, deltas.Length).
                headPreSkinDeltas = FaceGenMeshMorpher.ComputeAccumulatedDeltas(
                    egm, npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs, egm.VertexCount);
            }
        }

        if (npc.BaseHeadNifPath != null)
        {
            // Both modes use the same skinning path: extract named bone transforms,
            // pass them as externalBoneTransforms, and use DQS. This ensures the head
            // mesh vertices and attachment bone corrections share the exact same bone
            // dictionary regardless of render mode.
            if (isFullBody)
            {
                var skelBones = skeletonBones ?? idlePoseBones;
                model = LoadHeadWithPreSkinMorphs(npc.BaseHeadNifPath, meshesArchive, meshExtractor,
                    textureResolver, skelBones, headPreSkinDeltas);
                if (model != null)
                    usedBaseRaceMesh = true;
            }
            else
            {
                // Head-only mode: extract bones from the head NIF, then use them for
                // both skinning and attachment correction — same pattern as full-body.
                var headRaw = NpcRenderHelpers.LoadNifRawFromBsa(npc.BaseHeadNifPath, meshesArchive,
                    meshExtractor);
                if (headRaw != null)
                {
                    attachmentBoneTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
                        headRaw.Value.Data, headRaw.Value.Info);
                    if (attachmentBoneTransforms.Count == 0)
                        Log.Warn("Head NIF has 0 named bone transforms: {0}", npc.BaseHeadNifPath);
                }

                // Head-only: can't cache when using pre-skin deltas (per-NPC morphs).
                // Cache only the unmorphed base mesh; apply morphs after cloning.
                if (headPreSkinDeltas != null)
                {
                    // Per-NPC morphed extraction — no caching
                    model = LoadHeadWithPreSkinMorphs(npc.BaseHeadNifPath, meshesArchive, meshExtractor,
                        textureResolver, attachmentBoneTransforms, headPreSkinDeltas);
                }
                else
                {
                    // No EGM morphs — use cache
                    if (!headMeshCache.TryGetValue(npc.BaseHeadNifPath, out var cached))
                    {
                        cached = NpcRenderHelpers.LoadNifFromBsa(npc.BaseHeadNifPath, meshesArchive,
                            meshExtractor, textureResolver, attachmentBoneTransforms,
                            useDualQuaternionSkinning: true);
                        headMeshCache[npc.BaseHeadNifPath] = cached;
                    }

                    if (cached != null)
                        model = NpcRenderHelpers.DeepCloneModel(cached);
                }

                if (model != null)
                    usedBaseRaceMesh = true;
            }
        }

        // Fallback: try per-NPC FaceGen mesh (already pre-morphed, skip EGM)
        if (model == null && npc.FaceGenNifPath != null)
            model = NpcRenderHelpers.LoadNifFromBsa(npc.FaceGenNifPath, meshesArchive, meshExtractor, textureResolver);

        if (model == null || !model.HasGeometry)
            return null;

        effectiveBonelessAttachmentTransform ??=
            NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(
                attachmentBoneTransforms,
                poseDeltaCache: null);

        // Apply head texture override from RACE INDX 0 ICON
        string? fullTexPath = null;
        if (headTexturePath != null)
        {
            fullTexPath = "textures\\" + headTexturePath;
            foreach (var submesh in model.Submeshes)
                submesh.DiffuseTexturePath = fullTexPath;
        }

        // Apply EGT texture morphs
        if (!s.NoEgt && usedBaseRaceMesh && npc.BaseHeadNifPath != null &&
            npc.FaceGenTextureCoeffs != null && fullTexPath != null)
        {
            ApplyHeadEgtMorphs(npc, fullTexPath, meshesArchive, meshExtractor, textureResolver,
                egtCache, model, s);
        }

        Log.Debug("Head bounds: ({0:F2}, {1:F2}, {2:F2}) → ({3:F2}, {4:F2}, {5:F2})",
            model.MinX, model.MinY, model.MinZ, model.MaxX, model.MaxY, model.MaxZ);

        // Load and attach hair mesh
        if (npc.HairNifPath != null)
        {
            AttachHairMesh(npc, model, attachmentBoneTransforms, usedBaseRaceMesh,
                effectiveHairFilter, meshesArchive, meshExtractor, textureResolver, egmCache,
                effectiveBonelessAttachmentTransform);
        }

        // Load and attach head part meshes (eyebrows, beards, teeth, etc. from PNAM → HDPT)
        if (npc.HeadPartNifPaths != null)
        {
            AttachHeadParts(npc, model, attachmentBoneTransforms, usedBaseRaceMesh,
                meshesArchive, meshExtractor, textureResolver, egmCache,
                effectiveBonelessAttachmentTransform);
        }

        // Load and attach eye meshes (left and right)
        AttachEyeMeshes(npc, model, attachmentBoneTransforms, usedBaseRaceMesh,
            meshesArchive, meshExtractor, textureResolver, egmCache,
            effectiveBonelessAttachmentTransform);

        if (!s.NoEquip && npc.EquippedItems != null)
        {
            AttachHeadEquipment(
                npc,
                model,
                attachmentBoneTransforms,
                usedBaseRaceMesh,
                meshesArchive,
                meshExtractor,
                textureResolver,
                egmCache,
                effectiveBonelessAttachmentTransform);
        }

        return model;
    }

    private static NifRenderableModel? LoadHeadWithPreSkinMorphs(
        string headNifPath,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? boneTransforms,
        float[]? preSkinMorphDeltas)
    {
        var result = NpcRenderHelpers.LoadNifRawFromBsa(headNifPath, meshesArchive, meshExtractor);
        if (result == null)
            return null;

        var model = NifGeometryExtractor.Extract(result.Value.Data, result.Value.Info, textureResolver,
            externalBoneTransforms: boneTransforms,
            useDualQuaternionSkinning: true,
            preSkinMorphDeltas: preSkinMorphDeltas);

        // When EGM deltas were applied pre-skinning, recalculate normals from the
        // final morphed+skinned positions so lighting reflects the morphed geometry.
        if (model != null && preSkinMorphDeltas != null)
        {
            foreach (var sub in model.Submeshes)
                FaceGenMeshMorpher.RecalculateNormals(sub);
        }

        return model;
    }

    private static string? ResolveHairFilter(NpcAppearance npc, NpcRenderSettings settings)
    {
        if (settings.NoEquip)
            return null;

        return NpcRenderHelpers.HasHatEquipment(npc.EquippedItems) ? "Hat" : null;
    }

    private static void ApplyHeadEgtMorphs(
        NpcAppearance npc, string fullTexPath,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache,
        NifRenderableModel model, NpcRenderSettings s)
    {
        var egtPath = Path.ChangeExtension(npc.BaseHeadNifPath!, ".egt");

        if (!egtCache.TryGetValue(egtPath, out var egt))
        {
            egt = NpcRenderHelpers.LoadEgtFromBsa(egtPath, meshesArchive, meshExtractor);
            egtCache[egtPath] = egt;
        }

        if (egt == null)
            return;

        FaceGenTextureMorpher.DebugLabel = npc.EditorId ?? $"{npc.NpcFormId:X8}";

        var baseTexture = textureResolver.GetTexture(fullTexPath);
        if (baseTexture == null)
        {
            Log.Warn("Base head texture not found for EGT morph: {0}", fullTexPath);
            return;
        }

        var morphed = FaceGenTextureMorpher.Apply(baseTexture, egt, npc.FaceGenTextureCoeffs!);
        if (morphed == null)
        {
            Log.Warn("EGT texture morph returned null for NPC 0x{0:X8} (base texture: {1})",
                npc.NpcFormId, fullTexPath);
            return;
        }

        if (s.ExportEgt)
        {
            var egtDir = Path.Combine(s.OutputDir, "egt_debug");
            var label = npc.EditorId ?? $"{npc.NpcFormId:X8}";
            PngWriter.SaveRgba(baseTexture.Pixels, baseTexture.Width, baseTexture.Height,
                Path.Combine(egtDir, $"{label}_base_{baseTexture.Width}x{baseTexture.Height}.png"));
            PngWriter.SaveRgba(morphed.Pixels, morphed.Width, morphed.Height,
                Path.Combine(egtDir, $"{label}_morphed_{morphed.Width}x{morphed.Height}.png"));
        }

        var npcTexKey = $"facegen_egt\\{npc.NpcFormId:X8}.dds";
        textureResolver.InjectTexture(npcTexKey, morphed);
        foreach (var submesh in model.Submeshes)
            submesh.DiffuseTexturePath = npcTexKey;
    }

    private static void AttachHairMesh(
        NpcAppearance npc, NifRenderableModel model,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        bool usedBaseRaceMesh, string? hairFilterOverride,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Matrix4x4? bonelessAttachmentTransform)
    {
        var hairNifPath = npc.HairNifPath!;
        var hairBaseName = Path.GetFileNameWithoutExtension(hairNifPath);
        var hairDir = Path.GetDirectoryName(hairNifPath) ?? "";

        var hairRaw = NpcRenderHelpers.LoadNifRawFromBsa(
            hairNifPath,
            meshesArchive,
            meshExtractor);
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
        // head bone includes Bip01's 90° Z rotation, so boneless NIFs need the head NIF's
        // own Bip01 Head as a reference to remap correctly: inv(headNifBone) * skelBone.
        // Apply EGM morphs BEFORE boneless correction — EGM deltas are in NIF local space.
        if (usedBaseRaceMesh &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var egmSuffix = hairFilterOverride == "Hat" ? "hat.egm" : "nohat.egm";
            var hairEgmPath = Path.Combine(hairDir, hairBaseName + egmSuffix);
            NpcRenderHelpers.LoadAndApplyEgm(hairEgmPath, hairModel,
                npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                meshesArchive, meshExtractor, egmCache);
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
                "'Bip01 Head' bone not found — hair will render at origin. Available bones: {0}",
                availableBones);
        }

        // Merge hair submeshes into head model
        var hairTint = NpcRenderHelpers.UnpackHairColor(npc.HairColor);
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
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Matrix4x4? bonelessAttachmentTransform)
    {
        Matrix4x4? eyeAttachmentTransform = bonelessAttachmentTransform;
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

            var eyeRaw = NpcRenderHelpers.LoadNifRawFromBsa(eyeNifPath, meshesArchive, meshExtractor);
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
                NpcRenderHelpers.LoadAndApplyEgm(eyeEgmPath, eyeModel,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                    meshesArchive, meshExtractor, egmCache);
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
                    "'Bip01 Head' bone not found — eye will render at origin. Available bones: {0}",
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
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Matrix4x4? bonelessAttachmentTransform)
    {
        foreach (var partPath in npc.HeadPartNifPaths!)
        {
            var partRaw = NpcRenderHelpers.LoadNifRawFromBsa(partPath, meshesArchive, meshExtractor);
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

            // Apply EGM morphs BEFORE boneless correction — EGM deltas are in NIF local space.
            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                var egmPath = Path.ChangeExtension(partPath, ".egm");
                NpcRenderHelpers.LoadAndApplyEgm(egmPath, partModel,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                    meshesArchive, meshExtractor, egmCache);
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
            var partTint = NpcRenderHelpers.UnpackHairColor(npc.HairColor);
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
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Matrix4x4? bonelessAttachmentTransform)
    {
        foreach (var item in npc.EquippedItems!)
        {
            if (!NpcRenderHelpers.IsHeadEquipment(item.BipedFlags))
                continue;

            var equipRaw = NpcRenderHelpers.LoadNifRawFromBsa(item.MeshPath, meshesArchive, meshExtractor);
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

            var hasSkinning = equipRaw.Value.Info.Blocks.Any(
                block => block.TypeName is "NiSkinInstance" or "BSDismemberSkinInstance");

            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                var egmPath = Path.ChangeExtension(item.MeshPath, ".egm");
                NpcRenderHelpers.LoadAndApplyEgm(
                    egmPath,
                    equipModel,
                    npc.FaceGenSymmetricCoeffs,
                    npc.FaceGenAsymmetricCoeffs,
                    meshesArchive,
                    meshExtractor,
                    egmCache);
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
                        item.MeshPath);
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
