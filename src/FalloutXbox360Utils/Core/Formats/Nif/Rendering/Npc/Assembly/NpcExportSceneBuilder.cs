using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Orchestrates NPC export scene construction. Body assembly lives in
///     <see cref="NpcExportBodyAssembler" /> and head assembly in
///     <see cref="NpcExportHeadAssembler" />.
/// </summary>
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
        var compositionCaches = new NpcCompositionCaches(
            egmCache,
            egtCache,
            new Dictionary<string, NpcCompositionCaches.CachedNpcSkeletonPlan?>(
                StringComparer.OrdinalIgnoreCase));
        var plan = NpcCompositionPlanner.CreatePlan(
            npc,
            meshArchives,
            textureResolver,
            compositionCaches,
            NpcCompositionOptions.From(settings));
        return NpcCompositionExportAdapter.BuildNpc(plan, meshArchives, textureResolver, compositionCaches);
    }

    internal static Dictionary<string, int> AddNodes(
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

    internal static void AddSkinnedNif(
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

    internal static void AddSkinnedPart(
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

    internal static void AddExtractedRigidPart(
        NpcExportScene scene,
        NifExportExtractor.ExtractedMeshPart part,
        Matrix4x4 worldTransform,
        string label)
    {
        var rigidSubmesh = CloneSubmesh(part.Submesh);
        NpcRenderHelpers.TransformSubmesh(rigidSubmesh, worldTransform);
        AddRigidSubmesh(scene, label, rigidSubmesh);
    }

    internal static void AddRigidModel(NpcExportScene scene, string label, NifRenderableModel model)
    {
        foreach (var submesh in model.Submeshes)
        {
            AddRigidSubmesh(scene, label, CloneSubmesh(submesh));
        }
    }

    internal static void AddRigidSubmesh(NpcExportScene scene, string label, RenderableSubmesh submesh)
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

    internal static NifExportExtractor.ExtractedScene? LoadExtractedNif(
        string nifPath,
        NpcMeshArchiveSet meshArchives,
        string? filterShapeName = null,
        float[]? preSkinMorphDeltas = null)
    {
        var raw = NpcMeshHelpers.LoadNifRawFromBsa(nifPath, meshArchives);
        return raw == null
            ? null
            : NifExportExtractor.Extract(raw.Value.Data, raw.Value.Info, filterShapeName: filterShapeName,
                preSkinMorphDeltas: preSkinMorphDeltas);
    }

    internal static RenderableSubmesh CloneSubmesh(RenderableSubmesh submesh)
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

    internal sealed record SkeletonContext(
        NpcExportScene Scene,
        Dictionary<string, Matrix4x4> BoneTransforms,
        Dictionary<string, Matrix4x4>? PoseDeltas,
        Dictionary<string, int> NodeIndicesByBoneName);
}
