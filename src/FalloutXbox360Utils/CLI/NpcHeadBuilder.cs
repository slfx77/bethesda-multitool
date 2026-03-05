using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;

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
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        RenderNpcCommand.NpcRenderSettings s,
        string? hairFilterOverride = null,
        Dictionary<string, Matrix4x4>? skeletonBones = null,
        Dictionary<string, Matrix4x4>? poseDeltas = null,
        Dictionary<string, Matrix4x4>? idlePoseBones = null)
    {
        NifRenderableModel? model = null;
        var headTexturePath = npc.HeadDiffuseOverride;
        var usedBaseRaceMesh = false;

        // Bone transforms for positioning unskinned attachments (hair, eyes, head parts).
        // Full-body mode: use skeleton bones (same frame as body parts).
        // Head-only mode: extract from head NIF's own scene graph.
        var attachmentBoneTransforms = skeletonBones;

        if (npc.BaseHeadNifPath != null)
        {
            if (skeletonBones != null)
            {
                // Full-body mode with skeleton bones: extract head with skeleton-driven skinning.
                model = NpcRenderHelpers.LoadNifFromBsa(npc.BaseHeadNifPath, meshesArchive, meshExtractor,
                    textureResolver, skeletonBones);
                if (model != null)
                    usedBaseRaceMesh = true;
            }
            else if (idlePoseBones != null)
            {
                // Full-body mode: use skeleton's idle-pose world transforms for head skinning.
                model = NpcRenderHelpers.LoadNifFromBsa(npc.BaseHeadNifPath, meshesArchive, meshExtractor,
                    textureResolver, idlePoseBones);
                if (model != null)
                    usedBaseRaceMesh = true;

                attachmentBoneTransforms = idlePoseBones;
            }
            else
            {
                // Head-only mode: use cache (shared across NPCs of same race/gender)
                if (!headMeshCache.TryGetValue(npc.BaseHeadNifPath, out var cached))
                {
                    cached = NpcRenderHelpers.LoadNifFromBsa(npc.BaseHeadNifPath, meshesArchive,
                        meshExtractor, textureResolver);
                    headMeshCache[npc.BaseHeadNifPath] = cached;
                }

                if (cached != null)
                {
                    model = NpcRenderHelpers.DeepCloneModel(cached);
                    usedBaseRaceMesh = true;

                    var headRaw = NpcRenderHelpers.LoadNifRawFromBsa(npc.BaseHeadNifPath, meshesArchive,
                        meshExtractor);
                    if (headRaw != null)
                    {
                        attachmentBoneTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
                            headRaw.Value.Data, headRaw.Value.Info);
                        if (attachmentBoneTransforms.Count == 0)
                            Log.Warn("Head NIF has 0 named bone transforms: {0}", npc.BaseHeadNifPath);
                    }
                    else
                    {
                        Log.Warn("Failed to load raw head NIF for bone extraction: {0}", npc.BaseHeadNifPath);
                    }
                }
            }
        }

        // Fallback: try per-NPC FaceGen mesh (already pre-morphed, skip EGM)
        if (model == null && npc.FaceGenNifPath != null)
            model = NpcRenderHelpers.LoadNifFromBsa(npc.FaceGenNifPath, meshesArchive, meshExtractor, textureResolver);

        if (model == null || !model.HasGeometry)
            return null;

        // Apply EGM morphs only when using the base race mesh (FaceGen fallback is pre-morphed)
        if (usedBaseRaceMesh && !s.NoEgm && npc.BaseHeadNifPath != null &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var egmPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egm");
            NpcRenderHelpers.LoadAndApplyEgm(egmPath, model,
                npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                meshesArchive, meshExtractor, egmCache);
        }

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
                hairFilterOverride, meshesArchive, meshExtractor, textureResolver, egmCache);
        }

        // Load and attach eye meshes (left and right independently)
        Log.Debug("Eyes: L={0}, R={1}, texture={2}",
            npc.LeftEyeNifPath ?? "(none)", npc.RightEyeNifPath ?? "(none)",
            npc.EyeTexturePath ?? "(none — no ENAM/EYES)");
        AttachEyeMesh(npc.LeftEyeNifPath, "Bip01 Head", npc, model, attachmentBoneTransforms,
            usedBaseRaceMesh, meshesArchive, meshExtractor, textureResolver, egmCache);
        AttachEyeMesh(npc.RightEyeNifPath, "Bip01 Head", npc, model, attachmentBoneTransforms,
            usedBaseRaceMesh, meshesArchive, meshExtractor, textureResolver, egmCache);

        // Load and attach head part meshes (eyebrows, beards, teeth, etc. from PNAM → HDPT)
        if (npc.HeadPartNifPaths != null)
        {
            AttachHeadParts(npc, model, attachmentBoneTransforms, usedBaseRaceMesh,
                meshesArchive, meshExtractor, textureResolver, egmCache);
        }

        return model;
    }

    private static void ApplyHeadEgtMorphs(
        NpcAppearance npc, string fullTexPath,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache,
        NifRenderableModel model, RenderNpcCommand.NpcRenderSettings s)
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
        Dictionary<string, EgmParser?> egmCache)
    {
        var hairBaseName = Path.GetFileNameWithoutExtension(npc.HairNifPath!);
        var hairDir = Path.GetDirectoryName(npc.HairNifPath!) ?? "";

        var hairModel = NpcRenderHelpers.LoadNifFromBsa(npc.HairNifPath!, meshesArchive, meshExtractor,
            textureResolver, filterShapeName: hairFilterOverride ?? "NoHat");
        if (hairModel == null || !hairModel.HasGeometry)
        {
            Log.Warn("Hair NIF failed to load or has no geometry: {0}", npc.HairNifPath);
            return;
        }

        // Hair NIFs are NOT skinned — vertices are in Bip01 Head local space.
        if (attachmentBoneTransforms != null &&
            attachmentBoneTransforms.TryGetValue("Bip01 Head", out var headBoneMatrix))
        {
            Log.Debug("Transforming hair by Bip01 Head: T=({0:F4}, {1:F4}, {2:F4})",
                headBoneMatrix.Translation.X, headBoneMatrix.Translation.Y, headBoneMatrix.Translation.Z);
            foreach (var sub in hairModel.Submeshes)
                NpcRenderHelpers.TransformSubmesh(sub, headBoneMatrix);
        }
        else
        {
            Log.Warn("'Bip01 Head' bone not found — hair will render at origin. Available bones: {0}",
                attachmentBoneTransforms != null
                    ? string.Join(", ", attachmentBoneTransforms.Keys)
                    : "(null)");
        }

        // Apply same EGM morphs to hair
        if (usedBaseRaceMesh &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var egmSuffix = hairFilterOverride == "Hat" ? "hat.egm" : "nohat.egm";
            var hairEgmPath = Path.Combine(hairDir, hairBaseName + egmSuffix);
            NpcRenderHelpers.LoadAndApplyEgm(hairEgmPath, hairModel,
                npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                meshesArchive, meshExtractor, egmCache);
        }

        // Merge hair submeshes into head model
        var hairTint = NpcRenderHelpers.UnpackHairColor(npc.HairColor);
        foreach (var sub in hairModel.Submeshes)
        {
            if (!sub.HasAlphaBlend && !sub.HasAlphaTest)
            {
                sub.HasAlphaBlend = true;
                sub.HasAlphaTest = true;
                sub.AlphaTestThreshold = 0;
            }

            sub.TintColor = hairTint;
            if (npc.HairTexturePath != null)
                sub.DiffuseTexturePath = npc.HairTexturePath;

            sub.RenderOrder = 1;
            model.Submeshes.Add(sub);
            model.ExpandBounds(sub.Positions);
        }
    }

    /// <summary>
    ///     Loads an eye NIF, positions it via bone transform, applies EGM morphs,
    ///     overrides eye texture, and merges into the head model.
    /// </summary>
    private static void AttachEyeMesh(
        string? eyeNifPath, string boneName,
        NpcAppearance npc, NifRenderableModel model,
        Dictionary<string, Matrix4x4>? headBoneTransforms,
        bool usedBaseRaceMesh,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache)
    {
        if (eyeNifPath == null)
            return;

        var eyeModel = NpcRenderHelpers.LoadNifFromBsa(eyeNifPath, meshesArchive, meshExtractor, textureResolver);
        if (eyeModel == null || !eyeModel.HasGeometry)
        {
            Log.Warn("Eye NIF failed to load or has no geometry: {0}", eyeNifPath);
            return;
        }

        Log.Debug("Eye '{0}' bounds (local): ({1:F2}, {2:F2}, {3:F2}) → ({4:F2}, {5:F2}, {6:F2})",
            eyeNifPath, eyeModel.MinX, eyeModel.MinY, eyeModel.MinZ,
            eyeModel.MaxX, eyeModel.MaxY, eyeModel.MaxZ);

        // Eye NIFs are NOT skinned — transform from local space to world space via head bone.
        if (headBoneTransforms != null &&
            headBoneTransforms.TryGetValue(boneName, out var eyeBoneMatrix))
        {
            var t = eyeBoneMatrix.Translation;
            Log.Debug("Transforming eye by {0} matrix: T=({1:F4}, {2:F4}, {3:F4})",
                boneName, t.X, t.Y, t.Z);
            foreach (var sub in eyeModel.Submeshes)
                NpcRenderHelpers.TransformSubmesh(sub, eyeBoneMatrix);
        }
        else
        {
            Log.Warn("'{0}' bone not found — eye will render at origin. Available bones: {1}",
                boneName,
                headBoneTransforms != null
                    ? string.Join(", ", headBoneTransforms.Keys)
                    : "(null)");
        }

        // Apply EGM morphs to eye
        if (usedBaseRaceMesh &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var eyeEgmPath = Path.ChangeExtension(eyeNifPath, ".egm");
            NpcRenderHelpers.LoadAndApplyEgm(eyeEgmPath, eyeModel,
                npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                meshesArchive, meshExtractor, egmCache);
        }

        // Override eye texture from EYES record
        if (npc.EyeTexturePath != null)
        {
            foreach (var sub in eyeModel.Submeshes)
                sub.DiffuseTexturePath = npc.EyeTexturePath;
        }

        // Merge eye submeshes into head model. RenderOrder=2.
        foreach (var sub in eyeModel.Submeshes)
        {
            sub.RenderOrder = 2;
            model.Submeshes.Add(sub);
            model.ExpandBounds(sub.Positions);
        }
    }

    private static void AttachHeadParts(
        NpcAppearance npc, NifRenderableModel model,
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms,
        bool usedBaseRaceMesh,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache)
    {
        foreach (var partPath in npc.HeadPartNifPaths!)
        {
            var partModel = NpcRenderHelpers.LoadNifFromBsa(partPath, meshesArchive, meshExtractor, textureResolver);
            if (partModel == null || !partModel.HasGeometry)
            {
                Log.Warn("Head part NIF failed to load: {0}", partPath);
                continue;
            }

            // Head parts are NOT skinned — apply head bone transform.
            if (attachmentBoneTransforms != null &&
                attachmentBoneTransforms.TryGetValue("Bip01 Head", out var headBone))
            {
                foreach (var sub in partModel.Submeshes)
                    NpcRenderHelpers.TransformSubmesh(sub, headBone);
            }

            // Apply EGM morphs
            if (usedBaseRaceMesh &&
                (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
            {
                var egmPath = Path.ChangeExtension(partPath, ".egm");
                NpcRenderHelpers.LoadAndApplyEgm(egmPath, partModel,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs,
                    meshesArchive, meshExtractor, egmCache);
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
}
