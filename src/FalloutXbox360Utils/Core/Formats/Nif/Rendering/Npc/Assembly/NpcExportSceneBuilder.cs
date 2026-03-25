using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Orchestrates NPC export scene construction. Body assembly lives in
///     <see cref="NpcExportSceneBuilder"/> (NpcExportBodyAssembler.cs) and head assembly
///     in NpcExportHeadAssembler.cs, both as partial class extensions.
/// </summary>
internal static partial class NpcExportSceneBuilder
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
                : NpcExportScene.RootNodeIndex;
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
            NpcExportScene.RootNodeIndex,
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
