using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

internal static class NpcExportSceneBuilder
{
    private static readonly Logger Log = Logger.Instance;

    internal static NpcExportScene? Build(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        NpcExportSettings settings)
    {
        return settings.HeadOnly
            ? BuildHeadOnlyScene(npc, meshArchives, textureResolver, egmCache, egtCache, settings)
            : BuildFullBodyScene(npc, meshArchives, textureResolver, egmCache, egtCache, settings);
    }

    private static NpcExportScene? BuildFullBodyScene(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        NpcExportSettings settings)
    {
        var skeletonContext = LoadSkeletonContext(npc, meshArchives, settings);
        if (skeletonContext == null)
        {
            return null;
        }

        var scene = skeletonContext.Scene;
        var coveredSlots = 0u;
        if (!settings.NoEquip && npc.EquippedItems != null)
        {
            foreach (var item in npc.EquippedItems)
            {
                coveredSlots |= item.BipedFlags;
            }
        }

        var effectiveBodyTex = npc.BodyTexturePath;
        var effectiveHandTex = npc.HandTexturePath;
        if (!settings.NoEgt && npc.FaceGenTextureCoeffs != null)
        {
            ApplyBodyEgtMorphs(
                npc,
                meshArchives,
                textureResolver,
                egtCache,
                ref effectiveBodyTex,
                ref effectiveHandTex);
        }

        if ((coveredSlots & 0x04) == 0 && npc.UpperBodyNifPath != null)
        {
            AddSkinnedNif(
                scene,
                npc.UpperBodyNifPath,
                meshArchives,
                skeletonContext.NodeIndicesByBoneName,
                submesh =>
                {
                    if (effectiveBodyTex != null &&
                        NpcRenderHelpers.ShouldApplyBodyTextureOverride(submesh.DiffuseTexturePath, effectiveBodyTex))
                    {
                        submesh.DiffuseTexturePath = effectiveBodyTex;
                    }
                });
        }

        if ((coveredSlots & 0x08) == 0 && npc.LeftHandNifPath != null)
        {
            AddSkinnedNif(
                scene,
                npc.LeftHandNifPath,
                meshArchives,
                skeletonContext.NodeIndicesByBoneName,
                submesh =>
                {
                    if (effectiveHandTex != null &&
                        NpcRenderHelpers.ShouldApplyBodyTextureOverride(submesh.DiffuseTexturePath, effectiveHandTex))
                    {
                        submesh.DiffuseTexturePath = effectiveHandTex;
                    }
                });
        }

        if ((coveredSlots & 0x10) == 0 && npc.RightHandNifPath != null)
        {
            AddSkinnedNif(
                scene,
                npc.RightHandNifPath,
                meshArchives,
                skeletonContext.NodeIndicesByBoneName,
                submesh =>
                {
                    if (effectiveHandTex != null &&
                        NpcRenderHelpers.ShouldApplyBodyTextureOverride(submesh.DiffuseTexturePath, effectiveHandTex))
                    {
                        submesh.DiffuseTexturePath = effectiveHandTex;
                    }
                });
        }

        AddBodyEquipment(
            scene,
            npc,
            meshArchives,
            skeletonContext.NodeIndicesByBoneName,
            skeletonContext.BoneTransforms,
            effectiveBodyTex,
            effectiveHandTex,
            settings);
        AddWeapon(scene, npc, meshArchives, textureResolver, skeletonContext, settings);

        var bonelessAttachmentTransform = NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(
            skeletonContext.BoneTransforms,
            skeletonContext.PoseDeltas);
        AddHeadContent(
            scene,
            npc,
            meshArchives,
            textureResolver,
            egmCache,
            egtCache,
            settings,
            skeletonContext.NodeIndicesByBoneName,
            skeletonContext.BoneTransforms,
            bonelessAttachmentTransform);

        return scene.MeshParts.Count > 0 ? scene : null;
    }

    private static NpcExportScene? BuildHeadOnlyScene(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        NpcExportSettings settings)
    {
        if (npc.BaseHeadNifPath == null && npc.FaceGenNifPath == null)
        {
            return null;
        }

        var scene = new NpcExportScene();
        Dictionary<string, int>? nodeIndicesByBoneName = null;
        Dictionary<string, Matrix4x4>? attachmentBoneTransforms = null;
        Matrix4x4? bonelessAttachmentTransform = null;

        if (npc.BaseHeadNifPath != null)
        {
            var headRaw = NpcRenderHelpers.LoadNifRawFromBsa(npc.BaseHeadNifPath, meshArchives);
            if (headRaw != null)
            {
                var headNodes = NifExportExtractor.Extract(headRaw.Value.Data, headRaw.Value.Info);
                nodeIndicesByBoneName = AddNodes(scene, headNodes.Nodes, NpcExportNodeKind.Skeleton);
                attachmentBoneTransforms = headNodes.NamedNodeWorldTransforms;
                bonelessAttachmentTransform = NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(
                    attachmentBoneTransforms,
                    null);
            }
        }

        AddHeadContent(
            scene,
            npc,
            meshArchives,
            textureResolver,
            egmCache,
            egtCache,
            settings,
            nodeIndicesByBoneName,
            attachmentBoneTransforms,
            bonelessAttachmentTransform);

        if (scene.MeshParts.Count == 0 && npc.FaceGenNifPath != null)
        {
            var faceGenModel = NpcRenderHelpers.LoadNifFromBsa(
                npc.FaceGenNifPath,
                meshArchives,
                textureResolver);
            if (faceGenModel != null)
            {
                AddRigidModel(scene, npc.FaceGenNifPath, faceGenModel);
            }
        }

        return scene.MeshParts.Count > 0 ? scene : null;
    }

    private static SkeletonContext? LoadSkeletonContext(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NpcExportSettings settings)
    {
        if (npc.SkeletonNifPath == null)
        {
            return null;
        }

        var skeletonRaw = NpcRenderHelpers.LoadNifRawFromBsa(npc.SkeletonNifPath, meshArchives);
        if (skeletonRaw == null)
        {
            return null;
        }

        var animOverrides = settings.BindPose
            ? null
            : LoadAnimationOverrides(npc.SkeletonNifPath, meshArchives, skeletonRaw.Value, settings.AnimOverride);
        var boneTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skeletonRaw.Value.Data,
            skeletonRaw.Value.Info,
            animOverrides);
        var bindPoseTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skeletonRaw.Value.Data,
            skeletonRaw.Value.Info);
        Dictionary<string, Matrix4x4>? poseDeltas = null;
        if (animOverrides != null)
        {
            poseDeltas = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, posedWorld) in boneTransforms)
            {
                if (!bindPoseTransforms.TryGetValue(name, out var bindWorld) ||
                    !Matrix4x4.Invert(bindWorld, out var inverseBind))
                {
                    continue;
                }

                poseDeltas[name] = inverseBind * posedWorld;
            }
        }

        var extractedSkeleton = NifExportExtractor.Extract(
            skeletonRaw.Value.Data,
            skeletonRaw.Value.Info,
            animOverrides);
        var scene = new NpcExportScene();
        var nodeIndicesByBoneName = AddNodes(scene, extractedSkeleton.Nodes, NpcExportNodeKind.Skeleton);
        return new SkeletonContext(scene, boneTransforms, poseDeltas, nodeIndicesByBoneName);
    }

    private static Dictionary<string, int> AddNodes(
        NpcExportScene scene,
        IEnumerable<NifExportExtractor.ExtractedNode> nodes,
        NpcExportNodeKind kind)
    {
        var nodeList = nodes.ToList();
        var blockToSceneNode = new Dictionary<int, int>();
        foreach (var node in nodeList)
        {
            var parentSceneNode = node.ParentBlockIndex is int parentBlockIndex &&
                                  blockToSceneNode.TryGetValue(parentBlockIndex, out var existingParent)
                ? existingParent
                : scene.RootNodeIndex;
            blockToSceneNode[node.BlockIndex] = scene.AddNode(
                $"{node.Name}_{node.BlockIndex}",
                parentSceneNode,
                node.LocalTransform,
                node.WorldTransform,
                kind,
                node.LookupName);
        }

        return blockToSceneNode
            .Where(entry => nodeList.First(node => node.BlockIndex == entry.Key).LookupName != null)
            .ToDictionary(
                entry => nodeList.First(node => node.BlockIndex == entry.Key).LookupName!,
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AddBodyEquipment(
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
            if (NpcRenderHelpers.IsHeadEquipment(item.BipedFlags))
            {
                continue;
            }

            if (item.AttachmentMode != EquipmentAttachmentMode.None)
            {
                var raw = NpcRenderHelpers.LoadNifRawFromBsa(item.MeshPath, meshArchives);
                if (raw != null && NpcBodyBuilder.IsRigidEquipmentModel(raw.Value.Data, raw.Value.Info))
                {
                    if (NpcBodyBuilder.TryResolveEquipmentAttachmentTransform(
                            item, boneTransforms, out _, out var attachmentTransform, out _))
                    {
                        var extracted = LoadExtractedNif(item.MeshPath, meshArchives);
                        if (extracted != null && extracted.MeshParts.Count > 0)
                        {
                            foreach (var part in extracted.MeshParts)
                            {
                                var submesh = CloneSubmesh(part.Submesh);
                                NpcRenderHelpers.TransformSubmesh(submesh, attachmentTransform);
                                ApplyEquipmentTextureOverride(submesh, effectiveBodyTex, effectiveHandTex);
                                AddRigidSubmesh(scene, item.MeshPath, submesh);
                            }

                            continue;
                        }
                    }
                }
            }

            AddSkinnedNif(
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
        if (effectiveBodyTex != null && NpcRenderHelpers.IsEquipmentSkinSubmesh(submesh.DiffuseTexturePath))
        {
            submesh.DiffuseTexturePath =
                submesh.DiffuseTexturePath?.Contains("hand", StringComparison.OrdinalIgnoreCase) == true
                    ? effectiveHandTex ?? effectiveBodyTex
                    : effectiveBodyTex;
        }
    }

    private static void AddWeapon(
        NpcExportScene scene,
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        SkeletonContext skeletonContext,
        NpcExportSettings settings)
    {
        if (!settings.IncludeWeapon ||
            settings.NoEquip ||
            npc.WeaponVisual?.IsVisible != true ||
            npc.WeaponVisual.MeshPath == null)
        {
            return;
        }

        if (!NpcBodyBuilder.TryResolveWeaponAttachmentNode(npc.WeaponVisual, out var attachmentNodeName, out _))
        {
            return;
        }

        Matrix4x4? attachmentTransform = null;
        NpcBodyBuilder.WeaponHolsterPose? holsterPose = null;
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
                ? NpcBodyBuilder.ResolveWeaponHolsterAttachmentTransform(holsterPose, attachmentNodeName)
                : null;
        }

        if (!attachmentTransform.HasValue)
        {
            return;
        }

        var weaponRaw = NpcRenderHelpers.LoadNifRawFromBsa(npc.WeaponVisual.MeshPath, meshArchives);
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
            if (NpcBodyBuilder.TryResolveModelAttachmentCompensation(
                    weaponRaw.Value.Data,
                    weaponRaw.Value.Info,
                    "Weapon",
                    out var modelAnchorCompensation,
                    out var compensationKind) &&
                NpcBodyBuilder.ShouldApplyWeaponModelAttachmentCompensation(
                    npc.WeaponVisual.AttachmentMode,
                    compensationKind))
            {
                NpcRenderHelpers.TransformModel(weaponModel, modelAnchorCompensation);
            }

            NpcRenderHelpers.TransformModel(weaponModel, attachmentTransform.Value);
            AddRigidModel(scene, npc.WeaponVisual.MeshPath, weaponModel);
        }

        if (npc.WeaponVisual.AttachmentMode != WeaponAttachmentMode.HolsterPose ||
            holsterAttachmentGroups.Count == 0 ||
            holsterPose == null)
        {
            return;
        }

        var allShapeIndices = NpcBodyBuilder.FindShapeBlockIndices(weaponRaw.Value.Data, weaponRaw.Value.Info);
        foreach (var group in holsterAttachmentGroups)
        {
            var groupAttachmentTransform = NpcBodyBuilder.ResolveWeaponHolsterAttachmentTransform(
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
            AddRigidModel(scene, $"{npc.WeaponVisual.MeshPath}:{group.SourceNodeName}", groupModel);
        }
    }

    private static void AddHeadContent(
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
            var extracted = LoadExtractedNif(
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
                        AddSkinnedPart(scene, part, nodeIndicesByBoneName);
                    }
                    else
                    {
                        AddExtractedRigidPart(scene, part, part.ShapeWorldTransform, npc.BaseHeadNifPath);
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

        var hairFilter = settings.NoEquip
            ? null
            : NpcRenderHelpers.HasHatEquipment(npc.EquippedItems)
                ? "Hat"
                : null;
        AddRaceFaceParts(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
            attachmentBoneTransforms, bonelessAttachmentTransform);
        AddHair(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
            hairFilter, attachmentBoneTransforms, bonelessAttachmentTransform);
        AddHeadParts(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
            attachmentBoneTransforms, bonelessAttachmentTransform);
        AddEyes(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
            attachmentBoneTransforms, bonelessAttachmentTransform);
        AddHeadEquipment(scene, npc, meshArchives, textureResolver, egmCache, usedBaseRaceMesh,
            nodeIndicesByBoneName, attachmentBoneTransforms, bonelessAttachmentTransform, settings);
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

        var hairRaw = NpcRenderHelpers.LoadNifRawFromBsa(npc.HairNifPath, meshArchives);
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
            NpcRenderHelpers.LoadAndApplyEgm(
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

        var tint = NpcRenderHelpers.UnpackHairColor(npc.HairColor);
        foreach (var submesh in hairModel.Submeshes)
        {
            submesh.TintColor = tint;
            submesh.IsDoubleSided = true;
            if (npc.HairTexturePath != null)
            {
                submesh.DiffuseTexturePath = npc.HairTexturePath;
            }
        }

        AddRigidModel(scene, npc.HairNifPath, hairModel);
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

            var partRaw = NpcRenderHelpers.LoadNifRawFromBsa(facePartPath, meshArchives);
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
                NpcRenderHelpers.LoadAndApplyEgm(
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

            AddRigidModel(scene, facePartPath, partModel);
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
            var partRaw = NpcRenderHelpers.LoadNifRawFromBsa(headPartPath, meshArchives);
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
                NpcRenderHelpers.LoadAndApplyEgm(
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

            var tint = NpcRenderHelpers.UnpackHairColor(npc.HairColor);
            foreach (var submesh in partModel.Submeshes)
            {
                submesh.TintColor = tint;
                submesh.IsDoubleSided = true;
            }

            AddRigidModel(scene, headPartPath, partModel);
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

            var eyeRaw = NpcRenderHelpers.LoadNifRawFromBsa(eyePath, meshArchives);
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
                NpcRenderHelpers.LoadAndApplyEgm(
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

            AddRigidModel(scene, eyePath, eyeModel);
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
        NpcExportSettings settings)
    {
        if (settings.NoEquip || npc.EquippedItems == null)
        {
            return;
        }

        foreach (var item in npc.EquippedItems.Where(item => NpcRenderHelpers.IsHeadEquipment(item.BipedFlags)))
        {
            var raw = NpcRenderHelpers.LoadNifRawFromBsa(item.MeshPath, meshArchives);
            if (raw == null)
            {
                continue;
            }

            var hasSkinning =
                raw.Value.Info.Blocks.Any(block => block.TypeName is "NiSkinInstance" or "BSDismemberSkinInstance");
            if (hasSkinning && nodeIndicesByBoneName != null)
            {
                AddSkinnedNif(scene, item.MeshPath, meshArchives, nodeIndicesByBoneName);
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
                NpcRenderHelpers.LoadAndApplyEgm(
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

            AddRigidModel(scene, item.MeshPath, model);
        }
    }

    private static void AddSkinnedNif(
        NpcExportScene scene,
        string nifPath,
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, int> nodeIndicesByBoneName,
        Action<RenderableSubmesh>? mutateSubmesh = null,
        string? filterShapeName = null,
        float[]? preSkinMorphDeltas = null)
    {
        var extracted = LoadExtractedNif(
            nifPath,
            meshArchives,
            filterShapeName,
            preSkinMorphDeltas);
        if (extracted == null)
        {
            return;
        }

        foreach (var part in extracted.MeshParts)
        {
            mutateSubmesh?.Invoke(part.Submesh);
            if (part.Skin != null)
            {
                AddSkinnedPart(scene, part, nodeIndicesByBoneName);
            }
            else
            {
                AddExtractedRigidPart(scene, part, part.ShapeWorldTransform, nifPath);
            }
        }
    }

    private static void AddSkinnedPart(
        NpcExportScene scene,
        NifExportExtractor.ExtractedMeshPart part,
        Dictionary<string, int> nodeIndicesByBoneName)
    {
        var jointNodeIndices = new int[part.Skin!.BoneNames.Length];
        for (var index = 0; index < part.Skin.BoneNames.Length; index++)
        {
            if (!nodeIndicesByBoneName.TryGetValue(part.Skin.BoneNames[index], out var jointNodeIndex))
            {
                Log.Warn("Skipping skinned mesh '{0}': missing joint '{1}'", part.Name, part.Skin.BoneNames[index]);
                return;
            }

            jointNodeIndices[index] = jointNodeIndex;
        }

        scene.MeshParts.Add(new NpcExportMeshPart
        {
            Name = part.Name,
            Submesh = CloneSubmesh(part.Submesh),
            Skin = new NpcExportSkinBinding
            {
                JointNodeIndices = jointNodeIndices,
                InverseBindMatrices = part.Skin.InverseBindMatrices,
                PerVertexInfluences = part.Skin.PerVertexInfluences
            }
        });
    }

    private static void AddExtractedRigidPart(
        NpcExportScene scene,
        NifExportExtractor.ExtractedMeshPart part,
        Matrix4x4 worldTransform,
        string label)
    {
        var rigidSubmesh = CloneSubmesh(part.Submesh);
        NpcRenderHelpers.TransformSubmesh(rigidSubmesh, worldTransform);
        AddRigidSubmesh(scene, label, rigidSubmesh);
    }

    private static void AddRigidModel(NpcExportScene scene, string label, NifRenderableModel model)
    {
        foreach (var submesh in model.Submeshes)
        {
            AddRigidSubmesh(scene, label, CloneSubmesh(submesh));
        }
    }

    private static void AddRigidSubmesh(NpcExportScene scene, string label, RenderableSubmesh submesh)
    {
        var nodeIndex = scene.AddNode(
            $"{Path.GetFileNameWithoutExtension(label)}_{scene.MeshParts.Count}",
            scene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            NpcExportNodeKind.Attachment);
        scene.MeshParts.Add(new NpcExportMeshPart
        {
            Name = Path.GetFileNameWithoutExtension(label),
            NodeIndex = nodeIndex,
            Submesh = submesh
        });
    }

    private static NifExportExtractor.ExtractedScene? LoadExtractedNif(
        string nifPath,
        NpcMeshArchiveSet meshArchives,
        string? filterShapeName = null,
        float[]? preSkinMorphDeltas = null)
    {
        var raw = NpcRenderHelpers.LoadNifRawFromBsa(nifPath, meshArchives);
        return raw == null
            ? null
            : NifExportExtractor.Extract(raw.Value.Data, raw.Value.Info, filterShapeName: filterShapeName,
                preSkinMorphDeltas: preSkinMorphDeltas);
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
        var egm = NpcRenderHelpers.LoadAndCacheEgm(egmPath, meshArchives, egmCache);
        return egm == null
            ? null
            : FaceGenMeshMorpher.ComputeAccumulatedDeltas(
                egm,
                npc.FaceGenSymmetricCoeffs,
                npc.FaceGenAsymmetricCoeffs,
                egm.VertexCount);
    }

    private static void ApplyBodyEgtMorphs(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache,
        ref string? effectiveBodyTex,
        ref string? effectiveHandTex)
    {
        if (npc.BodyEgtPath != null && npc.BodyTexturePath != null && npc.FaceGenTextureCoeffs != null)
        {
            effectiveBodyTex = NpcRenderHelpers.ApplyBodyEgtMorph(
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
            effectiveHandTex = NpcRenderHelpers.ApplyBodyEgtMorph(
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
            _ = NpcRenderHelpers.ApplyBodyEgtMorph(
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
            egt = NpcRenderHelpers.LoadEgtFromBsa(egtPath, meshArchives);
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

        var textureKey = NpcRenderHelpers.BuildNpcFaceEgtTextureKey(npc);
        textureResolver.InjectTexture(textureKey, morphedTexture);
        return textureKey;
    }

    private static Dictionary<string, NifAnimationParser.AnimPoseOverride>? LoadAnimationOverrides(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        (byte[] Data, NifInfo Info) skeletonRaw,
        string? animOverride)
    {
        var skeletonDirectory = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(animOverride))
        {
            var customRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonDirectory + animOverride, meshArchives, true);
            if (customRaw != null)
            {
                return NifAnimationParser.ParseIdlePoseOverrides(customRaw.Value.Data, customRaw.Value.Info);
            }
        }

        var idleRaw = NpcRenderHelpers.LoadNifRawFromBsa(
            skeletonDirectory + "locomotion\\mtidle.kf",
            meshArchives,
            true);
        if (idleRaw == null &&
            skeletonDirectory.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            idleRaw = NpcRenderHelpers.LoadNifRawFromBsa(
                skeletonDirectory.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase) +
                "locomotion\\mtidle.kf",
                meshArchives,
                true);
        }

        return idleRaw != null
            ? NifAnimationParser.ParseIdlePoseOverrides(idleRaw.Value.Data, idleRaw.Value.Info)
            : NifAnimationParser.ParseIdlePoseOverrides(skeletonRaw.Data, skeletonRaw.Info);
    }

    private static NpcBodyBuilder.WeaponHolsterPose? LoadHolsterPose(
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
        var holsterRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonDirectory + kfRelPath, meshArchives, true);
        if (holsterRaw == null &&
            skeletonDirectory.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            holsterRaw = NpcRenderHelpers.LoadNifRawFromBsa(
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

        var skeletonRaw = NpcRenderHelpers.LoadNifRawFromBsa(npc.SkeletonNifPath, meshArchives);
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
        var parentOverrideBone = NpcBodyBuilder.TryParseSequenceParentBoneName(holsterRaw.Value.Info);
        return new NpcBodyBuilder.WeaponHolsterPose(
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

    private static RenderableSubmesh CloneSubmesh(RenderableSubmesh submesh)
    {
        return new RenderableSubmesh
        {
            ShapeName = submesh.ShapeName,
            Positions = (float[])submesh.Positions.Clone(),
            Triangles = (ushort[])submesh.Triangles.Clone(),
            Normals = submesh.Normals != null ? (float[])submesh.Normals.Clone() : null,
            UVs = submesh.UVs != null ? (float[])submesh.UVs.Clone() : null,
            VertexColors = submesh.VertexColors != null ? (byte[])submesh.VertexColors.Clone() : null,
            Tangents = submesh.Tangents != null ? (float[])submesh.Tangents.Clone() : null,
            Bitangents = submesh.Bitangents != null ? (float[])submesh.Bitangents.Clone() : null,
            ShaderMetadata = submesh.ShaderMetadata,
            DiffuseTexturePath = submesh.DiffuseTexturePath,
            NormalMapTexturePath = submesh.NormalMapTexturePath,
            IsEmissive = submesh.IsEmissive,
            UseVertexColors = submesh.UseVertexColors,
            IsDoubleSided = submesh.IsDoubleSided,
            HasAlphaBlend = submesh.HasAlphaBlend,
            HasAlphaTest = submesh.HasAlphaTest,
            AlphaTestThreshold = submesh.AlphaTestThreshold,
            AlphaTestFunction = submesh.AlphaTestFunction,
            SrcBlendMode = submesh.SrcBlendMode,
            DstBlendMode = submesh.DstBlendMode,
            MaterialAlpha = submesh.MaterialAlpha,
            MaterialGlossiness = submesh.MaterialGlossiness,
            IsEyeEnvmap = submesh.IsEyeEnvmap,
            EnvMapScale = submesh.EnvMapScale,
            TintColor = submesh.TintColor
        };
    }

    private sealed record SkeletonContext(
        NpcExportScene Scene,
        Dictionary<string, Matrix4x4> BoneTransforms,
        Dictionary<string, Matrix4x4>? PoseDeltas,
        Dictionary<string, int> NodeIndicesByBoneName);
}
